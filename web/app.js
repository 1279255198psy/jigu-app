// FILE: jigu-app/web/app.js
// 稽古 · 界面层（无任何检索计算）
//
// 职责边界（严格执行）：
//   · 前端只做三件事：收集用户输入、交给 C# 宿主、把返回的 JSON 渲染出来。
//   · 分词、同义词、IDF 加权全部在 C# 完成（Corpus.cs / Update.cs）。
//   · 不引入任何检索/分词库，不做本地计算，避免前端内存增长与脚本报错。
(function () {
  "use strict";
  var timers = [], lastQuery = "", busy = false, lastRendered = "", LIB_PAGE = 20, libPage = 0;

  function $(id) { return (document && document.getElementById) ? document.getElementById(id) : null; }

  function later(fn, ms) {
    var id = window.setTimeout(function () {
      for (var i = 0; i < timers.length; i++) { if (timers[i] === id) { timers.splice(i, 1); break; } }
      try { fn(); } catch (e) { logFromJs("timer", errText(e)); }
    }, ms);
    timers.push(id);
    return id;
  }
  function clearAllTimers() {
    for (var i = 0; i < timers.length; i++) { try { window.clearTimeout(timers[i]); } catch (e) { } }
    timers = [];
  }
  function ready(fn) {
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fn, { once: true });
    else fn();
  }
  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = text;
    return n;
  }
  function setStatus(text, kind) {
    var bar = $("status");
    if (!bar) return;
    bar.textContent = "";
    bar.appendChild(el("span", "pulse " + (kind || "")));
    bar.appendChild(el("span", null, String(text === undefined || text === null ? "" : text)));
  }

  // ---------- 错误一律转人话 ----------
  function friendly(t) {
    var s = String(t === undefined || t === null ? "" : t);
    if (!s) return "运行中出现未知问题";
    if (s.indexOf("remoteObjectId") >= 0 || s.indexOf("0x80070490") >= 0
        || s.indexOf("NotFound") >= 0 || s.indexOf("找不到元素") >= 0)
      return "页面尚未就绪，已跳过本次操作";
    if (s.charAt(0) === "{" || s.charAt(0) === "[") return "操作未完成，请稍后重试";
    return s.length > 160 ? s.slice(0, 160) + "…" : s;
  }
  function errText(v) {
    if (v === null || v === undefined) return "未知错误";
    if (typeof v === "string") return friendly(v);
    if (typeof v === "number" || typeof v === "boolean") return String(v);
    try {
      if (v instanceof Error) return friendly(v.name ? v.name + ": " + v.message : v.message);
      if (typeof v.message === "string" && v.message) return friendly(v.message);
      if (v.reason !== undefined) return errText(v.reason);
      var j = JSON.stringify(v);
      if (typeof j === "string" && j !== "{}" && j !== "null") return friendly(j);
    } catch (e) { }
    return "运行中出现未知问题";
  }
  function logFromJs(where, detail) {
    try {
      var b = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects;
      if (b && b.host && b.host.LogFromJs) b.host.LogFromJs(String(where), String(detail));
    } catch (e) { }
  }
  function reportError(where, v) {
    var t = friendly(errText(v));
    logFromJs(where, t);
    setStatus(t, "warn");
  }

  // ---------- 宿主桥 ----------
  function host() {
    try {
      var b = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects;
      return (b && b.host) ? b.host : null;
    } catch (e) { return null; }
  }
  function call(name, args) {
    return new Promise(function (resolve) {
      var h = host();
      if (!h || typeof h[name] !== "function") { resolve(null); return; }
      try {
        Promise.resolve(h[name].apply(h, args || [])).then(resolve, function (e) {
          logFromJs("call " + name, errText(e));
          resolve(null);
        });
      } catch (e) { logFromJs("call " + name, errText(e)); resolve(null); }
    });
  }
  function callJson(name, args) {
    return call(name, args).then(function (raw) {
      if (typeof raw !== "string" || !raw) return null;
      try { return JSON.parse(raw); } catch (e) { reportError("parse " + name, e); return null; }
    });
  }

  // ---------- 离线兜底（页面被系统浏览器直接打开时没有宿主桥）----------
  // 仅在完全没有 window.chrome.webview 时启用；WebView2 承载时这段永远不执行。
  var LOCAL = null;
  function loadLocalCorpus() {
    if (LOCAL) return Promise.resolve(LOCAL);
    return fetch("corpus.json").then(function (r) { return r.json(); }).then(function (data) {
      var docs = [];
      (data.books || []).forEach(function (b) {
        (b.items || []).forEach(function (it) { it.__book = b.book || ""; docs.push(it); });
      });
      if (!docs.length && data.items) {
        data.items.forEach(function (it) { it.__book = it.book || ""; docs.push(it); });
      }
      var index = {};
      docs.forEach(function (d, i) {
        var text = [d.book, d.chapter, d.title, d.original, d.translation,
          d.decision, d.outcome, (d.figures || []).join(""), (d.themes || []).join("")].join("");
        var n = text.length, seen = {};
        for (var j = 0; j + 2 <= n; j++) {
          var t = text.substr(j, 2);
          if (seen[t]) continue;
          seen[t] = 1;
          (index[t] || (index[t] = [])).push(i);
        }
      });
      LOCAL = { docs: docs, index: index };
      return LOCAL;
    });
  }

  function localSearch(query, topK) {
    return loadLocalCorpus().then(function (C) {
      var terms = {}, n = query.length, j;
      for (j = 0; j + 2 <= n; j++) terms[query.substr(j, 2)] = 1;
      var score = {}, total = Math.max(C.docs.length, 1);
      Object.keys(terms).forEach(function (t) {
        var list = C.index[t];
        if (!list) return;
        var idf = Math.log((total + 1) / (list.length + 1)) + 1;
        list.forEach(function (d) { score[d] = (score[d] || 0) + idf; });
      });
      var ranked = Object.keys(score).map(function (d) { return { i: +d, s: score[d] }; });
      ranked.sort(function (a, b) { return b.s - a.s || a.i - b.i; });
      var hits = ranked.slice(0, topK || 3).map(function (r) {
        var d = C.docs[r.i];
        return {
          book: d.__book || "", chapter: d.chapter || "", title: d.title || "",
          original: d.original || "", translation: d.translation || "",
          figures: d.figures || [], decision: d.decision || "",
          outcome: d.outcome || "", score: Math.round(r.s * 10) / 10,
          themes: d.themes || [], pros: d.pros || [], cons: d.cons || []
        };
      });
      return { hits: hits, terms: Object.keys(terms), elapsedMs: 0, local: true };
    });
  }

  function localStats() {
    return loadLocalCorpus().then(function (C) {
      var books = {};
      C.docs.forEach(function (d) { var b = d.__book || ""; books[b] = (books[b] || 0) + 1; });
      return { docs: C.docs.length, terms: Object.keys(C.index).length, books: books };
    });
  }
  function fallbackCall(name, args) {
    if (name === "Search") return localSearch(String(args[0] || ""), args[1] || 3);
    if (name === "ExplainSearch") {
      var q = String(args[0] || ""), t = {}, i;
      for (i = 0; i + 2 <= q.length; i++) t[q.substr(i, 2)] = 1;
      return Promise.resolve({ normalized: q, terms: Object.keys(t) });
    }
    if (name === "CorpusStats") return localStats();
    if (name === "ConsumeDataUpdated") return Promise.resolve(false);
    // 离线（页面被系统浏览器直接打开）时本机没有更新服务：如实回答，不要假装"已就绪"
    if (name === "GetUpdateState") return Promise.resolve({ ready: false, phase: "idle", snoozed: false });
    if (name === "ApplyReadyUpdate") {
      return Promise.resolve({ updating: false, message: "离线模式下没有可用的更新服务。" });
    }
    // version 留空：这条路径根本没有宿主可问，报一个写死的版本号只会误导用户
    // （以前写死 "1.6.0"，而程序版本早就不是它了）。界面对空值有专门的分支。
    if (name === "GetSnapshot") return loadLocalCorpus().then(function (C) {
      return { version: "", local: true, docs: C.docs.length };
    }, function () {
      return { version: "", local: true, docs: 0 };
    });
    return Promise.resolve(null);
  }
  // 桥不可用时自愈：宿主对象没注册成功时，WebView2 的 hostObjects.host 依然返回一个
  // 真值代理（不是 undefined），所以「host() 非空」并不能证明桥可用 —— 只能靠真实调用
  // 的结果判断。调用返回 null 即视为桥不可用，改走离线实现。
  function callJsonAuto(name, args) {
    if (!host()) return fallbackCall(name, args);
    return callJson(name, args).then(function (data) {
      if (data !== null) return data;
      logFromJs("bridge unavailable, falling back to offline " + name, "");
      return fallbackCall(name, args);
    });
  }

  // ---------- 渲染 ----------
  function chipList(terms) {
    var wrap = el("div", "chips");
    var list = (terms || []).slice(0, 10);
    for (var i = 0; i < list.length; i++) {
      var t = String(list[i]);
      wrap.appendChild(el("span", t.length >= 3 ? "chip chip-term" : "chip", t));
    }
    return wrap;
  }
  function block(title, body) {
    var sec = el("section", "block");
    sec.appendChild(el("h4", null, title));
    sec.appendChild(body);
    return sec;
  }
  function sealCorner() {
    var s = el("span", "seal-corner", "稽");
    s.title = "稽古";
    return s;
  }
  // 决策分析栏：利/弊正文是随语料写好的（离线、不调用 AI），只有「与你的处境对应」
  // 这一行是运行时算的 —— 看这条史料的哪些情境词原样出现在用户输入里。
  function analysisBlock(hit, query) {
    var pros = hit.pros || [], cons = hit.cons || [];
    if (!pros.length && !cons.length) return null;
    var wrap = el("section", "block analysis");
    wrap.appendChild(el("h4", null, "决策分析"));
    var q = String(query || "");
    var themes = hit.themes || [];
    if (themes.length) {
      var lead = el("p", "analysis-lead");
      lead.appendChild(el("span", "muted", q ? "与你的处境对应：" : "本条情境："));
      var chips = el("span", "chips inline");
      for (var i = 0; i < themes.length; i++) {
        var t = String(themes[i]);
        var on = q.length >= 2 && q.indexOf(t) >= 0;
        var c = el("span", on ? "chip chip-on" : "chip chip-off", on ? "✓ " + t : t);
        if (on) c.title = "你的描述里出现了这个词";
        chips.appendChild(c);
      }
      lead.appendChild(chips);
      wrap.appendChild(lead);
    }
    var grid = el("div", "grid-2");
    var pu = el("ul", "pros");
    if (pros.length) for (var p = 0; p < pros.length; p++) pu.appendChild(el("li", null, pros[p]));
    else pu.appendChild(el("li", "muted", "未标注"));
    var cu = el("ul", "cons");
    if (cons.length) for (var n = 0; n < cons.length; n++) cu.appendChild(el("li", null, cons[n]));
    else cu.appendChild(el("li", "muted", "未标注"));
    grid.appendChild(block("这么做的好处", pu));
    grid.appendChild(block("这么做要付的代价", cu));
    wrap.appendChild(grid);
    wrap.appendChild(el("p", "analysis-note",
      "这两栏是史事本身的条件与代价，不是结论。哪一边更重，取决于你的实际情况与上面那几个词是否成立。"));
    return wrap;
  }
  function renderHit(hit, index, query) {
    var card = el("article", "hit-card");
    var head = el("header", "hit-head");
    head.appendChild(el("span", "rank", "#" + (index + 1)));
    var tb = el("div", "hit-title");
    tb.appendChild(el("h3", null, hit.title || hit.chapter || "（无标题）"));
    tb.appendChild(el("p", "hit-sub", (hit.book || "史料") + "·" + (hit.chapter || "")));
    head.appendChild(tb);
    if (typeof hit.score === "number") {
      var sc = el("div", "score", hit.score.toFixed(2));
      sc.title = "本地加权得分";
      head.appendChild(sc);
    }
    card.appendChild(head);
    if (hit.terms && hit.terms.length) card.appendChild(chipList(hit.terms));
    card.appendChild(block("原文摘录", el("blockquote", "quote", hit.original || "（未收录原文）")));
    card.appendChild(block("现代文翻译", el("p", "prose", hit.translation || "（该条暂无白话译文）")));
    var grid = el("div", "grid-2");
    var figs = el("ul", "figures");
    if (hit.figures && hit.figures.length) {
      for (var i = 0; i < hit.figures.length; i++) figs.appendChild(el("li", null, hit.figures[i]));
    } else figs.appendChild(el("li", "muted", "未标注"));
    grid.appendChild(block("核心人物", figs));
    grid.appendChild(block("关键决策", el("p", "prose", hit.decision || "未标注")));
    card.appendChild(grid);
    card.appendChild(block("最终结果", el("p", "prose outcome", hit.outcome || "未标注")));
    var an = analysisBlock(hit, query);
    if (an) card.appendChild(an);
    card.appendChild(sealCorner());
    return card;
  }
  function renderResults(data, query) {
    var box = $("results");
    if (!box) return;
    if (query && query === lastRendered && box.childNodes.length > 0) return;
    lastRendered = query || "";
    box.textContent = "";
    var hits = (data && data.hits) ? data.hits : [];
    var took = (data && typeof data.tookMs === "number") ? data.tookMs : 0;
    box.appendChild(el("p", "took", "本地检索 " + took + " 毫秒"));
    if (!hits.length) {
      box.appendChild(el("p", "empty", "未找到相似的史事，可以换一种说法，或补充具体情境。"));
      return;
    }
    var head = el("div", "section-title");
    head.appendChild(el("h2", null, "镜 · 最相似的历史事件"));
    head.appendChild(el("span", "hint",
      (data && data.local) ? "离线兜底引擎检索所得（精度低于内置界面）" : "本机 C# 引擎检索所得"));
    box.appendChild(head);
    for (var i = 0; i < hits.length; i++) box.appendChild(renderHit(hits[i], i, query));
  }
  function renderExplain(data) {
    var box = $("explain-body");
    if (!box) return;
    box.textContent = "";
    if (!data) return;
    box.appendChild(el("p", null, "规范化：" + (data.normalized || "—")));
    box.appendChild(el("p", null, "词元（C# 生成）：" + ((data.terms && data.terms.length) ? data.terms.join("、") : "—")));
  }

  // ---------- 检索 ----------
  function inkBurst(btn) {
    if (!btn) return;
    btn.classList.remove("inking");
    void btn.offsetWidth;
    btn.classList.add("inking");
    later(function () { if (btn) btn.classList.remove("inking"); }, 800);
  }
  // index.html 里 #go 自带 disabled，等引擎备好书再放开。
  // 此前只有示例按钮和 Ctrl+Enter 能触发检索：新用户第一次点「稽古一问」毫无反应。
  function enableSearch() {
    var btn = $("go");
    if (btn) { btn.disabled = false; btn.textContent = "稽古一问"; }
  }
  function doSearch(text) {
    var ta = $("situation"), btn = $("go");
    var query = String(text === undefined ? (ta ? ta.value : "") : text).trim();
    if (!query) { setStatus("请先写下你的处境。", "warn"); return; }
    if (busy) return;
    lastQuery = query;
    if (ta) ta.value = query;
    busy = true;
    if (btn) { btn.disabled = true; btn.textContent = "正在检索…"; }
    inkBurst(btn);
    setStatus("正在检索…");
    var started = Date.now();
    Promise.all([callJsonAuto("Search", [query, 3]), callJsonAuto("ExplainSearch", [query])])
      .then(function (res) {
        var data = res[0];
        if (!data) { setStatus("检索未能完成，请稍后重试。", "warn"); return; }
        if (data.error) { setStatus(friendly(data.error), "warn"); return; }
        renderResults(data, query);
        renderExplain(res[1]);
        var ew = $("explain-wrap");
        if (ew) ew.style.display = "block";
        var cost = (typeof data.tookMs === "number") ? data.tookMs : (Date.now() - started);
        setStatus("检得 " + ((data.hits && data.hits.length) || 0) + " 则 · 耗时 " + cost + " ms", "ready");
      })
      .then(function () {
        busy = false;
        if (btn) { btn.disabled = false; btn.textContent = "稽古一问"; }
      }, function (e) {
        busy = false;
        if (btn) { btn.disabled = false; btn.textContent = "稽古一问"; }
        reportError("search", e);
      });
  }

  // ---------- 藏书阁 ----------
  function renderLibrary(stats, snapshot) {
    var box = $("library");
    if (!box) return;
    box.textContent = "";
    if (!stats) { box.appendChild(el("p", "muted", "尚未读取书库信息。")); return; }
    box.appendChild(el("p", "prose", "共 " + (stats.docs || 0) + " 则史料 · 索引词 " + (stats.terms || 0)
      + " · 索引约 " + (stats.indexKB || 0) + " KB（内存只保存词表，正文按需读取）"));
    var books = [];
    for (var k in stats.books) if (Object.prototype.hasOwnProperty.call(stats.books, k)) books.push(k);
    books.sort();
    var pages = Math.max(1, Math.ceil(books.length / LIB_PAGE));
    if (libPage >= pages) libPage = pages - 1;
    if (libPage < 0) libPage = 0;
    var table = el("table", "books");
    var hr = el("tr");
    hr.appendChild(el("th", null, "史料"));
    hr.appendChild(el("th", null, "条数"));
    var thead = el("thead");
    thead.appendChild(hr);
    table.appendChild(thead);
    var tbody = el("tbody");
    var from = libPage * LIB_PAGE, to = Math.min(books.length, from + LIB_PAGE);
    for (var i = from; i < to; i++) {
      var tr = el("tr");
      tr.appendChild(el("td", null, books[i]));
      tr.appendChild(el("td", null, String(stats.books[books[i]])));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    box.appendChild(table);
    if (pages > 1) {
      var pager = el("div", "pager");
      var prev = el("button", "ghost", "上一页"), next = el("button", "ghost", "下一页");
      prev.disabled = libPage <= 0;
      next.disabled = libPage >= pages - 1;
      prev.onclick = function () { libPage--; renderLibrary(stats, snapshot); };
      next.onclick = function () { libPage++; renderLibrary(stats, snapshot); };
      pager.appendChild(prev);
      pager.appendChild(el("span", "hint", " " + (libPage + 1) + " / " + pages + " "));
      pager.appendChild(next);
      box.appendChild(pager);
    }
    if (snapshot) {
      box.appendChild(el("p", "hint", "系统：" + (snapshot.os || "—")
        + " · 界面内核 WebView2：" + (snapshot.webview2 ? "正常" : "缺失")
        + (snapshot.version ? " · 程序版本 v" + snapshot.version : " · 程序版本未知")));
      box.appendChild(el("p", "hint", "史料文件：" + (stats.source || "—")));
    }
  }
  function refreshLibrary() {
    return Promise.all([callJsonAuto("CorpusStats"), callJsonAuto("GetSnapshot")])
      .then(function (r) { renderLibrary(r[0], r[1]); });
  }

  // ---------- 更新 ----------
  function showReport(t) { var b = $("update-report"); if (b) b.textContent = t; }
  function reportText(r, what) {
    if (!r) return "更新服务暂时不可用。";
    if (typeof r.message === "string" && r.message) return r.message;
    return what + "检查完成。";
  }
  function checkData() {
    showReport("正在检查史料更新…");
    callJson("CheckDataUpdate").then(function (r) { showReport(reportText(r, "史料")); });
  }
  function applyData() {
    showReport("正在下载并合并史料…");
    callJson("ApplyDataUpdate").then(function (r) {
      showReport(reportText(r, "史料"));
      if (r && r.mergedCount > 0) refreshLibrary();
    });
  }
  // 程序更新是「后台静默下载，下好了才提示重启」，所以界面这里不发起下载，
  // 只渲染宿主的状态：ready 表示包已经躺在本机，重启即生效。
  var updateState = null;      // 最近一次 GetUpdateState 的结果
  var announcedVersion = "";   // 同一个版本的就绪横幅只播报一次，别每分钟刷一遍

  function applyUpdateState(s) {
    updateState = (s && typeof s === "object") ? s : null;
    var ready = !!(updateState && updateState.ready);
    var snoozed = !!(updateState && updateState.snoozed);
    var btn = $("apply-app"), snz = $("snooze-app");
    if (btn) btn.textContent = ready ? "重启并更新" : "立即更新";
    if (snz) snz.style.display = (ready && !snoozed) ? "" : "none";
    if (!updateState) return;

    if (ready && !snoozed) {
      if (announcedVersion === updateState.remoteVersion) return;
      announcedVersion = updateState.remoteVersion;
      var t = "新版本 v" + (updateState.remoteVersion || "?") + " 已下载完成，重启后生效。";
      if (updateState.notes) t += "\n" + updateState.notes;
      showReport(t);
    } else if (updateState.phase === "downloading") {
      showReport("正在后台下载新版本…"
        + (updateState.progress > 0 ? "（" + updateState.progress + "%）" : ""));
    }
    // 其余情况不动报告区：那是用户上一次手动操作留下的结果，不该被轮询擦掉
  }

  function checkApp() {
    showReport("正在检查程序更新…");
    callJson("CheckAppUpdate").then(function (r) {
      showReport(reportText(r, "程序"));
      return callJsonAuto("GetUpdateState");
    }).then(applyUpdateState);
  }
  function applyApp() {
    // 已经下好就直接换（这一步不碰网络，断网也能重启完成更新）；
    // 否则交给宿主：检查 + 转后台下载
    var ready = !!(updateState && updateState.ready);
    showReport(ready ? "正在重启并更新…" : "正在准备更新…");
    var p = ready ? callJson("ApplyReadyUpdate") : callJson("StartAppUpdate");
    p.then(function (r) {
      showReport(reportText(r, "程序"));
      if (r && r.updating) setStatus("正在更新并重启，请稍候…", "ready");
    });
  }
  function snoozeApp() {
    var v = updateState ? (updateState.remoteVersion || "") : "";
    var snz = $("snooze-app");
    if (snz) snz.style.display = "none";
    if (updateState) updateState.snoozed = true;
    showReport("新版本 v" + (v || "?") + " 已下载，这次先不更新，下次启动时再提示。");
    return call("SnoozeUpdate", [v]);
  }

  // ---------- 页签 ----------
  var EXAMPLES = [
    "我和合伙人互相猜忌，团队里有人不断传话挑拨，骨干已经想走了",
    "公司规模比对手小很多，现金流吃紧，不知道该不该继续正面竞争",
    "前两年扩张太快，现在管理失控，各地团队各自为政不听指挥",
    "新产品上线后被用户集中投诉，口碑下滑，销售还催我加大投入"
  ];

  function showTab(name) {
    var s = $("view-search"), l = $("view-library");
    if (s) s.style.display = name === "search" ? "block" : "none";
    if (l) l.style.display = name === "library" ? "block" : "none";
    var ts = $("tab-search"), tl = $("tab-library");
    if (ts) ts.className = name === "search" ? "tab active" : "tab";
    if (tl) tl.className = name === "library" ? "tab active" : "tab";
    if (name === "library") refreshLibrary();
  }
  function bind(id, handler) { var n = $(id); if (n) n.onclick = handler; }

  // ---------- 启动 ----------
  function boot() {
    if (!$("status") || !$("results")) { logFromJs("boot", "关键元素缺失"); return; }
    setStatus("正在备书…");
    var ex = $("examples");
    if (ex) {
      for (var i = 0; i < EXAMPLES.length; i++) {
        (function (q) {
          var b = el("button", "chip chip-btn", q.length > 18 ? q.slice(0, 18) + "…" : q);
          b.onclick = function () { doSearch(q); };
          ex.appendChild(b);
        })(EXAMPLES[i]);
      }
    }
    bind("go", function () { doSearch(); });
    var ta = $("situation");
    if (ta) {
      ta.onkeydown = function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === "Enter") { e.preventDefault(); doSearch(); }
      };
    }
    bind("tab-search", function () { showTab("search"); });
    bind("tab-library", function () { showTab("library"); });
    bind("check-data", checkData);
    bind("apply-data", applyData);
    bind("check-app", checkApp);
    bind("apply-app", applyApp);
    bind("snooze-app", snoozeApp);
    bind("open-folder", function () { call("ShowDataFolder"); });
    bind("explain-toggle", function () {
      var b = $("explain-body");
      if (b) b.style.display = b.style.display === "none" ? "block" : "none";
    });
    var eb = $("explain-body");
    if (eb) eb.style.display = "none";

    // 一律经 callJsonAuto：桥可用则问真引擎，桥不可用会自愈到离线实现（snap.local === true）
    callJsonAuto("GetSnapshot").then(function (snap) {
      if (!snap) { setStatus("未能连接本地引擎。", "warn"); return; }
      if (!snap.local) {
        setStatus("已备 " + (snap.docs || 0) + " 则史料 · v" + (snap.version || ""),
          (snap.docs || 0) > 0 ? "ready" : "warn");
        if ((snap.docs || 0) > 0) enableSearch();
        return;
      }
      if ((snap.docs || 0) > 0) {
        setStatus("离线模式 · 已备 " + snap.docs + " 则史料（精度低于内置界面）", "ready");
        enableSearch();
      } else {
        setStatus("未能读取 corpus.json，请确认它与本页面在同一目录。", "warn");
      }
    });

    // 先问一次：可能是上一个会话已经把包下好、只是没重启（宿主在启动时就会检测）
    callJsonAuto("GetUpdateState").then(applyUpdateState);

    later(function poll() {
      if (!host()) { later(poll, 60000); return; }
      call("ConsumeDataUpdated").then(function (v) {
        if (v === true) { setStatus("史料已更新，正在刷新…", "ready"); refreshLibrary(); }
      });
      // 后台下载完成后最多 60 秒，藏书阁面板就会显示「新版本已下载，重启后生效」
      callJsonAuto("GetUpdateState").then(applyUpdateState);
      later(poll, 60000);
    }, 60000);

    window.addEventListener("beforeunload", function () { clearAllTimers(); lastRendered = ""; });
  }

  window.onerror = function (msg, src, line, col, error) {
    var t = errText(error && (error.message || error.name) ? error : msg);
    logFromJs("onerror " + String(src || "").split("/").pop() + ":" + line, t);
    setStatus(t, "warn");
    return false;
  };
  window.addEventListener("unhandledrejection", function (ev) {
    reportError("unhandledrejection", ev && ev.reason !== undefined ? ev.reason : ev);
    if (ev && ev.preventDefault) ev.preventDefault();
  });

  ready(boot);
})();
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
          d.decision, d.outcome, d.cause, d.process, d.significance,
          (d.figures || []).join(""), (d.themes || []).join(""),
          (d.cast || []).join("")].join("");
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
      var toHit = function (r) {
        var d = C.docs[r.i];
        return {
          book: d.__book || "", chapter: d.chapter || "", title: d.title || "",
          original: d.original || "", translation: d.translation || "",
          figures: d.figures || [], decision: d.decision || "",
          outcome: d.outcome || "", score: Math.round(r.s * 10) / 10,
          themes: d.themes || [], pros: d.pros || [], cons: d.cons || [],
          cast: d.cast || [], cause: d.cause || "", process: d.process || "",
          significance: d.significance || "",
          // 分级字段：离线语料目前一条档位都没有，全是空串 —— 如实留空，
          // 界面据此走「三次相似的处境」那条退化路径。
          verdict: d.verdict || "", verdictWhy: d.verdictWhy || "",
          // 离线引擎不做同义词桥（那在 C# 里），所以没有「为什么给你看这条」。
          why: []
        };
      };
      // 分档选取，规则与 C# 的 Corpus.SearchByVerdict 一致：在相关度下限之上的
      // 候选池里，每档取分最高的那条，按 上→中→下 输出，缺档就不出现。
      // 真机上这一步在 C# 里做；离线这条路径本来用不到，但预览页（make-preview.ps1
      // 的 -WithVerdicts）要在不跑程序的情况下验收三栏排版，所以这里必须有。
      var verdictDocs = 0, di;
      for (di = 0; di < C.docs.length; di++) if (C.docs[di].verdict) verdictDocs++;
      var picked = ranked.slice(0, topK || 3), tiers = [], graded = false;
      if (verdictDocs > 0 && ranked.length) {
        var pool = ranked.slice(0, 200), top = pool[0].s, byTier = {};
        for (di = 0; di < pool.length && pool[di].s >= top * 0.35; di++) {
          var v = C.docs[pool[di].i].verdict;
          if (v && !byTier[v]) byTier[v] = pool[di];
        }
        var chosen = [];
        for (di = 0; di < TIER_ORDER.length; di++) {
          var r0 = byTier[TIER_ORDER[di]];
          tiers.push({ verdict: TIER_ORDER[di], hit: r0 ? chosen.length : -1 });
          if (r0) chosen.push(r0);
        }
        if (chosen.length) { picked = chosen; graded = true; }
        else tiers = [];
      }
      var hits = picked.map(toHit);
      return { hits: hits, terms: Object.keys(terms), elapsedMs: 0, local: true,
        graded: graded, tiers: tiers, verdictDocs: verdictDocs };
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
      // bridge 是空数组而不是不填：离线引擎没有同义词桥（那在 C# 里），
      // 如实说「没理解出什么」，不要编。
      return Promise.resolve({ normalized: q, terms: Object.keys(t), bridge: [] });
    }
    if (name === "CorpusStats") return localStats();
    // 离线（页面被系统浏览器直接打开）时没有分册概念：只说「没装」，不要编一份书目出来
    if (name === "GetLibrary") return Promise.resolve({ installed: false, books: [] });
    if (name === "SetLibrarySelection") {
      return Promise.resolve({ ok: false, error: "离线模式不能加载史书分册。" });
    }
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
  // 情境词与用户输入的重合点亮，三栏概览与决策分析栏共用同一套判据 ——
  // 两处各写一遍的话，日后改一处忘一处，同一个词在两个地方会有两种亮法。
  function situationLine(themes, query, leadText) {
    if (!themes || !themes.length) return null;
    var q = String(query || "");
    var lead = el("p", "analysis-lead");
    lead.appendChild(el("span", "muted", leadText));
    var chips = el("span", "chips inline");
    for (var i = 0; i < themes.length; i++) {
      var t = String(themes[i]);
      var on = q.length >= 2 && q.indexOf(t) >= 0;
      var c = el("span", on ? "chip chip-on" : "chip chip-off", on ? "✓ " + t : t);
      if (on) c.title = "你的描述里出现了这个词";
      chips.appendChild(c);
    }
    lead.appendChild(chips);
    return lead;
  }
  // 决策分析栏：利/弊正文是随语料写好的（离线、不调用 AI）。
  // 这里**不再**渲染「与你的处境对应」那一行：它与上面 renderPlanCol 的「当时的处境」
  // 取的是同一份 themes、同一套点亮判据，从前一个在概览、一个在展开里所以看不出来，
  // 如今两块并排，同一排词会连着出现两遍。
  function analysisBlock(hit) {
    var pros = hit.pros || [], cons = hit.cons || [];
    if (!pros.length && !cons.length) return null;
    var wrap = el("section", "block analysis");
    wrap.appendChild(el("h4", null, "决策分析"));
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
      "这两栏是史事本身的条件与代价，不是结论。哪一边更重，取决于你的实际情况与上面「当时的处境」里那几个词是否成立。"));
    return wrap;
  }
  // 关键决策正文：decision 是一句话概括，cause / process 是标注补上的起因与经过。
  // 老数据只有 decision，那就只渲染那一句，不硬凑空行。
  function decisionBody(hit) {
    var wrap = el("div", "decision-body");
    if (hit.decision) wrap.appendChild(el("p", "prose", hit.decision));
    else if (!hit.cause && !hit.process) wrap.appendChild(el("p", "prose muted", "未标注"));
    if (hit.cause) wrap.appendChild(annoRow("起 因", hit.cause));
    if (hit.process) wrap.appendChild(annoRow("经 过", hit.process));
    return wrap;
  }
  function annoRow(label, text) {
    var p = el("p", "anno-row");
    p.appendChild(el("span", "anno-label", label));
    p.appendChild(el("span", "anno-text", text));
    return p;
  }
  // 「为什么给你看这条」：把用户原话与被理解成的情境并列。这是同义词桥唯一
  // 露出水面的地方 —— 打分早就用了它，但用户此前看不到，于是觉得结果「意义不明」。
  function whyBlock(why) {
    if (!why || !why.length) return null;
    var ul = el("ul", "why-list");
    for (var i = 0; i < why.length; i++) {
      var li = el("li", null, "");
      li.appendChild(el("span", "why-said", "你说到「" + why[i].said + "」"));
      li.appendChild(el("span", "why-arrow", "→"));
      li.appendChild(el("span", "why-label", why[i].label));
      ul.appendChild(li);
    }
    return block("为什么给你看这条", ul);
  }
  // 处境识别条：摆在结果最前面，先说「我们听懂了什么」，再给史料。
  //
  // 按**处境**分组，不是平铺一堆词。这句区别是这张条存在的全部理由：平铺时
  // 用户看到的是「命中了 7 个词」，分组后看到的才是「你这段话里有 3 个处境」。
  // 处境在前（引擎给的 concerns，权威），触发它的原话在后做佐证。
  //
  // 原话来自各条命中的 why，而 why 每条上限 4 条，所以它未必覆盖全部处境 ——
  // 没有佐证原话的处境只显示名字，不编一句用户没说过的话来凑。
  // 一个处境都没有（也不在为什么里出现）时整条不出现，不留空壳。
  function situationStrip(data, hits) {
    var concerns = (data && data.concerns) || [];
    var said = {}, extra = [];
    for (var i = 0; i < hits.length; i++) {
      var why = hits[i].why || [];
      for (var j = 0; j < why.length; j++) {
        var label = why[j].label, s = why[j].said;
        if (!label || !s || s === label) continue;   // said == label 是同义反复，不是佐证
        if (!said[label]) { said[label] = []; extra.push(label); }
        if (said[label].indexOf(s) < 0 && said[label].length < 3) said[label].push(s);
      }
    }
    var labels = [];
    for (var a = 0; a < concerns.length; a++)
      if (labels.indexOf(concerns[a]) < 0) labels.push(concerns[a]);
    for (var b = 0; b < extra.length; b++)
      if (labels.indexOf(extra[b]) < 0) labels.push(extra[b]);
    if (!labels.length) return null;

    var strip = el("div", "situation-strip");
    strip.appendChild(el("span", "situation-lead",
      "你的描述里认出了 " + labels.length + " 个处境："));
    var chips = el("span", "chips inline");
    for (var m = 0; m < labels.length; m++) {
      var chip = el("span", "chip chip-syn");
      chip.appendChild(el("em", null, labels[m]));
      var quotes = said[labels[m]];
      if (quotes && quotes.length) {
        var parts = [];
        for (var q = 0; q < quotes.length; q++) parts.push("「" + quotes[q] + "」");
        chip.appendChild(el("i", null, "你说 " + parts.join("、")));
      }
      chips.appendChild(chip);
    }
    strip.appendChild(chips);
    return strip;
  }
  function clip(text, n) {
    var s = String(text || "");
    if (s.length <= n) return s;
    return s.substr(0, n) + "…";
  }
  // 展开后的正文：只有原文与白话译文。
  // 其余判断依据（核心人物、现实意义、决策分析、命中词元）一律摆在展开**之外**，
  // 与「怎么做」「结果如何」并列直接可见 —— 它们是读一条史料时真正要看的，
  // 藏在一次点击后面等于没写。展开留给「原文长什么样」这一个问题。
  function renderSourceText(hit) {
    var wrap = el("div", "hit-detail");
    wrap.appendChild(block("原文摘录", el("blockquote", "quote", hit.original || "（未收录原文）")));
    wrap.appendChild(block("现代文翻译", el("p", "prose", hit.translation || "（该条暂无白话译文）")));
    wrap.appendChild(sealCorner());
    return wrap;
  }
  var TIER_ORDER = ["上", "中", "下"];
  var TIER_TEXT = { "上": "上策", "中": "中策", "下": "下策" };
  var TIER_CLASS = { "上": "plan-up", "中": "plan-mid", "下": "plan-down" };
  // 把 hits 摆成槽位。graded=false 时是按分数排的 N 条（徽章为序号），
  // graded=true 时固定 上/中/下 三档，缺档就是空槽。
  // 两种模式共用 renderPlanCol 这一条路径，差别只在徽章文字与是否空档 ——
  // 所以「标注真跑起来」那天，界面不需要再改代码。
  function resultSlots(data) {
    var hits = (data && data.hits) ? data.hits : [];
    // coverage 与 hits 同序（C# 侧 SearchToJson 一起吐出来的两数组）。
    var coverage = (data && data.coverage) || [];
    var concernTotal = ((data && data.concerns) || []).length;
    var slots = [], i;
    if (!(data && data.graded)) {
      for (i = 0; i < hits.length; i++)
        slots.push({ hit: hits[i], badge: "#" + (i + 1), cls: "", at: i });
      for (i = 0; i < slots.length; i++) {
        slots[i].cov = typeof coverage[i] === "number" ? coverage[i] : -1;
        slots[i].concernTotal = concernTotal;
      }
      return slots;
    }
    var byTier = {}, at = {}, tiers = data.tiers || [];
    for (i = 0; i < tiers.length; i++) {
      var it = tiers[i];
      if (it && typeof it.hit === "number" && it.hit >= 0 && it.hit < hits.length) {
        byTier[it.verdict] = hits[it.hit];
        at[it.verdict] = it.hit;
      }
    }
    for (i = 0; i < TIER_ORDER.length; i++) {
      var v = TIER_ORDER[i];
      var idx = at[v];
      slots.push({ hit: byTier[v] || null, badge: TIER_TEXT[v], cls: TIER_CLASS[v],
        verdict: v, at: idx,
        cov: typeof coverage[idx] === "number" ? coverage[idx] : -1,
        concernTotal: concernTotal });
    }
    return slots;
  }
  function renderPlanCol(slot, query) {
    var hit = slot.hit;
    var col = el("article", "plan-col" + (slot.cls ? " " + slot.cls : "") + (hit ? "" : " empty"));
    var head = el("header", "plan-head");
    head.appendChild(el("span", "plan-badge" + (slot.cls ? " " + slot.cls : ""), slot.badge));
    if (typeof hit !== "undefined" && hit && typeof hit.score === "number") {
      var sc = el("span", "score", hit.score.toFixed(2));
      sc.title = "本地加权得分";
      head.appendChild(sc);
    }
    col.appendChild(head);
    if (!hit) {
      col.appendChild(el("p", "plan-empty-text", "这一档暂无贴切的史事。"));
      col.appendChild(el("p", "plan-empty-note",
        "宁缺勿凑 —— 硬塞一条与你处境无关的史料，比如实留空更糟。"));
      return col;
    }
    var tb = el("div", "plan-title");
    tb.appendChild(el("h3", null, hit.title || hit.chapter || "（无标题）"));
    tb.appendChild(el("p", "hit-sub", (hit.book || "史料") + "·" + (hit.chapter || "")));
    col.appendChild(tb);
    // 覆盖说明：把排序依据摊开给用户看。这正是「结果意义不明」的解药 ——
    // 一条史料凭什么排在这儿，答「它占了你说的 3 个处境里的 2 个」比答
    // 「它的加权分是 7.27」有用得多。覆盖 0 个的不显示：那会变成一句
    // 自曝其短的声明，而它出现在这里只是因为有档位要填。
    // 只说得出 1 个处境的查询也不显示 —— 那时「1 个中的 1 个」不构成排序依据。
    if (slot.cov > 0 && slot.concernTotal > 1) {
      col.appendChild(el("p", "hit-coverage",
        "这条史料占了你说的 " + slot.concernTotal + " 个处境中的 " + slot.cov + " 个"));
    }
    // 档位判据只在有档位时才出现（没有标注的条目是空串）。
    if (hit.verdict && hit.verdictWhy) {
      col.appendChild(el("p", "plan-verdict-why",
        (TIER_TEXT[hit.verdict] || "本档") + "：" + hit.verdictWhy));
    }
    var themed = situationLine(hit.themes, query, "当时的处境：");
    if (themed) col.appendChild(themed);
    var hasHow = !!(hit.decision || hit.cause || hit.process);
    if (hasHow) col.appendChild(block("怎么做", decisionBody(hit)));
    if (hit.outcome) col.appendChild(block("结果如何", el("p", "prose outcome", hit.outcome)));

    // 以下四块原先在「展开」里，现改为直接呈现（见 renderSourceText 的注释）。
    // 核心人物：cast 带身份角色（「项羽（西楚霸王·主帅）」），精选条目只有 figures
    // 且是人名裸串。cast 非空就用 cast，否则回落 figures —— 精选那边完全不受影响。
    var cast = (hit.cast && hit.cast.length) ? hit.cast : hit.figures;
    if (cast && cast.length) {
      var figs = el("ul", "figures" + (hit.cast && hit.cast.length ? " cast" : ""));
      for (var i = 0; i < cast.length; i++) figs.appendChild(el("li", null, cast[i]));
      col.appendChild(block("核心人物", figs));
    }
    // 现实意义与「决策分析（利/弊）」并列但独立：前者是这条史料对当下的启发，
    // 后者是史事本身的条件与代价。没有标注的条目整块不出现，不留空壳。
    if (hit.significance) {
      col.appendChild(block("现实意义", el("p", "prose significance", hit.significance)));
    }
    var an = analysisBlock(hit);
    if (an) col.appendChild(an);

    // 没有标注的分片条目（只有原文与译文）上面那些块一个都不会出现，给一段译文节选，
    // 免得整栏只剩一个标题。原文与译文全文仍在「展开」里。
    var annotated = hasHow || hit.outcome || (hit.themes && hit.themes.length)
      || hit.significance || (cast && cast.length)
      || (hit.pros && hit.pros.length) || (hit.cons && hit.cons.length);
    if (!annotated && hit.translation) {
      col.appendChild(block("白话译文（节选）", el("p", "prose", clip(hit.translation, 90))));
    }
    var why = whyBlock(hit.why);
    if (why) col.appendChild(why);
    if (hit.terms && hit.terms.length) col.appendChild(block("命中词元", chipList(hit.terms)));

    var btn = el("button", "plan-expand", "展开原文与译文");
    btn.type = "button";
    btn.setAttribute("aria-expanded", "false");
    var detail = renderSourceText(hit);
    detail.hidden = true;
    btn.addEventListener("click", function () {
      detail.hidden = !detail.hidden;
      btn.textContent = detail.hidden ? "展开原文与译文" : "收起";
      btn.setAttribute("aria-expanded", detail.hidden ? "false" : "true");
    });
    col.appendChild(btn);
    col.appendChild(detail);
    return col;
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
    var graded = !!(data && data.graded);
    var head = el("div", "section-title");
    // 标题随数据升格：有档位才是「上中下三策」，否则如实叫「三次相似的处境」。
    head.appendChild(el("h2", null, graded ? "镜 · 上中下三策" : "镜 · 三次相似的处境"));
    var hint = (data && data.local)
      ? "离线兜底引擎检索所得（精度低于内置界面）" : "本机 C# 引擎检索所得";
    if (!graded && data && data.verdictDocs === 0) {
      hint += " · 上中下分级待标注产出，此前按相关度排列";
    }
    head.appendChild(el("span", "hint", hint));
    box.appendChild(head);
    var strip = situationStrip(data, hits);
    if (strip) box.appendChild(strip);
    var grid = el("div", "grid-3");
    var slots = resultSlots(data);
    for (var i = 0; i < slots.length; i++) grid.appendChild(renderPlanCol(slots[i], query));
    box.appendChild(grid);
  }
  function renderExplain(data) {
    var box = $("explain-body");
    if (!box) return;
    box.textContent = "";
    if (!data) return;
    box.appendChild(el("p", null, "规范化：" + (data.normalized || "—")));
    box.appendChild(el("p", null, "词元（C# 生成）：" + ((data.terms && data.terms.length) ? data.terms.join("、") : "—")));
    var pairs = data.bridge || [];
    if (pairs.length) {
      // 「观其解字之法」讲的是切词，这里补上它上面那一层：处境。
      // 同一个平铺列表，按 label 归并之后才看得出「这不是 5 个词，是 2 个处境」。
      var byLabel = [], idx = {};
      for (var i = 0; i < pairs.length; i++) {
        var lb = pairs[i].label;
        if (idx[lb] === undefined) { idx[lb] = byLabel.length; byLabel.push({ label: lb, said: [] }); }
        var g = byLabel[idx[lb]];
        if (g.said.indexOf(pairs[i].said) < 0) g.said.push(pairs[i].said);
      }
      // 处境数以引擎的 concerns 为准（bridge 里可能少 —— 有的处境是标签名被
      // 直接写中而触发的，那不产生 bridge 对，但确实是个处境）。
      var concernList = data.concerns || [];
      var total = concernList.length || byLabel.length;
      // 按 concerns 的顺序摆，bridge 里多出来的（理论上有）缀在后面。
      var ordered = [], seenLb = {};
      for (var c1 = 0; c1 < concernList.length; c1++) {
        seenLb[concernList[c1]] = 1;
        for (var c2 = 0; c2 < byLabel.length; c2++)
          if (byLabel[c2].label === concernList[c1]) ordered.push(byLabel[c2]);
      }
      for (var c3 = 0; c3 < byLabel.length; c3++)
        if (!seenLb[byLabel[c3].label]) ordered.push(byLabel[c3]);
      var parts = [];
      for (var j = 0; j < ordered.length; j++) {
        var g2 = ordered[j], qs = [];
        for (var k = 0; k < g2.said.length; k++) qs.push("「" + g2.said[k] + "」");
        parts.push(qs.length ? g2.label + "（你说 " + qs.join("、") + "）" : g2.label);
      }
      box.appendChild(el("p", null,
        "系统把你的话理解成了 " + total + " 个处境：" + parts.join("；")));
    }
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
  // 「装了」与「加载了」是两件事：分册随安装包装好（离线可用），但只有勾选的才读进内存。
  // 这一段是唯一会改动内存占用的界面 —— 应用一次等于重建索引，所以要显式的按钮 + 忙碌态，
  // 不能每勾一下就重载。
  var SEL_PAGE = 20, selPage = 0;
  // 实测（标注后的分片）：史记 9.2 MB 分册 -> 索引 163.7 MB；前四史 26.6 MB -> 424.9 MB。
  // 16–19 倍，随各书用词重合度浮动，所以界面上的数字一律标「约」。
  // 取 19 是刻意压在这条带子的上沿：宁可把内存报高一点，也别让内存吃紧的人
  // 按偏小的数去勾书。改系数前先跑 tests\ShardCheck.exe --ram-default 实测。
  var RAM_PER_BYTE = 19;

  function fmtMB(bytes) {
    var b = Number(bytes) || 0;
    return (Math.round(b * 10 / 1048576) / 10) + " MB";
  }

  function renderShards(lib) {
    var box = $("shards");
    if (!box) return;
    box.textContent = "";
    if (!lib || !lib.installed || !(lib.books || []).length) {
      box.appendChild(el("p", "hint", "本次安装未附带史书分册，检索只用精选史料。"));
      return;
    }

    var books = lib.books;
    var pending = {};
    var i;
    for (i = 0; i < books.length; i++) if (books[i].selected) pending[books[i].slug] = 1;

    box.appendChild(el("h2", "sec-title", "史书分册"));
    box.appendChild(el("p", "hint", "分册已随程序装好，勾选后才读进内存，取消即释放。"
      + "全量索引约 1.5 GB，按需选择。"));

    var table = el("table", "books");
    var hr = el("tr");
    hr.appendChild(el("th", null, "选"));
    hr.appendChild(el("th", null, "史书"));
    hr.appendChild(el("th", null, "朝代"));
    hr.appendChild(el("th", null, "条数"));
    hr.appendChild(el("th", null, "文件"));
    hr.appendChild(el("th", null, "译文"));
    var thead = el("thead");
    thead.appendChild(hr);
    table.appendChild(thead);

    var pages = Math.max(1, Math.ceil(books.length / SEL_PAGE));
    if (selPage >= pages) selPage = pages - 1;
    if (selPage < 0) selPage = 0;
    var tbody = el("tbody");
    var from = selPage * SEL_PAGE, to = Math.min(books.length, from + SEL_PAGE);
    var boxes = [];
    for (i = from; i < to; i++) {
      (function (b) {
        var tr = el("tr");
        var td0 = el("td");
        var cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = !!pending[b.slug];
        cb.onchange = function () {
          if (cb.checked) pending[b.slug] = 1; else delete pending[b.slug];
          summary();
        };
        boxes.push({ slug: b.slug, box: cb });
        td0.appendChild(cb);
        tr.appendChild(td0);
        tr.appendChild(el("td", null, b.book));
        tr.appendChild(el("td", null, b.dynasty));
        tr.appendChild(el("td", null, String(b.docs)));
        tr.appendChild(el("td", null, fmtMB(b.bytes)));
        tr.appendChild(el("td", null, b.pairing));
        tbody.appendChild(tr);
      })(books[i]);
    }
    table.appendChild(tbody);
    box.appendChild(table);

    if (pages > 1) {
      var pager = el("div", "pager");
      var prev = el("button", "ghost", "上一页"), next = el("button", "ghost", "下一页");
      prev.disabled = selPage <= 0;
      next.disabled = selPage >= pages - 1;
      prev.onclick = function () { selPage--; renderShards(lib); };
      next.onclick = function () { selPage++; renderShards(lib); };
      pager.appendChild(prev);
      pager.appendChild(el("span", "hint", " " + (selPage + 1) + " / " + pages + " "));
      pager.appendChild(next);
      box.appendChild(pager);
    }

    var row = el("div", "row");
    var all = el("button", "ghost", "全选"), none = el("button", "ghost", "全不选");
    var apply = el("button", "seal-btn", "应用勾选");
    var info = el("span", "hint");
    all.onclick = function () {
      pending = {};
      for (i = 0; i < books.length; i++) pending[books[i].slug] = 1;
      for (i = 0; i < boxes.length; i++) boxes[i].box.checked = true;
      summary();
    };
    none.onclick = function () {
      pending = {};
      for (i = 0; i < boxes.length; i++) boxes[i].box.checked = false;
      summary();
    };
    apply.onclick = function () { applySelection(lib, pending, apply); };
    row.appendChild(all);
    row.appendChild(none);
    row.appendChild(apply);
    row.appendChild(info);
    box.appendChild(row);

    box.appendChild(el("p", "hint", "内存是索引占用（常驻），与文件大小不是一回事："
      + "实测每 MB 分册约合 " + RAM_PER_BYTE + " MB 索引，上面的「约」按此折算。"));

    function summary() {
      var n = 0, bytes = 0;
      for (i = 0; i < books.length; i++) {
        if (!pending[books[i].slug]) continue;
        n++; bytes += Number(books[i].bytes) || 0;
      }
      info.textContent = "已选 " + n + " / " + books.length + " 部 · 文件 " + fmtMB(bytes)
        + " · 索引约 " + fmtMB(bytes * RAM_PER_BYTE);
    }
    summary();
  }

  function applySelection(lib, pending, btn) {
    var slugs = [];
    for (var k in pending) if (Object.prototype.hasOwnProperty.call(pending, k)) slugs.push(k);
    if (btn) btn.disabled = true;
    setStatus("正在重载史书，请稍候…", "ready");
    callJsonAuto("SetLibrarySelection", [JSON.stringify(slugs)]).then(function (r) {
      if (btn) btn.disabled = false;
      if (!r) { setStatus("史书重载失败：本地引擎没有响应。", "warn"); return; }
      if (r.ok === false) { setStatus("史书重载失败：" + friendly(r.error || ""), "warn"); return; }
      setStatus("已加载 " + (r.docs || 0) + " 则史料 · 索引约 " + Math.round((r.indexKB || 0) / 1024)
        + " MB", "ready");
      refreshLibrary();
    });
  }

  // 宿主是在后台线程里备书的，页面会比它早**约两秒**起来（实测：01:19:51 导航到页面，
  // 01:19:53 语料才就绪）。此前这里拿到 docs=0 就当成最终结论，于是：
  //   · 「稽古一问」永远不放开 —— 只有点示例按钮走完一次 doSearch，才会在收尾处解禁；
  //   · 藏书阁渲染成「全不勾」—— 同一时刻 _selectedShards 也还没赋值，且之后不会自愈。
  // 现在 docs=0 只代表「还没备好」，轮询等它，不把中间态当成结果。
  var engineReady = false, readyTimer = null, readyTries = 0;
  var libInstalled = false, libBooks = 0;

  function engineWait() {
    if (engineReady || readyTimer !== null) return;
    readyTries = 0;
    readyTimer = later(enginePoll, 400);
  }
  function enginePoll() {
    readyTimer = null;
    readyTries++;
    callJsonAuto("GetSnapshot").then(function (s) {
      if (s && !s.local && (s.docs || 0) > 0) {
        showSnapshot(s);
        refreshLibrary();   // 勾选和语料在 RebuildCorpus 里一起赋值，此刻一起就绪
        return;
      }
      // 40 次 × 400ms ≈ 16 秒还备不好就不再等：放开按钮让用户自己试。
      // 真出问题时 Search 会返回「书库尚未就绪」，比一个永远点不动的按钮诚实。
      if (readyTries >= 40) {
        setStatus("书库读取较慢，可以先试着检索。", "warn");
        enableSearch();
        return;
      }
      readyTimer = later(enginePoll, 400);
    });
  }

  // 「装了分册」与「勾了分册」是两件事：一部都没勾时给个明确的去处，
  // 否则用户只会觉得结果就这几条，根本不知道本地还躺着二十四史。
  function updateLibraryCta() {
    var cta = $("lib-cta");
    if (cta) cta.style.display = (engineReady && libInstalled && libBooks === 0) ? "" : "none";
  }

  function showSnapshot(snap) {
    if (!snap) { setStatus("未能连接本地引擎。", "warn"); return; }
    if (!snap.local) {
      var n = snap.docs || 0;
      libInstalled = !!snap.libraryInstalled;
      libBooks = snap.books || 0;
      if (n <= 0) { setStatus("正在备书…"); engineWait(); return; }
      engineReady = true;
      var t = "已备 " + n + " 则史料";
      if (libBooks > 0) t += " · 已加载 " + libBooks + " 部史书";
      else if (libInstalled) t += " · 未加载史书分册";
      t += " · v" + (snap.version || "");
      setStatus(t, "ready");
      enableSearch();
      updateLibraryCta();
      return;
    }
    if ((snap.docs || 0) > 0) {
      engineReady = true;
      setStatus("离线模式 · 已备 " + snap.docs + " 则史料（精度低于内置界面）", "ready");
      enableSearch();
    } else {
      setStatus("未能读取 corpus.json，请确认它与本页面在同一目录。", "warn");
    }
  }

  function refreshLibrary() {
    return Promise.all([callJsonAuto("CorpusStats"), callJsonAuto("GetSnapshot"),
      callJsonAuto("GetLibrary")])
      .then(function (r) {
        // 勾选数量以这里读到的为准：改完勾选、数据热更新、等语料就绪都会走这条路，
        // 不在这儿同步的话右侧那个「去藏书阁选书」会一直挂着不消失。
        if (r[1]) {
          if (r[1].libraryInstalled !== undefined) libInstalled = !!r[1].libraryInstalled;
          if (r[1].books !== undefined) libBooks = r[1].books || 0;
          updateLibraryCta();
        }
        renderLibrary(r[0], r[1]);
        renderShards(r[2]);
      });
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

  // 首次运行引导只弹一次。localStorage 不可用（少数受限承载环境）时当作「已看过」——
  // 每次启动都弹一遍比不弹更烦人。
  var GUIDE_KEY = "jigu.guide.library.v1";
  function guideSeen() {
    try { return window.localStorage.getItem(GUIDE_KEY) === "1"; } catch (e) { return true; }
  }
  function markGuide() {
    try { window.localStorage.setItem(GUIDE_KEY, "1"); } catch (e) { }
    var g = $("guide");
    if (g) g.style.display = "none";
  }

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
        // 输入法组字阶段（拼音候选未上屏）的回车是「选字」，不是「提交」。
        // 中文输入法下这条判断不能省：少了它，拼音打一半按回车会把半截拼音送去检索。
        if (e.isComposing || e.keyCode === 229) return;
        if (e.key !== "Enter" || e.altKey) return;
        if (e.shiftKey) return;          // Shift + Enter 留给换行
        e.preventDefault();              // 裸 Enter 与 Ctrl / ⌘ + Enter 都发起检索
        doSearch();
      };
    }
    bind("tab-search", function () { showTab("search"); });
    bind("tab-library", function () { showTab("library"); });
    bind("lib-cta", function () { showTab("library"); });
    bind("guide-go", function () { markGuide(); showTab("library"); });
    bind("guide-close", markGuide);
    var guide = $("guide");
    if (guide && !guideSeen()) guide.style.display = "block";
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
    callJsonAuto("GetSnapshot").then(showSnapshot);

    // 先问一次：可能是上一个会话已经把包下好、只是没重启（宿主在启动时就会检测）
    callJsonAuto("GetUpdateState").then(applyUpdateState);

    later(function poll() {
      if (!host()) { later(poll, 60000); return; }
      // 上一轮等了 16 秒仍没备好（enginePoll 放手的那个分支）：这一轮再补问一次，
      // 不然界面会一直停在「读得慢」的状态，用户没法自己恢复。
      if (!engineReady) callJsonAuto("GetSnapshot").then(showSnapshot);
      call("ConsumeDataUpdated").then(function (v) {
        if (v === true) {
          // 热替换会按当前勾选重建语料，状态条的条数/部数都得跟着变
          setStatus("史料已更新，正在刷新…", "ready");
          callJsonAuto("GetSnapshot").then(showSnapshot);
          refreshLibrary();
        }
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
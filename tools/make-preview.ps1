# FILE: jigu-app/tools/make-preview.ps1
# 生成一份离线预览页：把 web/ 前端与真实语料内联进单个 HTML，
# 直接用浏览器打开就能看界面（含三栏与「决策分析」栏），不需要跑 exe、不需要 WebView2。
#
# 用法:
#   powershell -File tools\make-preview.ps1
#   powershell -File tools\make-preview.ps1 -WithVerdicts
#   powershell -File tools\make-preview.ps1 -WithVerdicts -SkipTier 下
#   powershell -File tools\make-preview.ps1 -Question "新产品上线后被用户集中投诉"
#   powershell -File tools\make-preview.ps1 -HitsJson hits.json -Question "…" -ScrollTo "#results"
#
# -HitsJson 吃的是**真引擎**的 SearchToJson 输出，用来验收「为什么给你看这条」
# 与处境识别条（离线兜底引擎没有同义词桥，这两块在它那儿恒为空）。
# 生成办法：把 src\*.cs 与下面这个一次性程序同编，跑一次即可 ——
#   class HitDump { static void Main(string[] a) {
#       Corpus c = new Corpus();
#       c.LoadFrom(a[1], Library.ShardPaths(a[1], Library.LoadSelection(a[1], null)));
#       string t; long ms;
#       File.WriteAllText(a[2], c.SearchToJson(a[0], 3, out t, out ms), new UTF8Encoding(false)); } }
# 传 -HitsJson 时 -Question 要与导出时用的查询一致，否则结果区显示的是别的问题。
#
# -WithVerdicts 会给**预览专用**的语料盖上一份合成分级，用来验收三栏排版、档位配色、
# 徽章与空档文案 —— 真实语料此刻一条档位都没有（要等标注流水线跑起来）。
# 这些值只存在于生成的 HTML 里，绝不写回 resources\data\* 或 dist\。
param(
  [switch]$WithVerdicts,
  [string]$SkipTier = "",
  [string]$Question = "我和合伙人互相猜忌，团队里有人不断传话挑拨，骨干已经想走了",
  # 截图用：结果区在首屏之下，HtmlShot 不会滚动，所以由页面自己滚过去。
  # 留空则不动 —— 手动用浏览器打开预览时不需要它。
  [string]$ScrollTo = "",
  # 真引擎结果：把 稽古 的 SearchToJson 输出（见 tools 说明）存成文件传进来，
  # 预览页会装一个只有 Search 的假宿主桥原样返回它。离线兜底引擎没有同义词桥，
  # 「为什么给你看这条」与处境识别条在它那儿永远是空的 —— 想看这两块只能用真引擎。
  [string]$HitsJson = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "tests\preview-ui.html"

function Read-Utf8([string]$p) { return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) }

$html = Read-Utf8 (Join-Path $root "web\index.html")
$css1 = Read-Utf8 (Join-Path $root "web\styles.css")
$css2 = Read-Utf8 (Join-Path $root "web\ink.css")
$app = Read-Utf8 (Join-Path $root "web\app.js")

# 合成分级（只在 -WithVerdicts 时用）。按条目序号轮转 上/中/下，保证三档都有候选；
# 判据文案是编的，只为看排版，页面上会另外挂一条醒目提示说明这一点。
$SYN_TIERS = @("上", "中", "下")
$SYN_WHY = @{
  "上" = "（合成分级）当事人达成了目的，代价在可承受范围内"
  "中" = "（合成分级）局面暂时稳住，但根子没除，后来仍有反复"
  "下" = "（合成分级）手段失当，反而把关系推向更坏的一边"
}

# 语料：合并全部书卷。pros/cons 必须带上，否则分析栏在预览里是空的。
$items = New-Object System.Collections.ArrayList
$n = 0
Get-ChildItem (Join-Path $root "resources\data") -Filter *.json | Sort-Object Name | ForEach-Object {
  if ($_.Name -eq "version.json") { return }
  $o = Read-Utf8 $_.FullName | ConvertFrom-Json
  foreach ($it in $o.items) {
    $v = ""
    if ($WithVerdicts) {
      $cand = $SYN_TIERS[$n % 3]
      if ($cand -ne $SkipTier) { $v = $cand }
    }
    # 这份清单要与 build.ps1 的 $rec 和 web\app.js 的渲染分支对齐：漏一个字段，
    # 预览里对应的那一块就是空的，而人只会以为「排版没做」。cast/cause/process/
    # significance 是标注期才补上的，这张表当时还没跟上，于是「核心人物」只剩人名
    # 裸串（走 figures 回落）、「现实意义」整块不出现 —— 而三样在真机上都有。
    [void]$items.Add([ordered]@{
      book = $o.book; chapter = $it.chapter; title = $it.title
      original = $it.original; translation = $it.translation
      figures = $it.figures; decision = $it.decision; outcome = $it.outcome
      themes = $it.themes; pros = $it.pros; cons = $it.cons
      cast = $it.cast; cause = $it.cause; process = $it.process
      significance = $it.significance
      verdict = $v; verdictWhy = $(if ($v) { $SYN_WHY[$v] } else { "" })
    })
    $n++
  }
}
$corpusJs = ($items | ConvertTo-Json -Depth 6 -Compress)
Write-Host ("corpus items = " + $items.Count + $(if ($WithVerdicts) { "  (合成分级已盖上；跳过档位='" + $SkipTier + "')" } else { "" }))

# app.js 在没有宿主桥（hostObjects 不存在）时走离线兜底，那条路只做一件事：
# fetch("corpus.json")。预览页把语料直接喂给这个 fetch，离线引擎就跑起来了，
# 渲染路径与真机完全一致——预览看到的排版就是用户看到的排版。
# （离线引擎精度低于内置 C# 引擎，这里只看版式。）
$prelude = @"
// 截图工具用的是系统自带的旧 IE 内核，它没有 Promise，而离线兜底路径到处在用；
// 真机是 WebView2（Chromium），不缺这个。这里补一个最小实现，只为让预览能跑起来 ——
// 它不属于 web/app.js，也不会进产品。
(function () {
  if (typeof window.Promise !== "undefined") return;
  function P(exec) {
    var self = this;
    self.s = 0; self.v = undefined; self.q = [];
    function run(h) {
      if (!self.s) { self.q.push(h); return; }
      setTimeout(function () {
        var fn = self.s === 1 ? h.ok : h.no;
        if (typeof fn !== "function") { (self.s === 1 ? h.res : h.rej)(self.v); return; }
        try { h.res(fn(self.v)); } catch (e) { h.rej(e); }
      }, 0);
    }
    self._run = run;
    function settle(st, v) {
      if (self.s) return;
      // 真 Promise 会「展开」thenable（Promises/A+ 2.3.2）。这里必须照做：
      // app.js 写的是 fetch(...).then(r => r.json()).then(d => ...)，第二个回调
      // 拿到的应该是 JSON，而不是一个 Promise。不展开的话 loadLocalCorpus 会
      // 把 Promise 当数据读，docs 恒为 0，界面就报「未能读取 corpus.json」——
      // 这正是这个补丁存在的原因（实测截图复现过）。
      if (st === 1 && v !== null && typeof v === "object" && typeof v.then === "function") {
        v.then(function (x) { settle(1, x); }, function (e) { settle(2, e); });
        return;
      }
      self.s = st; self.v = v;
      var q = self.q; self.q = [];
      for (var i = 0; i < q.length; i++) run(q[i]);
    }
    try { exec(function (v) { settle(1, v); }, function (e) { settle(2, e); }); }
    catch (e) { settle(2, e); }
  }
  P.prototype.then = function (ok, no) {
    var self = this;
    return new P(function (res, rej) { self._run({ ok: ok, no: no, res: res, rej: rej }); });
  };
  P.prototype["catch"] = function (no) { return this.then(null, no); };
  P.resolve = function (v) { return new P(function (res) { res(v); }); };
  P.reject = function (e) { return new P(function (_, rej) { rej(e); }); };
  // app.js 用到的静态方法只有 all 和 resolve（refreshLibrary 那条 Promise.all）。
  // 旧内核一个都没有，缺 all 的话首页会直接甩出「对象不支持"all"属性或方法」。
  P.all = function (list) {
    return new P(function (res, rej) {
      var n = list.length, left = n, out = [];
      if (!n) { res(out); return; }
      for (var i = 0; i < n; i++) {
        (function (i) { P.resolve(list[i]).then(function (v) { out[i] = v; if (--left === 0) res(out); }, rej); })(i);
      }
    });
  };
  window.Promise = P;
})();
window.__PREVIEW__ = true;
window.__CORPUS__ = { book: "corpus", items: $corpusJs };
// 必须返回 Promise：app.js 写的是 fetch(...).then(...)，返回裸对象会在
// 旧内核上报「未能读取 corpus.json」。
window.fetch = function (url) {
  if (String(url).indexOf("corpus.json") >= 0) {
    return Promise.resolve({ json: function () { return Promise.resolve(window.__CORPUS__); } });
  }
  return Promise.reject(new Error("preview: no network"));
};
"@

# -HitsJson 时装一个「只有 Search 的假宿主桥」：Search 原样吐真引擎的 JSON，
# 其余接口一概没有 —— app.js 的 callJsonAuto 拿到 null 会自愈到离线兜底，
# 所以语料、统计那些仍然走上面的 fetch。这样结果区渲染的是真机那条路径。
$fakeBridge = ""
if ($HitsJson) {
  $hitsText = Read-Utf8 $HitsJson
  # 必须 stringify：真宿主桥返回的是**字符串**（app.js 的 callJson 会 typeof raw === "string"
  # 之后再 JSON.parse），直接吐对象会被当成「桥不可用」而静默退回离线引擎。
  $fakeBridge = @"
window.__HITS__ = JSON.stringify($hitsText);
window.chrome = window.chrome || {};
window.chrome.webview = { hostObjects: { host: {
  Search: function (q, k) { return window.__HITS__; }
} } };
"@
  Write-Host ("hits  : " + $HitsJson + " (" + $hitsText.Length + " 字符，真引擎输出)")
}
$prelude = $prelude + $fakeBridge

# 页面加载后自动填一个示例问题并点「稽古一问」，省得每次手输。
$bootQuestion = ($Question -replace '\\', '\\\\') -replace '"', '\"'
$bootScroll = $ScrollTo -replace '\\', '\\\\' -replace '"', '\"'
$bannerText = if ($WithVerdicts) {
  "预览页：语料上的上\/中\/下分级是合成的（只存在于这个 HTML 里），用来验收三栏排版；真实语料还没有档位。"
} else { "" }
$boot = @"
(function () {
  var tries = 0;
  function go() {
    var ta = document.getElementById("situation");
    var btn = document.getElementById("go");
    if ((!ta || !btn || btn.disabled) && tries++ < 40) return setTimeout(go, 250);
    if (!ta || !btn) return;
    ta.value = "$bootQuestion";
    btn.click();
  }
  window.addEventListener("load", function () { setTimeout(go, 250); });
})();
(function () {
  var sel = "$bootScroll";
  if (!sel) return;
  window.addEventListener("load", function () {
    setTimeout(function () {
      var el = document.querySelector(sel);
      if (el && el.scrollIntoView) el.scrollIntoView();
    }, 2500);
  });
})();
(function () {
  var t = "$bannerText";
  if (!t) return;
  window.addEventListener("load", function () {
    var p = document.createElement("p");
    p.textContent = t;
    p.style.cssText = "margin:0 0 12px;padding:8px 12px;font-size:13px;color:#8a5a2b;"
      + "background:#fdf6e6;border-left:3px solid #d4a24a;border-radius:3px";
    if (document.body.firstChild) document.body.insertBefore(p, document.body.firstChild);
    else document.body.appendChild(p);
  });
})();
"@

$html = $html.Replace('<link rel="stylesheet" href="styles.css" />', "<style>`n$css1`n</style>")
$html = $html.Replace('<link rel="stylesheet" href="ink.css" />', "<style>`n$css2`n</style>")
$html = $html.Replace('<script src="app.js"></script>', "<script>`n$prelude`n$app`n</script>`n<script>`n$boot`n</script>")
$html = $html.Replace('<title>稽古</title>', '<title>稽古 · 界面预览</title>')

[System.IO.File]::WriteAllText($out, $html, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("preview: " + $out + "  " + [math]::Round((Get-Item $out).Length/1KB,1) + " KB")

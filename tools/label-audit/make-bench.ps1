# FILE: jigu-app/tools/label-audit/make-bench.ps1
#
# 拼一个「语料目录」给 Tools\label-audit 下的审计/挖掘工具用。
#
# 为什么要这一步：那三个工具都要**同一目录**里同时有 corpus.json / labels.json /
# stopwords.json。而仓里这三样分在三处 ——
#   corpus.json   是构建产物，只在 dist\稽古\ 下（build.ps1 从 resources\data\*.json 合成）
#   labels.json   \  是源文件，在 resources\ 下（运行时被内嵌进 exe 当兜底）
#   stopwords.json/
# 这是刻意的：语料与标签分两条更新通道，运行时不从磁盘读标签，改标签要重新构建。
#
# 于是工具无法直接指向任何单一目录：指向 dist\ 会读到内嵌的旧标签（DataFiles.Load
# 找不到文件时回退到内嵌资源，会静默用旧表算出错的指向性 —— 这个坑踩过一次），
# 指向 resources\ 又没有 corpus.json。这个脚本把三样凑到一起，凑到一个**不进版本库**
# 的位置（默认 %TEMP%\jigu-bench），避免把 110 MB 的分片副本留在仓里。
#
# 用法：
#   powershell -File tools\label-audit\make-bench.ps1
#   powershell -File tools\label-audit\make-bench.ps1 -Out D:\somewhere\bench
#
# 语料变了（重跑了 build.ps1）要**重新执行一次**，工具读的是快照。

param(
    [string]$Out = (Join-Path $env:TEMP 'jigu-bench')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dist = Join-Path $root 'dist\稽古'
$res  = Join-Path $root 'resources'

if (-not (Test-Path (Join-Path $dist 'corpus.json'))) {
    throw "找不到 $dist\corpus.json —— 先跑一次 build.ps1。"
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null

Copy-Item (Join-Path $dist 'corpus.json')  (Join-Path $Out 'corpus.json')  -Force
Copy-Item (Join-Path $res  'labels.json')  (Join-Path $Out 'labels.json')  -Force
Copy-Item (Join-Path $res  'stopwords.json') (Join-Path $Out 'stopwords.json') -Force

# 分片（24 部史书正文）只有 --shards 模式要。约 110 MB，所以是复制而不是链接，
# 且目标目录默认在 %TEMP% 下 —— 这个目录随时可以整个删掉重建。
$shardsSrc = Join-Path $dist 'corpus'
$shardsDst = Join-Path $Out 'corpus'
if (Test-Path $shardsSrc) {
    New-Item -ItemType Directory -Force -Path $shardsDst | Out-Null
    Copy-Item (Join-Path $shardsSrc '*') $shardsDst -Recurse -Force
    $n = (Get-ChildItem $shardsDst -Filter *.json).Count
    Write-Host "分片 $n 个已复制"
}

Write-Host "bench 就绪：$Out"
Write-Host "  试着跑：TriggerMine.exe `"$Out`" --check `"被人高薪挖走`""

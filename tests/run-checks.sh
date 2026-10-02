#!/bin/sh
# 把 tests\ 下的检查程序全部重编一遍再跑，用来确认这次改动没有让任何一条护栏变绿。
#
# 为什么必须重编：
#   · 这些 exe 是编译产物（.gitignore 里 tests/*.exe 不入库），但 tests\AnnoCheck.exe
#     曾经是入库的预编译文件 —— 源码改了却不重编就会全绿假阴性。
#   · build.ps1 编 TierCheck 时若编译失败会静默跑到旧 exe，checks 数量不变就是假绿。
#     所以这里逐条 csc，rc 非 0 直接报出来。
#
# csc 路径坑（照 tests/compile-check.sh）：正斜杠路径的目录段会被吃掉，必须转反斜杠。
cd /d/DSH/jigu-app_new/jigu-app || exit 1
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

SRCS=""
for f in src/*.cs; do
  SRCS="$SRCS $(printf '%s' "$f" | tr '/' '\\')"
done

FLAGS="-nologo -target:exe -utf8output -reference:System.dll -reference:System.Core.dll
  -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Net.dll
  -reference:System.Security.dll -reference:System.IO.Compression.dll
  -reference:tests/Microsoft.Web.WebView2.Core.dll
  -reference:tests/Microsoft.Web.WebView2.WinForms.dll"

rc=0
for name in DataCheck TierCheck TriggerCheck PendingCheck ShardCheck AssetCheck AnnoCheck; do
  echo "### 编译 $name"
  # shellcheck disable=SC2086
  $CSC $FLAGS -out:tests/$name.exe -main:$name $SRCS "tests\\$name.cs" || { rc=1; continue; }
  echo "### 运行 $name"
  ./tests/$name.exe || rc=1
  echo
done

[ $rc -eq 0 ] && echo "=== ALL CHECKS OK ===" || echo "=== SOMETHING FAILED ==="
exit $rc

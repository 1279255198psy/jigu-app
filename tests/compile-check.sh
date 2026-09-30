#!/bin/sh
# 只做编译检查：按 build.ps1 第 3 步的同一组源文件与引用把 src\ 全量编一遍，
# 但不跑 build.ps1（不生成资源、不碰图标缓存）。用来验证改动没有语法/类型错误。
#
# 两点讲究：
#   1. 路径要转成反斜杠 —— 这个 shell 下正斜杠路径的目录部分会被吃掉
#      （csc 最终去找 <cwd>\Json.cs 而不是 <cwd>\src\Json.cs）。
#   2. /out 与 /target 必须排在源文件名前面，否则 CS2022。
# build.ps1 把主程序和安装向导分成两次 csc 调用，所以这里也分两次。
cd /d/DSH/jigu-app_new/jigu-app || exit 1
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

SRCS=""
n=0
for f in src/*.cs; do
  SRCS="$SRCS $(printf '%s' "$f" | tr '/' '\\')"
  n=$((n + 1))
done
echo "source files: $n"

FLAGS="-nologo -target:winexe -platform:anycpu -optimize+ -utf8output
  -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll
  -reference:System.Windows.Forms.dll -reference:System.Net.dll -reference:System.Security.dll
  -reference:tests/Microsoft.Web.WebView2.Core.dll
  -reference:tests/Microsoft.Web.WebView2.WinForms.dll"

rc=0
echo "### pass 1: 主程序"
# shellcheck disable=SC2086
$CSC $FLAGS -out:tests/CompileCheck.exe -main:Jigu.Program $SRCS || rc=1
echo "--- pass 1 exit=$? ---"

echo "### pass 2: 安装向导"
# shellcheck disable=SC2086
$CSC $FLAGS -out:tests/CompileCheckSetup.exe -main:JiguSetup.SetupProgram $SRCS || rc=1
echo "--- pass 2 exit=$? ---"

[ $rc -eq 0 ] && echo "=== COMPILE OK ===" || echo "=== COMPILE FAILED ==="
exit $rc

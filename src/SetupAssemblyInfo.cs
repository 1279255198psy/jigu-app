// FILE: jigu-app/src/SetupAssemblyInfo.cs
// 安装包的版本与产品信息（Windows「属性 → 详细信息」与安全软件提示中会显示）
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("稽古 安装程序")]
[assembly: AssemblyDescription("稽古 · 二十四史情境检索：安装向导（离线运行，零 API 调用）")]
[assembly: AssemblyProduct("稽古")]
[assembly: AssemblyCompany("稽古")]
[assembly: AssemblyCopyright("稽古 · 本地史镜检索")]
// 版本号来自 build.ps1 生成的 Jigu.BuildInfo（src/Version.g.cs），与主程序同源。
[assembly: AssemblyVersion(Jigu.BuildInfo.Version)]
[assembly: AssemblyFileVersion(Jigu.BuildInfo.Version)]
[assembly: ComVisible(false)]

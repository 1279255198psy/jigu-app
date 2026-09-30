# 稽古 · 发布到 GitHub Release（操作手册）

这份文档说明怎么把构建产物发布成 GitHub 上的 Release，让用户能直接下载安装包。
**配置已经写进仓库了**（`.github/workflows/release.yml`），你只需要打一个 tag。

---

## 一、发布：在 VS Code 里提交，然后跑一个任务

发布流只有两半 —— **提交**在 VS Code 的源代码管理面板，**发布**在 `tools\release.ps1`：

1. **提交**：源代码管理面板 → 写提交信息 → 提交 → 同步（把 `main` 推上去）。
2. **发布**：`Ctrl+Shift+P` → `Tasks: Run Task` → **稽古: 发布新版本（打 tag → CI 构建发布）**。
   等价于在终端里跑 `powershell -NoProfile -File tools\release.ps1`。

`tools\release.ps1` 依次做这几件事（下表就是脚本运行时打印的阶段名），
**任何一步不过就停在那里，绝不带病往下走**：

| 阶段 | 做什么 | 什么情况会停 |
| --- | --- | --- |
| `repository` | 确认在 `main`；检查**已跟踪**的文件都已提交（顺带把未跟踪的文件列出来给你看） | 不在 `main` 上；有未提交改动（CI 构建的是 commit，工作区改动不会进包）—— `-AllowDirty` 可明知故犯 |
| `origin` | `git fetch`，确认连得上 GitHub、且本地 `main` 不落后于 `origin/main` | 连不上（脚本**绝不提权改 hosts**，只提示你自己跑 `fix-hosts.ps1`）；落后于远端 |
| `version` | 从 `build.ps1` 读出 `$version` → tag `v<版本号>`；确认远端还没有这个 tag；带 `-Bump` / `-Version` 时改 `build.ps1` 并**自动提交这一次改动** | 版本号不是 `x.y.z`；远端已有同名 tag（说明版本号忘改了） |
| `local pre-flight` | 只有带 `-Build` 时走：本地 `build.ps1` + `--selfcheck` | 编译不过或自检不过 |
| `push main` | `git push origin main` | 推送失败（网络 / hosts / 凭据），此时还没打 tag |
| `tag` | `git tag -a v<版本号>` → `git push origin v<版本号>` | 推送失败；tag 已建在本地，修好原因再跑一次即可 |

其它开关：`-DryRun` 只跑检查、只打印，什么都不改；`-Build` 在打 tag 之前先本地
`build.ps1` + `--selfcheck` 走一遍（CI 也会自检，这只是让你省掉一次 5 分钟的往返）。
完整用法在脚本开头注释里。

> **任务列表从哪来**：`.vscode\tasks.json`（已进版本库）。它跟着**工作区根目录**走 ——
> 根目录必须是仓库根（就是 `jigu-app` 那个文件夹）才有这份。
> **建议直接把 `jigu-app` 文件夹作为工作区打开**：这样源代码管理面板、任务列表、
> 路径全在同一层。如果你打开的是外面那层壳，外层也放了一份等价的（路径多一层 `jigu-app\`）。
>
> 想真正一键：把下面这条加进 VS Code 的 `keybindings.json`
> （`Ctrl+Shift+P` → `Preferences: Open Keyboard Shortcuts (JSON)`）：
>
> ```json
> { "key": "ctrl+alt+r", "command": "workbench.action.tasks.runTask",
>   "args": "稽古: 发布新版本（打 tag → CI 构建发布）" }
> ```

推送 tag 后，GitHub Actions 会自动完成：
**在 Windows 机器上拉取 WebView2 SDK → 构建 → 校验 tag 与版本号一致 → 自检（`--selfcheck`）→ 生成更新清单 `app_version.json` → 创建 Release → 上传安装包 / 便携包 / SHA256 校验文件 / 更新清单与史料数据**。
全程约 3–6 分钟，不需要你做任何配置。挂上去之后，用户端程序的自动更新立刻就能发现这个版本。

### 为什么不再有 `PUSH.cmd` / `RELEASE.cmd`

它们做的有用的事（找 git、推 `main`、打并推 tag）都并进了 `tools\release.ps1`；
另外两件事是**有意去掉**的：

* **改 hosts 不再自动做**。`PUSH.cmd` 曾申请管理员权限，临时注释掉 hosts 里指向
  `127.0.0.1` 的几十条 GitHub 条目，推完再还原。发布脚本悄悄改这种安全相关的系统文件
  不划算 —— 现在连不上就停下，让你自己在看得见的窗口里跑 `fix-hosts.ps1`。
* **不再用 `-ExecutionPolicy Bypass`**。本机当前用户的执行策略已是 `RemoteSigned`，
  版本库里的脚本本来就能跑；真的被拦了应该 `Unblock-File` 解除文件的网络来源标记，
  而不是把执行策略这道闸整体关掉。

旧文件没有丢：`RELEASE.cmd` 在 git 历史里（`git show 82fad7d:RELEASE.cmd`），
`PUSH.cmd` 移到了仓库外层的 `_retired\` 目录。

---

## 二、发布前必须做的一件事：改版本号

### 1. 版本号只有一个真源

改 `build.ps1` 顶部的 `$version` 就够了：

```powershell
$version = "0.1.0"      # ← 只改这里
```

> 忘了改也能发：`tools\release.ps1 -Bump patch` 会自动 +1、提交这一次改动、再发布。
> 但不建议把它当默认 —— 版本号该由你决定，不该由机器猜。

构建时会生成 `src\Version.g.cs`，程序本体（`AppVer.Number`、用于自动更新的版本比对）
与安装向导（`SetupInfo.Version`）都引用它。**不再需要手工同步多个文件**——
以前漏改任何一处，自动更新都会空转或者死循环。

`src\Version.g.cs` 是生成物，已在 `.gitignore` 里，不要手工编辑。

### 2. tag 名要和版本号对得上（CI 会拦）

tag 用 `v` + 版本号，例如版本 `0.1.0` → tag `v0.1.0`。
workflow 只在 `v*` 的 tag 上触发。

CI 里有一道闸门：**tag 必须等于 `v$version`，否则直接失败**。
这是有意的 —— 版本号忘改的后果不是报错，而是发出去的包永远提示有新版本、
装完又回到旧版本号，无限循环。宁可让 CI 红给你看。

---

## 三、CI 里 WebView2 SDK 从哪来

构建只需要三个文件：

```
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll
```

托管 runner 上没有它们（本机是从 Office 安装目录里借的），所以 workflow 会：

1. 查询 NuGet 上 `Microsoft.Web.WebView2` 的最新版本；
2. 下载 `.nupkg` 并解压；
3. 把两个托管 DLL 从 `lib/net45/`、加载器从 `runtimes/win-x64/native/` 复制到仓库的 `lib/`；
4. 设置环境变量 `JIGU_WEBVIEW2_DIR=lib`，`build.ps1` 会优先从这里取。

**本地构建不受影响**：`build.ps1` 的查找顺序是
`JIGU_WEBVIEW2_DIR` → `<repo>\lib` → 本机 Office 安装目录。
所以你在自己机器上照旧 `build.ps1`，想让别的机器也能构建，只要把这三个 DLL 放进 `lib\`。

---

## 四、Release 里会有什么

| 资源 | 说明 |
| --- | --- |
| `jigu-installer-v0.1.0.exe` | 安装包（原文件名 `稽古-安装包.exe`，改成 ASCII 名是为了链接和下载方便） |
| `jigu-portable-v0.1.0.zip` | 便携包（解压即用） |
| `SHA256SUMS.txt` | 校验值，用户可 `certutil -hashfile` 比对；自动更新也从这里取安装包的哈希 |
| `app_version.json` | **自动更新清单**：版本 / 下载地址 / 哈希 / 更新说明 / `mandatory` |
| `data_version.json` | 史料数据清单（由 `build.ps1` 生成） |
| `corpus.json` · `labels.json` · `stopwords.json` | 史料与两张表，供数据通道增量下发 |

后五个文件是给**自动更新**用的。默认更新源是 `github:rdfghjgyuytytrudthgc/jigu-app`，
它读的就是「最新 Release」的附件，所以这几个文件挂上去之后，程序本体与史料两条通道
都零配置可用 —— 用户端不需要任何设置。

> `app_version.json` 的 `notes` 依次尝试：`CHANGELOG.md` 里该版本的小节 →
> 上一个 tag 以来的提交标题 → 一行 `稽古 vX.Y.Z 发布`。
> 仓库根目录已经有 `CHANGELOG.md` 了 —— **发版前把这一版的改动写进 `## 未发布` 一节，
> 再把它改名为 `## x.y.z — 日期`**（与 `build.ps1` 的版本号一致，否则取不到，
> `notes` 会悄悄退化成提交标题）。
> 格式约束、600 字上限等写在 `CHANGELOG.md` 顶部，别只看这里。

Release 正文是自动生成的固定内容（下载哪个、系统要求、校验方法、首次运行步骤，
末尾链到 `CHANGELOG.md`），写在 workflow 的 `Write release notes` 步骤里，要改直接改那段。

---

## 五、不想走 CI？手工发布也行

本机已经把产物构建好了：

```
dist\稽古-安装包.exe
dist\稽古-portable.zip
```

在 GitHub 网页上 **Releases → Draft a new release → 选择 tag → 上传这两个文件 → Publish** 即可。
CI 只是把这一步自动化了，不是必须的。

手工发布时**自动更新靠回退路径仍然可用**：程序找不到 `app_version.json` 就会从 Release
自己推导 —— 版本号取 tag，下载地址取名为 `jigu-installer-*.exe` 的附件，哈希从
`SHA256SUMS.txt` 里查。所以上面这两个文件名（ASCII、`jigu-installer-` 前缀）别改，
否则自动更新会找不到包。想显式控制更新说明，就再手工传一个 `app_version.json`
（格式见 `docs\更新服务托管说明.md`）。

---

## 六、常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| `tools\release.ps1` 说 `the working tree has uncommitted changes` | 还有没提交的改动。CI 构建的是 commit，这些改动不会进包 —— 先在源代码管理面板提交；确实只想发已提交的那版就加 `-AllowDirty` |
| 脚本说 `tag vX.Y.Z is already on origin` | 这个版本号已经发过。要发新版：`-Bump patch`，或改 `build.ps1` 顶部的版本号并提交 |
| 脚本 `warn` 说 origin 上还有一个不带 `v` 的 `0.1.0` | 本项目第一版是手工发的，tag 就叫 `0.1.0`（没带 `v`）。走新流程发的第一版请用 **`-Bump patch`（→ `0.1.1`）**，否则会出现两个共用 `0.1.0` 这个版本号的 Release，而自动更新读的是「最新」那一个 |
| 脚本说 `git fetch failed` | 连不上 GitHub。这台机器上 `github.com` 被 hosts 指到了 `127.0.0.1`，跑**仓库外层**的 `fix-hosts.ps1 -Mode disable`（需要管理员权限，推完记得 `-Mode restore`）；是凭据问题的话，Git for Windows 会弹登录窗口 |
| 脚本说 `local main is N commit(s) behind origin/main` | 远端有别的提交。先 `git pull --rebase origin main` 再来发 |
| VS Code 的任务列表里没有「稽古: 发布」 | 工作区根目录不是仓库根。把 `jigu-app` 文件夹作为工作区打开；如果你打开的是外层壳，外层也有一份等价的 `.vscode\tasks.json` |
| Actions 里没有跑 | tag 不是 `v*` 形式，或者 tag 推送时仓库里还没有 `.github/workflows/release.yml`（先推一次 `main`，再推 tag） |
| `tag 'v1.7.0' 与 build.ps1 里的版本号不一致` | 忘了改 `build.ps1` 顶部的 `$version`。改好、提交，再重新打 tag（或从 Actions 页 `workflow_dispatch` 指定 tag 重跑） |
| 用户端永远提示有新版本、装完还是旧版本号 | 同上：程序里认的版本号落后于 tag。这是自动更新唯一会「死循环」的情形，CI 的闸门就是为了拦它 |
| `csc.exe not found` | runner 镜像换了。workflow 用的是 `windows-2022`，如改成别的镜像需确认自带 .NET Framework 4.x |
| `WebView2Loader.dll not found inside the SDK package` | NuGet 包结构调整了，按 Actions 日志里打印的包内文件清单改 `Fetch the WebView2 SDK` 那一步的路径 |
| `selfcheck` 失败 | 语料或界面资源没随包打进去。看 Actions 日志里 `selfcheck.txt` 的内容，里面有具体哪一项 FAIL |
| Release 创建失败 `Resource not accessible` | 仓库 `Settings → Actions → General → Workflow permissions` 要选 **Read and write permissions** |
| 想删掉某个 Release | Releases 页面右上角 Delete；tag 也要删的话：`git push --delete origin v0.1.0` |

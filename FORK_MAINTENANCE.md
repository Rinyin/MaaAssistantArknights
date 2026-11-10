# Rinyin/MaaAssistantArknights 维护手册

本文件是本仓库（fork）的**唯一维护说明**，换会话、换人维护时从这里开始。
`AGENTS.md` 是给 AI 助手的入口，内容是本文件的摘要。

---

## 0. 新会话怎么开始

把下面这段话发给新会话即可（把版本号换掉）：

> 请先完整阅读 `/www/rinyi/MaaAssistantArknights/FORK_MAINTENANCE.md`，然后按第 4 节流程把上游 `v6.21.5` 同步到本仓库并发布。
> 遇到新的强制类弹窗/倒计时/引导，按第 3、5 节处理。发布前先给我看结果。

---

## 1. 项目定位（需求基本固定）

本仓库是上游 [MaaAssistantArknights/MaaAssistantArknights](https://github.com/MaaAssistantArknights/MaaAssistantArknights) 的定制版，**只有四条需求**：

1. **去掉强制类内容**（持续性主需求）：强制弹窗、强制阅读/倒计时、强制新手引导、反复打扰的启动提示。判断标准见第 3 节。
2. **软件本体只从本仓库更新**：客户端检查/下载本体更新只访问本仓库的 GitHub Release，绝不从上游（MaaApi、MaaRelease、Mirror酱 `MAA` 资源）下载本体覆盖本仓库的修改。
   **游戏资源热更新（MaaResource）和上游公告照常从上游获取。**
3. **只维护 Windows x64**：不处理 Linux / macOS / Android / ARM64 / MAAUnified / maa-cli。
4. **不使用 GitHub Actions、不处理 issue/PR、不做功能开发**：版本号与上游一致，在本地用脚本复用上游官方完整包生成并发布。

不在需求内的事（新功能、性能优化、修上游 bug、跟进上游 CI）都不要做。

---

## 2. 本仓库相对上游的全部定制修改

所有会进入发布包的修改**只在 `src/MaaWpfGui`（纯 C#）里**。这是本仓库能不编译 MaaCore、直接复用上游完整包的前提，**不要修改 C++ / 资源 / 其他目录来实现需求**。

每一条修改都由 `tools/ForkRelease/check_fork_invariants.py` 中的不变量自动校验（第 6 节）。

### 2.1 本体更新源（需求 2）

| 文件 | 修改 |
|---|---|
| `Constants/MaaUrls.cs` | 新增 `AppUpdateRepositoryOwner = "Rinyin"` 等常量（换仓库只改这里）；删除 `MirrorChyanAppUpdate` |
| `Helper/GitHubUpdateHelper.cs` | `GetLatestReleaseAsync` 改为读本仓库 `releases?per_page=30`，按渠道选择版本（stable 只取正式版 / beta 排除 nightly / alpha 全部，取 SemVer 最大）；没填 Token 时匿名请求（带 ETag 缓存）；`IsUpdateRepository` 只认本仓库，所以下载链接从代码上无法指向上游 |
| `ViewModels/Dialogs/VersionUpdateDialogViewModel.cs` | `CheckUpdate`、完整性修复只走本仓库 Release；删除 MaaApi / Mirror酱 本体更新代码；本仓库没有 Release 时视为“已是最新” |
| `ViewModels/UserControl/Settings/VersionUpdateSettingsUserControlModel.cs` | 手动检查本体更新不再因为没填 Mirror酱 CDK 被拦下 |

设置里的「更新源 / Mirror酱 CDK / GitHub Token」仍然保留：前两者只影响**资源更新**；GitHub Token 只用来提高检查本体更新时 GitHub API 的速率限制。

### 2.2 去强制化（需求 1）

| # | 原强制行为 | 现行为 | 文件 |
|---|---|---|---|
| 1 | 每次启动自动弹出公告窗口 | 启动时只在后台拉取公告，内容有更新时显示一条 Growl 提示（`Announcement.UpdatedTip`）| `ViewModels/UI/RootViewModel.cs` |
| 2 | 公告窗口隐藏关闭按钮、必须滚到底才能确定、连点 20 次才放行、复选框锁定 | 有关闭按钮，「确定」直接关闭，复选框随时可勾 | `Views/Dialogs/AnnouncementDialogView.xaml`、`ViewModels/Dialogs/AnnouncementDialogViewModel.cs` |
| 3 | 更新后首次启动自动弹出更新日志窗口 | 改为 Growl 提示（`VersionUpdate.UpdatedTip`），可在「设置 - 更新设置 - 更新日志」手动查看 | `VersionUpdateDialogViewModel.ShowUpdateOrDownload` |
| 4 | 新装首次启动强制显示设置指引（覆盖任务页、禁用开始按钮）| `Gui.GuideStep` 默认 = `GuideMaxStep`（视为已完成）；需要时在「设置 - 界面设置 - 重看设置指引」打开 | `Configuration/Global/Gui.cs` |
| 5 | 教程版本升级时强制重置已完成用户 | `OnDeserialized` 只更新版本号 | `Configuration/Global/Gui.cs` |
| 6 | 指引中必须先点开用户协议才能下一步 | 去掉门禁，链接和红点保留 | `ViewModels/UI/SettingsViewModel.cs` |
| 7 | 指引「任务设置」步必须完成演示操作才能下一步 | 「下一步」始终可点，提示仅供参考 | `Views/UserControl/GuideUserControl.xaml` |
| 8 | 「跳过」第 0–3 步不显示、点击还要确认 | 每一步都显示，点击直接跳过 | `ViewModels/UI/SettingsViewModel.cs` |
| 9 | 选了 Nightly 后每次启动弹内测提示，直到去「问题反馈」页点链接 | 只弹一次（5 种语言文案末句同步修改） | `ViewModels/UI/RootViewModel.cs`、语言文件 |

新增的本地化 key：`Announcement.UpdatedTip`、`VersionUpdate.UpdatedTip`（5 种语言）。

### 2.3 有意保留、不算强制的（不要删）

| 位置 | 原因 |
|---|---|
| 「启动后自动运行」10 秒倒计时（`AsstProxy` → `TaskQueueViewModel.ConfirmStartupAutoRunAsync`）| 用户自己开启的自动化功能，倒计时是可随时取消的反悔窗口 |
| 任务完成后关机/休眠倒计时（`TaskQueueViewModel.TimerCanceledAsync`）| 防误关机的取消窗口，删掉会直接关机 |
| 版本不一致、配置损坏、文件缺失修复、资源损坏、未知 DLL、VC 运行库缺失、安装位置错误等弹窗 | 真实错误/风险提示 |
| 完整包覆盖安装前的确认框 | 防止误删用户文件的确认 |
| 宿醉彩蛋、连点版本号彩蛋、成就提示 | 用户主动触发或不阻塞操作 |

### 2.4 其他（不进入发布包）

| 文件 | 说明 |
|---|---|
| `src/MaaWpfGui/MaaWpfGui.csproj` | 显式引用 `System.Security.Cryptography.Pkcs` 10.0.14，消除 MailKit 传递依赖的 NuGet 安全警告（进入发布包，替换 `externals/System.Security.Cryptography.Pkcs.dll`）。上游自己升级 MimeKit 后可以删掉 |
| `src/Python/asst/updater.py` | Python SDK 的本体更新也改读本仓库 Release（不进 Windows 包，仅保持一致） |
| `.github/workflows/*` | 早期为 CI 发布做过修改，**现已弃用 Actions**，内容无关紧要 |
| `tools/ForkRelease/` | 本地发布脚本、不变量检查、扫描基线 |
| `AGENTS.md`、`FORK_MAINTENANCE.md` | 维护文档 |

---

## 3. “强制类内容”判定标准

**属于强制类（要去掉或改成非阻塞）：**
- 启动时**自动**弹出的阅读类窗口/对话框（公告、协议、更新日志、教程、广告、宣传）；
- 关闭/确认按钮被隐藏、禁用，或必须**等待倒计时 / 滚动到底 / 连点多次 / 点开某个链接**才能放行；
- 不同意/未完成就退出程序或锁住主要功能；
- 强制新手引导（自动出现、不能跳过、有门禁、版本升级后强制重走）；
- 每次启动都重复出现、只能通过隐蔽操作关闭的提示；
- 自动打开浏览器/文档页面。

**不属于强制类（保留）：** 用户自己触发的窗口、真实错误提示、危险操作前的确认、用户开启的自动化功能前的可取消倒计时、自动消失的 Growl / Toast 通知。

**改法优先级：** 能改成“不自动出现 + 用户可手动打开”就不删功能；必要时用 Growl 轻提示替代弹窗；只弹一次的提示要真正记住“已显示”。

---

## 4. 例行流程：上游发布新版本 `vNEW` 时

上一个本仓库版本记为 `vPREV`（见第 8 节发布记录）。

### 4.1 环境准备（一次性）

```bash
# gh：细粒度 Token，仓库 Rinyin/MaaAssistantArknights，Contents = Read and write
gh auth login --hostname github.com --with-token    # 粘贴 Token 后回车，再按 Ctrl+D
gh auth status

# .NET SDK：版本必须与上游 ci.yml 中 "Generate global.json" 一致（当前 10.0.302）
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 10.0.302 --install-dir ~/.dotnet-10.0.302 --no-path
~/.dotnet-10.0.302/dotnet --version
```

> [!IMPORTANT]
> 上游如果改了 `ci.yml` 里 global.json 的 SDK 版本，要同时修改 `tools/ForkRelease/fork_release.py` 的 `DOTNET_SDK_VERSION` 并安装对应 SDK；
> 否则自包含运行时文件会与上游完整包不同（脚本会报“有 N 个文件与上游完整包不同”的警告）。

### 4.2 把上游 `vNEW` 合并到本仓库 `dev-v2`

**合并上游的 release tag，而不是上游 `dev-v2` 最新提交**。这样本仓库改动的基点正好是 `vNEW`，发布脚本生成的补丁可以原样应用到 `vNEW` 源码上。

```bash
cd /www/rinyi/MaaAssistantArknights
git pull --ff-only origin dev-v2
git remote get-url upstream 2>/dev/null || git remote add upstream https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
# 只取这个 tag 对应的提交，不在本地创建同名 tag（避免和本仓库发布时创建的同名 tag 冲突）
git fetch --no-tags upstream refs/tags/vNEW
git merge --no-edit FETCH_HEAD
```

冲突处理原则：
- `src/MaaWpfGui/**`：保留第 2 节的 fork 语义，同时吸收上游的其他改动；
- 语言文件 `Res/Localizations/*.xaml`：两边的 key 都保留，然后运行 `python3 tools/SortLocalizationKeys.py` 重排；
- `.github/**`：直接用上游版本（`git checkout --theirs .github`），Actions 已弃用；
- `AGENTS.md`、`FORK_MAINTENANCE.md`、`tools/ForkRelease/**`：保留本仓库版本（`--ours`）。

> [!NOTE]
> 不要用 GitHub 网页上的 “Sync fork” / `merge-upstream` API：它们会合并上游 `dev-v2` 的最新提交，可能带进上游下一个版本才有的 WPF/Core 联动改动，
> 导致发布脚本的补丁无法干净地应用到 `vNEW`。

### 4.3 审查与校验

```bash
# 1) fork 不变量 + 可疑强制类代码扫描（退出码 0 通过；1 不变量被破坏；2 有新的可疑代码）
python3 tools/ForkRelease/check_fork_invariants.py

# 2) 看上游这一版改了哪些 WPF 代码，重点检查是否新增了强制类内容（第 3 节标准）
gh api "repos/MaaAssistantArknights/MaaAssistantArknights/compare/vPREV...vNEW" -q '.files[].filename' | grep MaaWpfGui

# 3) 本地化校验
python3 tools/CheckStubLocalization.py
python3 tools/CheckLocalizationReferences.py
python3 tools/SortLocalizationKeys.py --check

# 4) 编译（Linux 可编译，必须 0 错误 0 警告；项目开启了 TreatWarningsAsErrors）
~/.dotnet-10.0.302/dotnet build src/MaaWpfGui/MaaWpfGui.csproj -c Release -p:Platform=x64 -p:EnableWindowsTargeting=true
```

- 不变量失败：说明上游改动覆盖了 fork 修改，按第 2 节重新改回。
- 扫描有新命中：逐条按第 3 节判断。是强制类就按第 5 节修改；确认无害就运行
  `python3 tools/ForkRelease/check_fork_invariants.py --update-baseline` 收录进基线（基线要随代码一起提交）。

### 4.4 提交并推送

```bash
git add -A && git commit -m "chore: sync upstream vNEW"
git push origin dev-v2              # origin 使用 SSH
```

### 4.5 生成并发布

```bash
# 先只在本地生成（产物：build/fork-release/vNEW/dist/）
python3 tools/ForkRelease/fork_release.py vNEW --dotnet ~/.dotnet-10.0.302/dotnet --ota-from vPREV
# 确认输出中“替换”的文件只有 MAA.dll、MAA.deps.json 等少数几个后发布
python3 tools/ForkRelease/fork_release.py vNEW --dotnet ~/.dotnet-10.0.302/dotnet --ota-from vPREV --publish
```

脚本做的事：从上游 `vNEW` 源码 + 本仓库 WPF 补丁编译 → 自动运行不变量检查 → 下载上游 `MAA-vNEW-win-x64.zip` → 替换变化的文件
（保留上游 `MAA.exe` 原生启动器）→ 生成完整包与相对 `vPREV` 的 OTA 包 → `gh release create`（更新日志沿用上游）。

- 补丁基于**上游 `vNEW` 的源码**而不是 `dev-v2` 最新代码，因为 `dev-v2` 可能已经包含上游下个版本的 WPF/Core 联动改动，搭配 `vNEW` 的 MaaCore 会出问题。
- 补丁只取 `HEAD` 已提交的 `src/MaaWpfGui` 改动，所以必须先完成 4.4。
- 本仓库没有 `vNEW` tag 时，`gh release create` 会在默认分支 HEAD 上创建该 tag。

### 4.6 验证

```bash
gh api repos/Rinyin/MaaAssistantArknights/releases/tags/vNEW -q '{prerelease, draft, assets: [.assets[] | {name, size}]}'
```

应看到 `prerelease: false`、`draft: false`，资产包含 `MAA-vNEW-win-x64.zip`（以及 `MAAComponent-OTA-vPREV_vNEW-win-x64.zip`）。
最后把本次发布补进第 8 节的发布记录并提交。

---

## 5. 发现新的强制类内容时怎么改

1. 在 `dev-v2` 的 `src/MaaWpfGui` 中修改（不要改其他目录）。
2. 需要新文案时，在 **5 个语言文件**（zh-cn / zh-tw / en-us / ja-jp / ko-kr）都加上，运行 `python3 tools/SortLocalizationKeys.py` 排序。
   注意：该脚本会把文件写成 LF，`.cs` / `.xaml` 按 `.gitattributes` 应为 CRLF，提交前确认没有整文件换行变化。
3. 在 `tools/ForkRelease/check_fork_invariants.py` 的 `INVARIANTS` 中**为这条修改加一条不变量**（`must_match` / `must_not_match` 正则），保证以后同步上游时能自动发现回归。
4. 更新扫描基线：`python3 tools/ForkRelease/check_fork_invariants.py --update-baseline`。
5. 按 4.3 编译与校验（0 错误 0 警告），更新本文件第 2.2 节表格，提交推送。
6. 需要立即生效时，按 4.5 重新发布：同版本号已发布过的话，先在 GitHub 上删除该 Release，再用 `--publish` 重新发布。或者等上游下一个版本一起发布。

---

## 6. 工具说明（`tools/ForkRelease/`）

| 文件 | 作用 |
|---|---|
| `fork_release.py` | 本地生成/发布 Windows x64 版本（见 4.5，`--help` 查看参数） |
| `check_fork_invariants.py` | ① 不变量：第 2 节每条修改的正则断言，失败退出码 1；② 可疑模式扫描（隐藏关闭按钮、倒计时、自动 ShowWindow、门禁 IsEnabled、用户协议、启动链 MessageBox 等），与基线比较，有新命中时退出码 2 |
| `forced_scan_baseline.txt` | 已人工审查、确认无害或有意保留的扫描命中 |
| `README.md` | 发布脚本的简要说明 |

---

## 7. 关键约束与坑

- **版本号**：本仓库版本号与上游一致。客户端用 SemVer 比较，必须**大于**已安装版本才会提示更新。
  从上游官方版首次切换到本仓库版本时，需要**覆盖安装一次**本仓库的完整包，之后才会从本仓库收到更新。
- **Release 必须是正式版**（非 draft、非 prerelease），资产名必须是 `MAA-vX.Y.Z-win-x64.zip` 和 `MAAComponent-OTA-<旧>_<新>-win-x64.zip`，客户端按名字匹配。
- **GitHub Actions 已在仓库设置中停用**，工作流文件保留但不会执行。
- **WPF 编译**：Linux 上需要加 `-p:EnableWindowsTargeting=true`；项目 `TreatWarningsAsErrors=true`，StyleCop 规则同样会报错。WPF 界面没有在 Linux 上实际运行过，发布前最好在 Windows 上实机检查一次。
- **换行**：`.cs` / `.xaml` 是 CRLF，`.yml` / `.md` / `.py` 是 LF（见 `.gitattributes`）。
- **浅克隆**：`/www/rinyi/MaaAssistantArknights` 是浅克隆（根提交显示为 grafted）。`fork_release.py` 通过 GitHub API 计算与上游的合并基点，要求该提交在本地存在；本地合并上游后会自然满足。
- **磁盘**：每个版本的工作目录 `build/fork-release/<tag>/` 约 1 GB，发布后可删除旧版本目录（`build/` 已被 git 忽略）。
- **Token 安全**：不要把 Token 发到聊天里，只用 `gh auth login --with-token` 在终端输入。

---

## 8. 发布记录

| 版本 | 日期 | 上游基点 | 替换的文件 | 完整包 SHA256 |
|---|---|---|---|---|
| v6.21.4 | 2026-11-06 | 上游 `v6.21.4` + fork 补丁（合并基点 `170d04de2`） | `MAA.dll`、`MAA.deps.json`、`externals/System.Security.Cryptography.Pkcs.dll` | `c93303fe129f3e70b4bb27ad505e47c80869d22193d149856c1298ce53c4472a` |
| v6.22.0 | 2026-11-11 | 上游 `v6.22.0` + fork 补丁（合并基点 `901e01c60`） | `MAA.dll`、`MAA.deps.json`、`externals/System.Security.Cryptography.Pkcs.dll` | `870e47974247a3e018cc36dc3f3d260afd7ea09afae74424d6d2346430a13c5f` |

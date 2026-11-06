# Rinyin/MaaAssistantArknights（fork）维护规范

> **必读：[`FORK_MAINTENANCE.md`](./FORK_MAINTENANCE.md)**。那里是完整的维护手册，包括定制修改清单、例行流程、判定标准和发布记录。本文件只是它的摘要。

## 固定需求（不要扩展）

1. **去掉强制类内容**（主需求）：启动时自动弹出的公告/协议/更新日志/教程，需要等待、滚动、连点或点开链接才能关闭的窗口，强制新手引导，反复打扰的启动提示。判定标准见手册第 3 节。
2. **软件本体只从本仓库 Release 更新**：不从上游 MaaApi / MaaRelease / Mirror酱 `MAA` 下载本体。游戏资源热更新（MaaResource）和上游公告照常从上游获取。
3. **只维护 Windows x64**。
4. **不用 GitHub Actions、不处理 issue/PR、不做新功能**。版本号与上游一致，用 `tools/ForkRelease/fork_release.py` 在本地复用上游官方完整包生成并发布。

## 硬性规则

- 所有定制修改只放在 `src/MaaWpfGui`（纯 C#），不要修改 MaaCore / 资源，否则无法复用上游完整包。
- 合并上游时合并**上游的 release tag**（`git fetch --no-tags upstream refs/tags/vNEW && git merge FETCH_HEAD`），不要合并上游 `dev-v2` 的最新提交。
- 每次合并上游后、发布前，都必须通过 `python3 tools/ForkRelease/check_fork_invariants.py`（退出码 0）。
- 新增一条去强制化修改时，要同时在 `check_fork_invariants.py` 里加不变量、更新扫描基线，并更新手册第 2.2 节。
- WPF 改动必须 0 错误 0 警告：
  `dotnet build src/MaaWpfGui/MaaWpfGui.csproj -c Release -p:Platform=x64 -p:EnableWindowsTargeting=true`（.NET SDK 10.0.302）。
- 新增文案要写全 5 种语言，并用 `python3 tools/SortLocalizationKeys.py` 排序；三个本地化校验脚本都要通过。
- 发布前先在本地生成（不加 `--publish`），给用户确认后再发布。不要在聊天中索要或接收 Token。
- 合并冲突时：`AGENTS.md`、`FORK_MAINTENANCE.md`、`tools/ForkRelease/**` 保留本仓库版本；`.github/**` 用上游版本。

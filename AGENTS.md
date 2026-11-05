# MaaAssistantArknights (Rinyin Fork) 同步与维护规范

本仓库（`Rinyin/MaaAssistantArknights`）是上游 `MaaAssistantArknights/MaaAssistantArknights` 的定制分支，所有版本同步与修改必须严格遵守以下规则：

## 1. 本体更新源与上游资源分离原则
- **客户端本体更新源**：必须指向本仓库 `Rinyin/MaaAssistantArknights`（`MaaUrls.cs`、`GitHubUpdateHelper.cs`、`VersionUpdateDialogViewModel.cs`、`VersionUpdateSettingsUserControlModel.cs`、`src/Python/asst/updater.py`），绝不能从上游仓库或 Mirror酱 下载客户端本体安装包或 OTA 包覆盖本地程序。
- **保留上游资源与通知**：游戏资源热更新（`ResourceUpdater.cs` 中的 `MaaResource` / Mirror酱 资源更新）与上游公告拉取（`AnnouncementDialogViewModel.cs`）必须保持从上游获取。
- **GitHub Actions 工作流**：
  - `.github/workflows/ci.yml`：保持 `macOS-GUI` 的 Apple 证书签名与公证仅在 `github.repository_owner == 'MaaAssistantArknights'` 时执行；`release` 任务的分发与上游 OTA 触发同样仅在上游执行，确保本仓库 push `v*` tag 时能顺利编译并发布 GitHub Release。
  - `.github/workflows/release-ota.yml`：保留 `make-ota-fork` 任务，在本仓库 Release 发布时自动生成并上传 OTA 增量包。
  - `.github/workflows/mirrorchyan_release_note.yml`：仅在上游仓库运行。

## 2. 强制性弹窗、倒计时与教程清理原则
在每次同步上游更新时，必须保留以下已完成去强制化的修改，并审查上游新增代码（`git diff <prev_tag>..<new_tag>`），一旦发现上游新增或修改了任何强制查看公告/协议、强制倒计时、强制教程或拦截弹窗，必须一并筛查并关闭/移除：
1. **启动公告与用户协议（`AnnouncementDialogView.xaml`、`AnnouncementDialogViewModel.cs`、`RootViewModel.cs`）**：
   - 启动时不自动弹出公告/协议模态窗口（仅在公告内容变化时以 Growl 轻提示通知）。
   - 公告窗口保留右上角关闭按钮（`ShowCloseButton="True"`），移除“必须滚动到底部才能点击确定”、“未读完点击时按钮锁死并变成‘你还没看完 / 还点’、需连点 20 次才放行”等强制阅读限制，“不再显示”复选框随时可勾选。
2. **新手引导与教程（`Gui.cs`、`RootViewModel.cs`、`SettingsViewModel.cs`、`GuideUserControl.xaml`）**：
   - `GuideStepIndex` 默认值保持为已完成（`-1`），新安装首次启动不强制进入设置引导；移除教程版本升级时强制重置老用户引导进度的逻辑。
   - 引导界面每一步均显示“跳过”按钮，点击“跳过”直接完成退出，不弹二次确认框；移除“必须先点击查看用户协议才能点下一步”等强制门禁。
3. **其他启动打扰弹窗（`RootViewModel.cs` 及 5 国语言包）**：
   - 版本更新后首次启动不强制弹出更新日志大窗口（改为 Growl 轻提示）。
   - 内测版（Beta/Alpha）启动提示最多只弹一次，不强制要求用户去问题反馈页点击链接才消除。

## 3. 同步上游新版本的标准步骤
当上游发布新版本 `<TAG>` 时：
1. `git fetch upstream --tags` 并 `git fetch upstream dev-v2`。
2. 查看上游从上一版本到 `<TAG>` 的改动：`git diff <PREV_TAG>..<TAG> -- src/ .github/`。
3. 在本地 `dev-v2` 分支合并 `<TAG>`（`git merge <TAG> --no-edit`），解决冲突时严格保留上述第 1、2 条定制修改。
4. 筛查上游在 `<PREV_TAG>..<TAG>` 中是否有新增的强制弹窗、倒计时或教程逻辑，如有则立即禁用或移除。
5. 运行本地化校验脚本：
   - `python3 tools/CheckStubLocalization.py`
   - `python3 tools/CheckLocalizationReferences.py`
   - `python3 tools/SortLocalizationKeys.py --check`
6. 提交更改到 `dev-v2`，将版本 tag `<TAG>` 指向最新提交（`git tag -f -a <TAG> -m "<TAG>"`），并推送至本仓库：
   `git push origin dev-v2 && git push -f origin refs/tags/<TAG>:refs/tags/<TAG>`。

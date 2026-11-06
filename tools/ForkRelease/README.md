# Fork 本地发布工具（Windows x64）

完整的维护流程见仓库根目录的 [`FORK_MAINTENANCE.md`](../../FORK_MAINTENANCE.md)。

| 文件 | 作用 |
|---|---|
| `fork_release.py` | 在本地生成并发布本仓库版本：上游同版本官方完整包 + 重新编译的 WPF |
| `check_fork_invariants.py` | 校验 fork 定制修改是否仍然生效，并扫描新增的可疑强制类代码 |
| `forced_scan_baseline.txt` | 扫描基线（已审查过的命中） |

## 原理

本仓库只修改了 WPF 客户端（`src/MaaWpfGui`，纯 C#），MaaCore 等原生组件与上游完全一致，所以：

- 版本号与上游相同；
- 发布包 = 上游 `MAA-vX.Y.Z-win-x64.zip` + 替换重新编译后变化的 WPF 文件（通常只有 `MAA.dll`、`MAA.deps.json` 等），
  上游的 `MAA.exe`（原生启动器）保留不动；
- WPF 用“上游 `vX.Y.Z` 源码 + 本仓库 `src/MaaWpfGui` 补丁”编译，保证与该版本 MaaCore 匹配；
- 使用与上游 CI 相同的 .NET SDK（`10.0.302`），这样自包含运行时文件与上游逐字节相同。

## 用法

```bash
python3 tools/ForkRelease/check_fork_invariants.py                       # 校验 fork 修改
python3 tools/ForkRelease/fork_release.py vX.Y.Z --dotnet <SDK>/dotnet   # 只在本地生成
python3 tools/ForkRelease/fork_release.py vX.Y.Z --dotnet <SDK>/dotnet --ota-from vPREV --publish
```

依赖：`git`、已登录的 `gh`（Token 需要本仓库 Contents 读写权限）、.NET SDK 10.0.302、Python 3。

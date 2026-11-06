#!/usr/bin/env python3
"""校验本仓库（fork）的定制修改是否仍然生效，并扫描可疑的强制性弹窗 / 倒计时 / 引导。

两部分：
1. 不变量（INVARIANTS）：fork 的核心修改，任何一条不满足即失败（退出码 1）。
   同步上游后、发布前都必须通过。
2. 可疑模式扫描（SCAN_PATTERNS）：用正则列出可能属于“强制类”的代码，与
   forced_scan_baseline.txt（已人工审查过的命中）比较，只报告新增命中（退出码 2）。
   新增命中需要人工判断：属于强制类就修改代码去掉；确认无害就用 --update-baseline 收录。

用法：
  python3 tools/ForkRelease/check_fork_invariants.py                  # 检查本仓库工作区
  python3 tools/ForkRelease/check_fork_invariants.py --root <目录>     # 检查其他源码目录（需包含 src/MaaWpfGui）
  python3 tools/ForkRelease/check_fork_invariants.py --update-baseline # 审查后把当前命中写入基线
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASELINE = Path(__file__).resolve().parent / "forced_scan_baseline.txt"
GUI = "src/MaaWpfGui"


@dataclass(frozen=True)
class Invariant:
    file: str  # 相对 src/MaaWpfGui
    description: str
    must_match: tuple[str, ...] = ()
    must_not_match: tuple[str, ...] = ()


INVARIANTS: tuple[Invariant, ...] = (
    # ---------------- 软件本体更新源：只从本仓库 Release 更新
    Invariant(
        "Constants/MaaUrls.cs",
        "本体更新源指向本仓库，且不再保留 Mirror酱 本体更新接口",
        must_match=(r'AppUpdateRepositoryOwner\s*=\s*"Rinyin"', r'AppUpdateRepositoryName\s*=\s*"MaaAssistantArknights"'),
        must_not_match=(r"MirrorChyanAppUpdate",),
    ),
    Invariant(
        "Helper/GitHubUpdateHelper.cs",
        "更新包只允许来自本仓库 Release；不再读取上游 MaaRelease 发布清单",
        must_match=(r"owner\.Equals\(MaaUrls\.AppUpdateRepositoryOwner", r"MaaUrls\.AppUpdateReleasesApi"),
        must_not_match=(r"MaaRelease/contents", r'repository\.Equals\("MaaRelease"'),
    ),
    Invariant(
        "ViewModels/Dialogs/VersionUpdateDialogViewModel.cs",
        "本体更新不走上游 MaaApi / Mirror酱；更新后首次启动不自动弹出更新日志窗口",
        must_match=(r"GitHubUpdateHelper\.GetLatestReleaseAsync\(request,", r'"VersionUpdate\.UpdatedTip"'),
        must_not_match=(r"version/summary\.json", r"CheckUpdateByMaaApi", r"CheckUpdateByMirrorChyan",
                        r"MirrorChyanAppUpdate", r"WindowManager\.ShowWindow\(this\)"),
    ),
    # ---------------- 公告：启动不自动弹出，窗口随时可关
    Invariant(
        "ViewModels/UI/RootViewModel.cs",
        "启动时不自动弹出公告窗口（仅 Growl 提示）；内测版提示只弹一次",
        must_match=(r'"Announcement\.UpdatedTip"', r"HasAcknowledgedNightlyWarning\s*=\s*true"),
        must_not_match=(r"ShowWindow\(Instances\.AnnouncementDialogViewModel\)",),
    ),
    Invariant(
        "Views/Dialogs/AnnouncementDialogView.xaml",
        "公告窗口保留关闭按钮，“不再显示”复选框不受滚动限制",
        must_not_match=(r'ShowCloseButton="False"', r"IsEnabled=\"\{c:Binding 'HasEverScrolledToBottom"),
    ),
    Invariant(
        "ViewModels/Dialogs/AnnouncementDialogViewModel.cs",
        "公告“确定”直接关闭，不要求滚动到底部 / 连点",
        must_match=(r"public void Close\(\)\s*\{\s*RequestClose\(\);\s*\}",),
        must_not_match=(r"_notFinishedMessages", r"Announcement\.NotFinishedConfirm"),
    ),
    # ---------------- 设置指引：默认不显示、不强制
    Invariant(
        "Configuration/Global/Gui.cs",
        "设置指引默认视为已完成；教程版本升级不重置进度",
        must_match=(r"public int GuideStep \{ get; set; \} = GuideMaxStep;",),
        must_not_match=(r"GuideStep\s*=\s*ViewModels\.UI\.SettingsViewModel",),
    ),
    Invariant(
        "ViewModels/UI/SettingsViewModel.cs",
        "设置指引随时可跳过（无确认框）；不要求先点开用户协议",
        must_match=(r"public bool GuideSkipVisible => true;", r"public void SkipGuide\(\)\s*\{\s*DoneGuide\(\);\s*\}"),
        must_not_match=(r'"Guide\.Agreement\.Tip"', r'"Guide\.SkipPrompt"'),
    ),
    Invariant(
        "Views/UserControl/GuideUserControl.xaml",
        "设置指引“下一步”不再被引导关卡禁用",
        must_not_match=(r"IsEnabled=\"\{c:Binding 'GuideNextGateTip == null'\}\"",),
    ),
)

# 可疑“强制类”模式：命中不代表一定有问题，与基线比对后只报告新增命中
SCAN_PATTERNS: tuple[tuple[str, str], ...] = (
    ("hide-close-button", r'ShowCloseButton="False"'),
    ("countdown", r"Countdown|倒计时|TextWithTimerDialogView\("),
    ("auto-show-window", r"WindowManager\.Show(Window|Dialog)\("),
    ("gated-enable", r"IsEnabled=\"\{c:Binding [^\"]*(Scroll|Read|Timer|Countdown|Second|Agree|Gate)"),
    ("agreement", r"Agreement|用户协议"),
    ("force-stay-open", r"forceStayOpen:\s*true"),
    ("block-close", r"e\.Cancel\s*=\s*true"),
    ("guide-reset", r"GuideStep\s*=|GuideStepIndex\s*=\s*0"),
    ("open-browser", r"Process\.Start\(new ProcessStartInfo\((?!url\b)[^)]*(Url|Uri|http)"),
    ("startup-messagebox", r"MessageBoxHelper\.Show\("),  # 仅统计启动相关文件，见 SCAN_STARTUP_ONLY
)
# 这些模式只在启动链相关文件中统计（全局统计噪音太大）
SCAN_STARTUP_ONLY = {"startup-messagebox": ("ViewModels/UI/RootViewModel.cs", "Main/Bootstrapper.cs", "App.xaml.cs")}


def check_invariants(gui: Path) -> list[str]:
    failures = []
    for inv in INVARIANTS:
        path = gui / inv.file
        if not path.exists():
            failures.append(f"[缺失] {inv.file}：文件不存在（{inv.description}）")
            continue
        text = path.read_text(encoding="utf-8-sig")
        for pattern in inv.must_match:
            if not re.search(pattern, text):
                failures.append(f"[缺少] {inv.file}：未找到 /{pattern}/ —— {inv.description}")
        for pattern in inv.must_not_match:
            m = re.search(pattern, text)
            if m:
                line = text.count("\n", 0, m.start()) + 1
                failures.append(f"[回归] {inv.file}:{line}：出现 /{pattern}/ —— {inv.description}")
    return failures


def scan(gui: Path) -> set[str]:
    hits = set()
    for path in sorted(gui.rglob("*")):
        if path.suffix not in (".cs", ".xaml") or "Res/Localizations" in path.as_posix() or not path.is_file():
            continue
        rel = path.relative_to(gui).as_posix()
        if "/obj/" in f"/{rel}" or "/bin/" in f"/{rel}":
            continue
        lines = path.read_text(encoding="utf-8-sig").splitlines()
        # 跳过注释行，只看代码
        lines = [l for l in lines if not l.lstrip().startswith(("//", "*", "/*", "<!--"))]
        for name, pattern in SCAN_PATTERNS:
            only = SCAN_STARTUP_ONLY.get(name)
            if only and rel not in only:
                continue
            regex = re.compile(pattern)
            for line in lines:
                if regex.search(line):
                    hits.add(f"{name}\t{rel}\t{line.strip()}")
    return hits


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=ROOT, help="源码根目录（默认本仓库）")
    parser.add_argument("--update-baseline", action="store_true", help="把当前扫描命中写入基线（需先人工审查）")
    args = parser.parse_args()

    gui = args.root.resolve() / GUI
    if not gui.is_dir():
        print(f"找不到 {gui}", file=sys.stderr)
        return 1

    failures = check_invariants(gui)
    if failures:
        print("✗ fork 不变量检查失败：")
        for f in failures:
            print("  " + f)
    else:
        print(f"✓ fork 不变量检查通过（{len(INVARIANTS)} 项）")

    hits = scan(gui)
    if args.update_baseline:
        BASELINE.write_text(
            "# 已人工审查、确认不属于强制类（或有意保留）的扫描命中。格式：模式\\t文件\\t去掉首尾空白的代码行\n"
            + "".join(f"{h}\n" for h in sorted(hits)),
            encoding="utf-8",
        )
        print(f"已写入基线 {BASELINE.relative_to(ROOT)}（{len(hits)} 条）")
        return 1 if failures else 0

    baseline = set()
    if BASELINE.exists():
        baseline = {l for l in BASELINE.read_text(encoding="utf-8").splitlines() if l and not l.startswith("#")}
    new_hits = sorted(hits - baseline)
    if new_hits:
        print(f"! 发现 {len(new_hits)} 条新的可疑“强制类”代码，需人工审查：")
        for h in new_hits:
            name, rel, line = h.split("\t", 2)
            print(f"  [{name}] {rel}: {line}")
        print("  属于强制类 → 修改代码移除；确认无害 → 运行 --update-baseline 收录")
    else:
        print("✓ 未发现新的可疑“强制类”代码")

    if failures:
        return 1
    return 2 if new_hits else 0


if __name__ == "__main__":
    sys.exit(main())

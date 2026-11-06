#!/usr/bin/env python3
"""不经过 GitHub Actions，在本地生成并发布本仓库（fork）的 Windows x64 版本。

本仓库只修改了 WPF 客户端（src/MaaWpfGui，纯 C#），MaaCore 等原生组件与上游完全一致，
所以每个版本都可以直接复用上游官方发布的完整包，只替换重新编译的 WPF 文件，版本号与上游保持一致：

1. 稀疏克隆上游指定 tag 的 src/MaaWpfGui；
2. 把本仓库对 src/MaaWpfGui 的改动（相对上游 dev-v2 的合并基点）打成补丁应用上去，
   并用 check_fork_invariants.py 校验 fork 定制修改仍然生效、没有新增可疑的强制类代码；
3. 用与上游 CI 相同版本的 .NET SDK 发布 WPF（Linux 上也可编译）；
4. 下载上游同版本的 MAA-<tag>-win-x64.zip，用编译结果覆盖其中发生变化的文件，
   保留上游的 MAA.exe（原生启动器 MaaAppHostStub，不是 SDK apphost）；
5. 生成 MAA-<tag>-win-x64.zip，可选再生成相对上一个 fork 版本的 OTA 增量包；
6. 加 --publish 时用 gh 在本仓库创建 Release 并上传（默认只在本地生成，不发布）。

用法：
  python3 tools/ForkRelease/fork_release.py v6.21.4                     # 只在本地生成
  python3 tools/ForkRelease/fork_release.py v6.21.5 --ota-from v6.21.4  # 同时生成 OTA 增量包
  python3 tools/ForkRelease/fork_release.py v6.21.4 --publish           # 生成并发布

依赖：git、gh（已登录）、.NET SDK（版本与上游 CI 一致，默认 10.0.302，可用 --dotnet 指定）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FORK_REPO = "Rinyin/MaaAssistantArknights"
UPSTREAM_REPO = "MaaAssistantArknights/MaaAssistantArknights"
UPSTREAM_URL = f"https://github.com/{UPSTREAM_REPO}.git"
UPSTREAM_BRANCH = "dev-v2"
GUI_DIR = "src/MaaWpfGui"
ARCH = "x64"
# 与上游 ci.yml 中 "Generate global.json" 保持一致，保证 .NET 运行时等文件与上游完整包逐字节相同
DOTNET_SDK_VERSION = "10.0.302"
# 保留上游完整包中的这些文件（MAA.exe 是 CI 用 MaaAppHostStub 替换后的原生启动器）
KEEP_UPSTREAM = {"MAA.exe"}


def run(cmd: list[str], cwd: Path | None = None, capture: bool = False) -> str:
    print("+", " ".join(cmd), flush=True)
    result = subprocess.run(cmd, cwd=cwd, check=True, text=True, capture_output=capture)
    return result.stdout if capture else ""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def list_files(root: Path) -> dict[str, Path]:
    return {p.relative_to(root).as_posix(): p for p in root.rglob("*") if p.is_file()}


def semver_core(tag: str) -> str:
    core = tag.lstrip("v").split("-")[0].split("+")[0]
    parts = core.split(".")
    if len(parts) < 3 or not all(p.isdigit() for p in parts[:3]):
        sys.exit(f"tag {tag} 不是 vX.Y.Z 形式的版本号")
    return ".".join(parts[:3])


def check_dotnet(dotnet: str) -> None:
    version = run([dotnet, "--version"], capture=True).strip()
    if version != DOTNET_SDK_VERSION:
        sys.exit(
            f".NET SDK 版本为 {version}，需要 {DOTNET_SDK_VERSION}（与上游 CI 一致，"
            "否则自包含运行时文件会与上游完整包不同）。可用 --dotnet 指定 SDK 路径，安装方式：\n"
            f"  bash dotnet-install.sh --version {DOTNET_SDK_VERSION} --install-dir <dir>"
        )


def resolve_patch_base(explicit: str | None) -> str:
    if explicit:
        base = explicit
    else:
        # 本仓库相对上游 dev-v2 的合并基点：其后的提交即本仓库自己的改动
        fork_owner = FORK_REPO.split("/")[0]
        base = run(
            ["gh", "api", f"repos/{UPSTREAM_REPO}/compare/{UPSTREAM_BRANCH}...{fork_owner}:{UPSTREAM_BRANCH}",
             "-q", ".merge_base_commit.sha"],
            capture=True,
        ).strip()
    try:
        run(["git", "cat-file", "-e", f"{base}^{{commit}}"], cwd=ROOT)
    except subprocess.CalledProcessError:
        sys.exit(f"本地仓库缺少合并基点提交 {base}，请先 git fetch，或用 --patch-base 指定")
    return base


def make_patch(base: str, out: Path) -> None:
    diff = run(["git", "diff", "--binary", base, "HEAD", "--", GUI_DIR], cwd=ROOT, capture=True)
    status = run(["git", "status", "--porcelain", "--", GUI_DIR], cwd=ROOT, capture=True)
    if status.strip():
        print(f"警告：{GUI_DIR} 有未提交的改动，补丁只包含已提交的内容（HEAD）", file=sys.stderr)
    if not diff.strip():
        sys.exit(f"{base}..HEAD 在 {GUI_DIR} 下没有改动，无需构建 fork 版本")
    out.write_text(diff, encoding="utf-8", newline="\n")


def prepare_source(tag: str, src: Path, patch: Path) -> None:
    if src.exists():
        shutil.rmtree(src)
    run(["git", "clone", "-q", "--depth", "1", "--branch", tag, "--filter=blob:none", "--sparse", UPSTREAM_URL, str(src)])
    run(["git", "sparse-checkout", "set", GUI_DIR], cwd=src)
    # 先应用到索引再检出，由 .gitattributes 还原 CRLF，避免补丁与工作区换行不一致
    run(["git", "apply", "--cached", str(patch)], cwd=src)
    run(["git", "checkout", "--", GUI_DIR], cwd=src)


def publish_gui(dotnet: str, tag: str, src: Path, out: Path) -> None:
    if out.exists():
        shutil.rmtree(out)
    version = semver_core(tag)
    cmd = [
        dotnet, "publish", f"{GUI_DIR}/MaaWpfGui.csproj",
        "-c", "Release", f"-p:Platform={ARCH}", "-p:EnableWindowsTargeting=true",
        # 等价于上游 CI 的 "Taggify version for csproj"
        f"-p:InformationalVersion={tag}", f"-p:Version={version}",
        f"-p:FileVersion={version}", f"-p:AssemblyVersion={version}",
        "-o", str(out),
    ]
    os.environ.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    run(cmd, cwd=src)


def download_asset(repo: str, tag: str, pattern: str, dest: Path) -> Path:
    dest.mkdir(parents=True, exist_ok=True)
    target = dest / pattern
    if target.exists():
        print(f"复用已下载的 {target}")
        return target
    run(["gh", "release", "download", tag, "--repo", repo, "--pattern", pattern, "--dir", str(dest)])
    return target


def build_package(upstream_zip: Path, publish_dir: Path, stage: Path, out_zip: Path) -> list[str]:
    if stage.exists():
        shutil.rmtree(stage)
    with zipfile.ZipFile(upstream_zip) as z:
        z.extractall(stage)
    upstream_files = list_files(stage)

    changed, added = [], []
    for rel, path in sorted(list_files(publish_dir).items()):
        if rel.endswith(".pdb") or rel in KEEP_UPSTREAM:
            continue
        target = stage / rel
        if rel in upstream_files:
            if sha256(path) == sha256(target):
                continue
            changed.append(rel)
        else:
            added.append(rel)
            target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)

    if added:
        # 文件集合变化时把新文件补进 filelist.txt（启动时据此检查安装完整性）
        filelist = stage / "filelist.txt"
        lines = [l for l in filelist.read_text(encoding="utf-8").splitlines() if l]
        lines = sorted(set(lines) | set(added), key=str.lower)
        filelist.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8", newline="")

    if out_zip.exists():
        out_zip.unlink()
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for rel, path in sorted(list_files(stage).items()):
            z.write(path, rel)

    for rel in changed:
        print(f"  替换  {rel}")
    for rel in added:
        print(f"  新增  {rel}")
    if len(changed) + len(added) > 20:
        print(
            f"警告：有 {len(changed) + len(added)} 个文件与上游完整包不同（正常只有 MAA.dll 等少数几个）。"
            "多半是 .NET SDK 版本与上游 CI 不一致，请核对上游 ci.yml 中 global.json 的 SDK 版本",
            file=sys.stderr,
        )
    return changed + added


def build_ota(from_zip: Path, to_zip: Path, out_zip: Path) -> None:
    """与 tools/OTAPacker/zipota.sh 相同的规则：去掉新旧包中同名且 CRC 相同的条目，
    去掉目录条目，附加 removelist.txt 列出新包中已不存在的旧文件。"""
    with zipfile.ZipFile(from_zip) as zf:
        old = {i.filename: i.CRC for i in zf.infolist() if not i.is_dir()}
    with zipfile.ZipFile(to_zip) as zt, zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zo:
        new_names = set()
        for info in zt.infolist():
            if info.is_dir():
                continue
            new_names.add(info.filename)
            if old.get(info.filename) == info.CRC:
                continue
            zo.writestr(info, zt.read(info))
        removed = sorted(set(old) - new_names)
        zo.writestr("removelist.txt", "".join(f"{name}\n" for name in removed))
    print(f"  OTA 包 {out_zip.name}：删除 {len(removed)} 个旧文件")


def release_notes(tag: str, dest: Path) -> Path:
    body = run(["gh", "release", "view", tag, "--repo", UPSTREAM_REPO, "--json", "body", "-q", ".body"], capture=True)
    note = (
        f"> 本版本基于上游 [{UPSTREAM_REPO} {tag}](https://github.com/{UPSTREAM_REPO}/releases/tag/{tag}) 官方完整包，"
        f"仅替换了本仓库修改过的 WPF 客户端；软件本体从本仓库更新，资源更新与公告仍来自上游。\n\n"
    )
    path = dest / "release-notes.md"
    path.write_text(note + body, encoding="utf-8")
    return path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("tag", help="上游版本号，如 v6.21.4（fork 版本号与上游保持一致）")
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"), help="dotnet 可执行文件路径")
    parser.add_argument("--patch-base", help="本仓库改动的基点提交（默认取与上游 dev-v2 的合并基点）")
    parser.add_argument("--ota-from", help="上一个 fork 版本号，生成从该版本到本版本的 OTA 增量包")
    parser.add_argument("--workdir", type=Path, help="工作目录（默认 build/fork-release/<tag>）")
    parser.add_argument("--publish", action="store_true", help="生成后在本仓库创建 Release 并上传")
    parser.add_argument("--accept-scan", action="store_true", help="可疑强制类代码扫描有新增命中时仍然发布")
    args = parser.parse_args()

    tag = args.tag
    semver_core(tag)
    work = (args.workdir or ROOT / "build" / "fork-release" / tag).resolve()
    work.mkdir(parents=True, exist_ok=True)
    dist = work / "dist"
    dist.mkdir(exist_ok=True)

    check_dotnet(args.dotnet)
    base = resolve_patch_base(args.patch_base)
    print(f"本仓库改动基点：{base}")

    patch = work / "fork-wpf.patch"
    make_patch(base, patch)
    prepare_source(tag, work / "src", patch)

    # 发布前校验：fork 不变量必须全部满足；上游新增的可疑强制类代码必须先审查
    check = subprocess.run([sys.executable, str(Path(__file__).with_name("check_fork_invariants.py")),
                            "--root", str(work / "src")])
    if check.returncode == 1:
        sys.exit("fork 不变量检查失败：上游改动覆盖了本仓库的定制修改，请先在 dev-v2 修复")
    if check.returncode == 2 and args.publish and not args.accept_scan:
        sys.exit("发现新的可疑强制类代码：请审查（必要时修改代码或更新基线）后重试，或加 --accept-scan 强制发布")

    publish_gui(args.dotnet, tag, work / "src", work / "publish")

    package_name = f"MAA-{tag}-win-{ARCH}.zip"
    upstream_zip = download_asset(UPSTREAM_REPO, tag, package_name, work / "upstream")
    package = dist / package_name
    print(f"生成 {package}")
    changed = build_package(upstream_zip, work / "publish", work / "stage", package)
    if not changed:
        sys.exit("编译结果与上游完整包完全相同，本仓库改动没有生效，已中止")

    assets = [package]
    if args.ota_from:
        prev_zip = download_asset(FORK_REPO, args.ota_from, f"MAA-{args.ota_from}-win-{ARCH}.zip", work / "previous")
        ota = dist / f"MAAComponent-OTA-{args.ota_from}_{tag}-win-{ARCH}.zip"
        build_ota(prev_zip, package, ota)
        assets.append(ota)

    summary = {"tag": tag, "patch_base": base, "assets": [str(a) for a in assets], "changed_files": changed}
    (work / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")

    if not args.publish:
        print("\n已在本地生成（未发布）：")
        for asset in assets:
            print(f"  {asset}")
        print(f"确认无误后加 --publish 重新运行，或手动：gh release create {tag} --repo {FORK_REPO} <文件>")
        return

    notes = release_notes(tag, work)
    prerelease = ["--prerelease"] if "-" in tag.lstrip("v") else []
    run(["gh", "release", "create", tag, "--repo", FORK_REPO, "--title", tag,
         "--notes-file", str(notes), *prerelease, *map(str, assets)])
    print(f"已发布：https://github.com/{FORK_REPO}/releases/tag/{tag}")


if __name__ == "__main__":
    main()

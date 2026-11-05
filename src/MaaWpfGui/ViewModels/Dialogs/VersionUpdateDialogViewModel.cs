// <copyright file="VersionUpdateDialogViewModel.cs" company="MaaAssistantArknights">
// Part of the MaaWpfGui project, maintained by the MaaAssistantArknights team (Maa Team)
// Copyright (C) 2021-2026 MaaAssistantArknights Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License v3.0 only as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using MaaWpfGui.Configuration.Factory;
using MaaWpfGui.Constants;
using MaaWpfGui.Constants.Enums;
using MaaWpfGui.Extensions;
using MaaWpfGui.Helper;
using MaaWpfGui.Main;
using MaaWpfGui.Models;
using MaaWpfGui.Services;
using MaaWpfGui.Services.Web;
using MaaWpfGui.States;
using MaaWpfGui.Utilities;
using MaaWpfGui.ViewModels.UI;
using MaaWpfGui.ViewModels.UserControl.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using Serilog;
using Stylet;
using static MaaWpfGui.Models.UpdateCredentialModel;

namespace MaaWpfGui.ViewModels.Dialogs;

/// <summary>
/// The view model of version update.
/// </summary>
public partial class VersionUpdateDialogViewModel : Screen
{
    private readonly RunningState _runningState;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionUpdateDialogViewModel"/> class.
    /// </summary>
    public VersionUpdateDialogViewModel()
    {
        _runningState = RunningState.Instance;
    }

    private static readonly ILogger _logger = Log.ForContext<VersionUpdateDialogViewModel>();

    private static readonly string _contributorAvatarDir = Path.Combine(PathsHelper.CacheDir, "contributor");

    private const string ContributorAvatarPlaceholderName = "_placeholder.png";

    // 32×32 全透明 PNG；头像未下载时占住 16px 位置，下载完成前后布局零跳动
    private const string PlaceholderAvatarBase64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAGklEQVR4nO3BAQEAAACCIP+vbkhAAQAAAO8GECAAARlDNO4AAAAASUVORK5CYII=";

    private static readonly HashSet<string> _downloadingAvatars = [];

    private static string FormatUpdateInfo(string text)
    {
        // MdXaml.Html 的 DetailsParser 用 bool.TryParse 解析 open 属性值，HTML 无值写法 <details open>
        // 解析出空串导致 TryParse 失败、Expander 恒为折叠；改写成带值形式使其自动展开
        text = MyRegex().Replace(text, "open=\"true\"");
        return AddContributorLink(text);
    }

    private static string AddContributorLink(string text)
    {
        /*
        //        "@ " -> "@ "
        //       "`@`" -> "`@`"
        //   "@MistEO" -> "![avatar](path "MistEO"){…}[@MistEO](https://github.com/MistEO)"
        // "[@MistEO]" -> "[@MistEO]"
        */
        // 头像图片与用户名链接平级不嵌套：MdXaml 的内联匹配正则带 Singleline，
        // 嵌套图片链接在 LF 行尾（GitHub/MirrorChyan 的 release body）下会跨行吞掉后续行的内容
        return Regex.Replace(text, @"([^\[`]|^)@([^\s]+)", m => {
            var user = m.Groups[2].Value;
            var avatar = GetContributorAvatarMarkdown(user);
            return $"{m.Groups[1].Value}{avatar}[@{user}](https://github.com/{user})";
        });
    }

    /// <summary>
    /// 生成贡献者头像的内联图片 Markdown：缓存命中用真头像，否则用透明占位图；
    /// 非 GitHub 用户名形状的捕获（可能带尾随标点）不插图，保持纯链接。
    /// </summary>
    private static string GetContributorAvatarMarkdown(string user)
    {
        if (!Regex.IsMatch(user, @"^[a-zA-Z0-9-]+$") || !EnsurePlaceholderAvatar())
        {
            return string.Empty;
        }

        string avatarPath = Path.Combine(_contributorAvatarDir, user + ".png");
        string effectivePath = File.Exists(avatarPath)
            ? avatarPath
            : Path.Combine(_contributorAvatarDir, ContributorAvatarPlaceholderName);

        // 路径用正斜杠，Markdown 中反斜杠是转义字符；尺寸语法 {width=16px} 由 MdXaml 的 ImageResizeExt 渲染；
        // title（即渲染后的 ToolTip）携带用户名，供头像下载完成后在已渲染文档中定位占位图换源
        return $"![avatar]({effectivePath.Replace('\\', '/')} \"{user}\"){{width=16px height=16px}}";
    }

    private static bool _placeholderAvatarReady;

    private static bool EnsurePlaceholderAvatar()
    {
        if (_placeholderAvatarReady)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(_contributorAvatarDir);
            string path = Path.Combine(_contributorAvatarDir, ContributorAvatarPlaceholderName);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, Convert.FromBase64String(PlaceholderAvatarBase64));
            }

            _placeholderAvatarReady = true;
        }
        catch (Exception e)
        {
            // 写盘失败（目录只读等）后不再重试，头像整体退化为纯链接
            _logger.Warning(e, "Failed to create placeholder contributor avatar");
        }

        return _placeholderAvatarReady;
    }

    /// <summary>
    /// 后台下载 changelog 中缺失的贡献者头像（走 GitHub 用户名头像直链，磁盘缓存命中即跳过，每个用户只下一次）。
    /// 每落盘一个就在已渲染文档中把对应占位图换成真头像（不重建 FlowDocument，折叠/滚动状态不受影响）；
    /// 失败静默，下次展示时重试。
    /// </summary>
    /// <param name="markdown">要提取用户名的 changelog 文本；传 null 时取当前 <see cref="UpdateInfo"/>（设置页更新日志按钮等只展示不写入的场景）。</param>
    /// <returns>Task</returns>
    public async Task DownloadMissingContributorAvatarsAsync(string? markdown = null)
    {
        markdown ??= UpdateInfo;
        var users = Regex.Matches(markdown, @"@([a-zA-Z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        foreach (var user in users)
        {
            string path = Path.Combine(_contributorAvatarDir, user + ".png");
            if (File.Exists(path))
            {
                continue;
            }

            lock (_downloadingAvatars)
            {
                if (!_downloadingAvatars.Add(user))
                {
                    continue;
                }
            }

            try
            {
                using var response = await Instances.HttpService.GetAsync(new Uri($"https://github.com/{user}.png?size=32")).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Directory.CreateDirectory(_contributorAvatarDir);
                    string tempPath = path + ".temp";
                    await File.WriteAllBytesAsync(tempPath, content).ConfigureAwait(false);
                    File.Move(tempPath, path);
                    ReplaceAvatarInDocument(user, path);
                }
            }
            catch (Exception e)
            {
                _logger.Warning(e, "Failed to download contributor avatar for {User}", user);
            }
            finally
            {
                lock (_downloadingAvatars)
                {
                    _ = _downloadingAvatars.Remove(user);
                }
            }
        }
    }

    /// <summary>
    /// 把弹窗已渲染文档中指定用户的占位头像图换成新下载的真头像（按 ToolTip 匹配用户）。
    /// 弹窗未打开时无文档可换，静默跳过——下次打开时 getter 会直接用磁盘上的真头像。
    /// </summary>
    /// <param name="user">GitHub 用户名。</param>
    /// <param name="avatarPath">真头像的本地路径。</param>
    private void ReplaceAvatarInDocument(string user, string avatarPath)
    {
        Execute.OnUIThread(() => {
            if (View is not Views.Dialogs.VersionUpdateDialogView view)
            {
                return;
            }

            if (view.ChangelogViewer.Document is not { } document)
            {
                return;
            }

            foreach (var image in EnumerateImages(document.Blocks))
            {
                if (image.Tag as string == "avatar" && image.ToolTip as string == user)
                {
                    image.Source = new BitmapImage(new Uri(avatarPath));
                }
            }
        });
    }

    private static IEnumerable<Paragraph> EnumerateParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;

                case Section section:
                    foreach (var nested in EnumerateParagraphs(section.Blocks))
                    {
                        yield return nested;
                    }

                    break;

                case List list:
                    foreach (var listItem in list.ListItems)
                    {
                        foreach (var nested in EnumerateParagraphs(listItem.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var nested in EnumerateParagraphs(cell.Blocks))
                                {
                                    yield return nested;
                                }
                            }
                        }
                    }

                    break;

                // details 块渲染为 Expander，内容在嵌套 FlowDocumentScrollViewer 的文档里
                case BlockUIContainer { Child: Expander { Content: FlowDocumentScrollViewer nested } }:
                    if (nested.Document is not null)
                    {
                        foreach (var paragraph in EnumerateParagraphs(nested.Document.Blocks))
                        {
                            yield return paragraph;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Image> EnumerateImages(BlockCollection blocks)
    {
        foreach (var paragraph in EnumerateParagraphs(blocks))
        {
            foreach (var image in EnumerateImages(paragraph.Inlines))
            {
                yield return image;
            }
        }
    }

    private static IEnumerable<Image> EnumerateImages(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer { Child: Image image }:
                    yield return image;
                    break;

                case Hyperlink hyperlink:
                    foreach (var nested in EnumerateImages(hyperlink.Inlines))
                    {
                        yield return nested;
                    }

                    break;

                case Span span:
                    foreach (var nested in EnumerateImages(span.Inlines))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    // UI 预览模式下 Core 未加载，不触碰 native，降级为占位值（预览模式不做更新比较）
    private readonly string _curVersion = FakeUpdateHelper.IsEnabled
        ? FakeUpdateHelper.CurrentVersion
        : Main.Bootstrapper.IsCoreInitSkipped
            ? "N/A"
            : Marshal.PtrToStringAnsi(MaaService.AsstGetVersion()) ?? "0.0.1";

    private string _latestVersion = string.Empty;

    /// <summary>
    /// Gets or sets the update tag.
    /// </summary>
    public string UpdateTag
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.Name = value;
        }
    } = FakeUpdateHelper.IsEnabled ? FakeUpdateHelper.TargetVersion : ConfigFactory.Root.Update.Name;

    private static string LoadUpdateBody()
    {
        var body = MarkdownDataHelper.Get("CHANGELOG");
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return string.Empty;
    }

    private string _updateInfo = LoadUpdateBody();

    // private static readonly MarkdownPipeline s_markdownPipeline = new MarkdownPipelineBuilder().UseXamlSupportedExtensions().Build();

    /// <summary>
    /// Gets or sets the update info.
    /// </summary>
    public string UpdateInfo
    {
        get {
            try
            {
                return FormatUpdateInfo(_updateInfo);
            }
            catch
            {
                return _updateInfo;
            }
        }

        set {
            SetAndNotify(ref _updateInfo, value);
            MarkdownDataHelper.Set("CHANGELOG", value);
            _ = DownloadMissingContributorAvatarsAsync(value);
        }
    }

    /// <summary>
    /// Gets or sets the update URL.
    /// </summary>
    public string UpdateUrl { get; set => SetAndNotify(ref field, value); } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it is the first boot after updating.
    /// </summary>
    public bool IsFirstBootAfterUpdate
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.IsFirstBoot = value;
        }
    } = ConfigFactory.Root.Update.IsFirstBoot;

    /// <summary>
    /// Gets or sets the name of the update package.
    /// </summary>
    public string UpdatePackageName
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.UpdatePackage = value;

            // 非空赋值即注册新更新包（FakeUpdate / MaaApi / MirrorChyan 下载链都经此 setter 直写配置，不经 RegisterPendingUpdatePackage），须同步删失败标志防死循环；
            // 空值（失败后的配置残留、资产名缺失）不删，保留标志供 AsstProxy.Init 启动期检测弹修复窗
            if (!string.IsNullOrWhiteSpace(value))
            {
                PendingUpdateApplier.ClearDelegatedUpdateFailureState();
            }
        }
    } = ConfigFactory.Root.Update.UpdatePackage;

    /// <summary>
    /// Gets the OS architecture.
    /// </summary>
    private static string OsArchitecture => RuntimeInformation.OSArchitecture.ToString().ToLower();

    /// <summary>
    /// Gets a value indicating whether the OS is arm.
    /// </summary>
    public static bool IsArm => OsArchitecture.StartsWith("arm");

    private const int UpdatePackageDownloadMaxAttempts = 3;

    private JObject? _latestJson;
    private JObject? _assetsObject;

    private bool _requiresFullPackageConfirmation;

    /// <summary>
    /// Gets a value indicating whether the installation only allows full package updates.
    /// 资源损坏或上次更新失败标志存在期间，OTA 增量只含差异文件，无法修复残留的不一致文件，
    /// 所有更新检查入口须强制改走完整包通道（修复流程，允许重装同版本）。
    /// </summary>
    internal static bool ShouldForceFullPackageUpdate => Bootstrapper.IsResourceBroken || PendingUpdateApplier.HasDelegatedUpdateFailure();

    public static bool HasPendingUpdatePackage()
    {
        return PendingUpdateApplier.HasPendingUpdatePackage();
    }

    public enum CheckUpdateRetT
    {
        /// <summary>
        /// 操作成功
        /// </summary>
        OK,

        /// <summary>
        /// 未知错误
        /// </summary>
        UnknownError,

        /// <summary>
        /// 无需更新
        /// </summary>
        NoNeedToUpdate,

        /// <summary>
        /// 调试版本无需更新
        /// </summary>
        NoNeedToUpdateDebugVersion,

        /// <summary>
        /// 已经是最新版
        /// </summary>
        AlreadyLatest,

        /// <summary>
        /// 网络错误
        /// </summary>
        NetworkError,

        /// <summary>
        /// 获取信息失败
        /// </summary>
        FailedToGetInfo,

        /// <summary>
        /// 更新包下载失败
        /// </summary>
        UpdatePackageDownloadFailed,

        /// <summary>
        /// 新版正在构建中
        /// </summary>
        NewVersionIsBeingBuilt,

        /// <summary>
        /// 只更新了游戏资源
        /// </summary>
        OnlyGameResourceUpdated,

        /// <summary>
        /// NoMirrorChyanCdk
        /// </summary>
        NoMirrorChyanCdk,

        /// <summary>
        /// 凭据错误已由共享状态模型显示并通知。
        /// </summary>
        CredentialError,
    }

    public enum AppUpdateSource
    {
        /// <summary>
        /// 本仓库（<see cref="MaaUrls.AppUpdateRepository"/>）的 GitHub Release，软件本体唯一的更新源。
        /// 不再使用上游 MaaApi / MaaRelease / MirrorChyan，避免本仓库的自定义修改被上游本体覆盖。
        /// </summary>
        GitHub,
    }

    /// <summary>
    /// Gets or sets a value indicating whether to show the update.
    /// </summary>
    public bool DoNotShowUpdate
    {
        get; set {
            SetAndNotify(ref field, value);
            ConfigFactory.Root.Update.DoNotShowUpdate = value;
        }
    } = ConfigFactory.Root.Update.DoNotShowUpdate;

    /// <summary>
    /// 如果是在更新后第一次启动，给出不打断操作的更新完成提示（不再自动弹出更新日志窗口），否则检查更新并下载更新包。
    /// </summary>
    /// <returns>Task</returns>
    public async Task ShowUpdateOrDownload()
    {
        if (IsFirstBootAfterUpdate)
        {
            IsFirstBootAfterUpdate = false;
            if (!DoNotShowUpdate)
            {
                // 更新日志可随时在 ｢设置 - 更新设置 - 更新日志｣ 手动查看（届时再下载贡献者头像）
                GrowlHelper.Info(LocalizationHelper.GetStringFormat("VersionUpdate.UpdatedTip", UpdateTag));
            }
        }
        else
        {
            if (!SettingsViewModel.VersionUpdateSettings.StartupUpdateCheck)
            {
                return;
            }

            if (!IsDebugVersion())
            {
                if (SettingsViewModel.VersionUpdateSettings.UpdateSource == UpdateSource.MirrorChyan && string.IsNullOrEmpty(SettingsViewModel.VersionUpdateSettings.MirrorChyanCdk))
                {
                    _ = Task.Run(() =>
                        MessageBoxHelper.Show(
                            LocalizationHelper.GetString("Settings.Update.MirrorChyan.SelectedButNoCdk"),
                            LocalizationHelper.GetString("Common.Warning"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            ok: LocalizationHelper.GetString("Common.Ok")));
                }

                await VersionUpdateAndAskToRestartAsync();
                await ResourceUpdater.ResourceUpdateAndReloadAsync();
            }
            else
            {
                // await ResourceUpdater.CheckAndDownloadResourceUpdate();
                // 跑个空任务避免 async warning
                await Task.Run(() => { });
            }
        }
    }

    /// <summary>
    /// 检查更新并下载更新包，如果成功则提示重启。
    /// </summary>
    /// <returns>Task</returns>
    public async Task VersionUpdateAndAskToRestartAsync()
    {
        if (SettingsViewModel.VersionUpdateSettings.IsCheckingForUpdates)
        {
            return;
        }

        var ret = await CheckAndDownloadVersionUpdate();
        if (ret == CheckUpdateRetT.OK)
        {
            _ = AskToRestart();
        }

        var toastMessage = ret switch {
            CheckUpdateRetT.NoNeedToUpdate => string.Empty,
            CheckUpdateRetT.NoNeedToUpdateDebugVersion => string.Empty,
            CheckUpdateRetT.AlreadyLatest => string.Empty,
            CheckUpdateRetT.UnknownError => LocalizationHelper.GetString("VersionUpdate.NewVersionDetectFailed.Title"),
            CheckUpdateRetT.NetworkError => LocalizationHelper.GetString("VersionUpdate.CheckNetworking"),
            CheckUpdateRetT.FailedToGetInfo => LocalizationHelper.GetString("VersionUpdate.GetReleaseNoteFailed"),
            CheckUpdateRetT.UpdatePackageDownloadFailed => LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadFailed.Title"),
            CheckUpdateRetT.OK => string.Empty,
            CheckUpdateRetT.NewVersionIsBeingBuilt => string.Empty,
            CheckUpdateRetT.OnlyGameResourceUpdated => string.Empty,
            CheckUpdateRetT.NoMirrorChyanCdk => LocalizationHelper.GetString("Settings.Update.MirrorChyan.SelectedButNoCdk"),
            _ => string.Empty,
        };

        if (toastMessage != string.Empty && !SettingsViewModel.VersionUpdateSettings.CurrentUpdateCredential.HasError)
        {
            ToastNotification.ShowDirect(toastMessage);
        }
    }

    /// <summary>
    /// 检查更新，并下载更新包。
    /// </summary>
    /// <returns>操作成功返回 <see langword="true"/>，反之则返回 <see langword="false"/>。</returns>
    public async Task<CheckUpdateRetT> CheckAndDownloadVersionUpdate()
    {
        try
        {
            SettingsViewModel.VersionUpdateSettings.IsCheckingForUpdates = true;

            if (FakeUpdateHelper.IsEnabled)
            {
                return await HandleFakeUpdate();
            }

            // 资源损坏/上次更新失败期间只允许完整包更新，OTA 只含差异文件无法修复残留的不一致文件。
            // 普通更新检查入口（设置页手动检查、启动/定时自动检查）统一重定向到完整包修复流程，
            // 不做版本新旧判断——已是最新版本时也重装同版本完整包以修复安装
            // 此分支有意不检查 ｢自动下载更新包｣ 偏好，修复优先于下载偏好，仅以流程内的完整包覆盖确认弹窗兜底
            if (ShouldForceFullPackageUpdate)
            {
                _logger.Information("Installation is broken (resource broken or delegated update failure), forcing full package update");

                // 重启提示交由调用方按 OK 返回值统一弹出，此处不再重复询问；
                // AlreadyRunning 映射为 NoNeedToUpdate 静默退出以避免双弹，重启询问由执行中的链路在成功收尾时弹出；
                // 失败一律映射为 UpdatePackageDownloadFailed，外层 toast 按下载失败提示，不区分实际失败原因
                var repairResult = await RunIntegrityRepairAsync(askRestartOnSuccess: false, updateCheckAlreadyOwned: true);
                return repairResult switch {
                    IntegrityRepairResult.Succeeded => CheckUpdateRetT.OK,
                    IntegrityRepairResult.AlreadyRunning => CheckUpdateRetT.NoNeedToUpdate,
                    IntegrityRepairResult.Canceled => CheckUpdateRetT.NoNeedToUpdate,
                    _ => CheckUpdateRetT.UpdatePackageDownloadFailed,
                };
            }

            var (checkRet, source) = await CheckUpdate();

            if (checkRet != CheckUpdateRetT.OK)
            {
                return checkRet;
            }

            return source switch {
                AppUpdateSource.GitHub => await HandleUpdateFromGitHubRelease(_githubUpdateRequest),
                _ => CheckUpdateRetT.UnknownError,
            };
        }
        catch (OperationCanceledException)
        {
            OutputDownloadProgress(string.Empty, downloading: false);
            return CheckUpdateRetT.NoNeedToUpdate;
        }
        finally
        {
            SettingsViewModel.VersionUpdateSettings.IsCheckingForUpdates = false;
        }
    }

    private async Task<CheckUpdateRetT> HandleFakeUpdate()
    {
        const double MinimumDetectedNewVersionDisplaySeconds = 0.5d;

        UpdateTag = FakeUpdateHelper.TargetVersion;

        UpdatePackageName = "MirrorChyanApp" + UpdateTag + ".zip";

        SettingsViewModel.VersionUpdateSettings.NewVersionFoundInfo = FakeUpdateHelper.HasPendingFakeUpdate
            ? $"{LocalizationHelper.GetString("VersionUpdate.NewVersionFound.Title")}: {UpdateTag}"
            : string.Empty;

        if (!FakeUpdateHelper.HasPendingFakeUpdate)
        {
            return CheckUpdateRetT.AlreadyLatest;
        }

        await Task.Delay(TimeSpan.FromSeconds(MinimumDetectedNewVersionDisplaySeconds));
        await SimulateMirrorChyanDownloadAsync();

        return CheckUpdateRetT.OK;
    }

    private static async Task SimulateMirrorChyanDownloadAsync()
    {
        const long MinPackageSizeMiB = 20;
        const long MaxPackageSizeMiB = 80;
        const long BytesPerMiB = 1024 * 1024;
        const double GigabitBytesPerSecond = 1_000_000_000d / 8d;
        const double LogUpdateIntervalSeconds = 1d;
        const double MinimumDownloadDisplaySeconds = 0.5d;
        const double MinSpeedFactor = 0.25d;
        const double MaxSpeedFactor = 1.20d;
        const double MaxSpeedFactorStepDelta = 0.22d;

        long totalBytes = Random.Shared.NextInt64(MinPackageSizeMiB * BytesPerMiB, (MaxPackageSizeMiB * BytesPerMiB) + 1);

        OutputDownloadProgress(LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadPreparing"), downloading: false, globalSource: false);

        long downloadedBytes = 0;
        double speedFactor = 1d + ((Random.Shared.NextDouble() - 0.5d) * 0.24d);
        bool hasReportedProgress = false;
        while (downloadedBytes < totalBytes)
        {
            speedFactor = Math.Clamp(
                speedFactor + ((Random.Shared.NextDouble() - 0.5d) * MaxSpeedFactorStepDelta * 2d),
                MinSpeedFactor,
                MaxSpeedFactor);

            double bytesPerSecond = GigabitBytesPerSecond * speedFactor;
            long remainingBytes = totalBytes - downloadedBytes;
            double remainingSeconds = remainingBytes / bytesPerSecond;
            double currentIntervalSeconds;

            if (!hasReportedProgress)
            {
                currentIntervalSeconds = Math.Max(MinimumDownloadDisplaySeconds, Math.Min(LogUpdateIntervalSeconds, remainingSeconds));
            }
            else
            {
                currentIntervalSeconds = Math.Min(LogUpdateIntervalSeconds, remainingSeconds);
            }

            long currentChunk = Math.Min(
                remainingBytes,
                Math.Max(1L, (long)Math.Round(bytesPerSecond * currentIntervalSeconds)));
            long currentValue = downloadedBytes + currentChunk;

            await Task.Delay(TimeSpan.FromSeconds(currentIntervalSeconds));

            OutputDownloadProgress(currentValue, totalBytes, (int)currentChunk, currentIntervalSeconds);

            downloadedBytes = currentValue;
            hasReportedProgress = true;
        }

        await Task.Delay(TimeSpan.FromSeconds(MinimumDownloadDisplaySeconds));
        OutputDownloadProgress(downloading: false, output: LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadCompleted.Title"));
    }

    private async Task<CheckUpdateRetT> HandleUpdateFromGitHubRelease(UpdateCredentialRequest? request = null)
    {
        // 保存新版本的信息
        var name = _latestJson?["name"]?.ToString();
        UpdateTag = string.IsNullOrEmpty(name) ? (_latestJson?["tag_name"]?.ToString() ?? string.Empty) : name;
        SettingsViewModel.VersionUpdateSettings.NewVersionFoundInfo = $"{LocalizationHelper.GetString("VersionUpdate.NewVersionFound.Title")}: {UpdateTag}";
        var body = _latestJson?["body"]?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(body))
        {
            var curHash = ComparableHash(_curVersion);
            var latestHash = ComparableHash(_latestVersion);

            if (curHash != null && latestHash != null)
            {
                body = $"**Full Changelog**: [{curHash} -> {latestHash}]({MaaUrls.AppUpdateRepository}/compare/{curHash}...{latestHash})";
            }
        }

        UpdateInfo = body;
        UpdateUrl = _latestJson?["html_url"]?.ToString() ?? string.Empty;

        bool otaFound = _assetsObject != null;
        bool goDownload = otaFound && SettingsViewModel.VersionUpdateSettings.AutoDownloadUpdatePackage;

        ShowUpdateInfo(otaFound, LocalizationHelper.GetString("VersionUpdate.NewVersionFound.GoWebpage"), true);

        string packageName = _assetsObject?["name"]?.ToString() ?? string.Empty;

        if (!goDownload || string.IsNullOrWhiteSpace(packageName))
        {
            OutputDownloadProgress(string.Empty, downloading: false);
            return CheckUpdateRetT.NoNeedToUpdate;
        }

        if (_assetsObject == null)
        {
            return CheckUpdateRetT.FailedToGetInfo;
        }

        string plannedPackagePath = GetPlannedUpdatePackagePath(packageName);
        if (_requiresFullPackageConfirmation && !ConfirmFullPackageUpdate(plannedPackagePath))
        {
            _logger.Information("Full package download canceled by user before download: {PackagePath}", plannedPackagePath);
            OutputDownloadProgress(string.Empty, downloading: false);
            return CheckUpdateRetT.NoNeedToUpdate;
        }

        string? url = await GetGitHubAssetDownloadUrlAsync(_assetsObject, request);
        if (string.IsNullOrEmpty(url))
        {
            url = GitHubUpdateHelper.GetBrowserDownloadUrl(_assetsObject);
            if (string.IsNullOrEmpty(url))
            {
                return CheckUpdateRetT.FailedToGetInfo;
            }
        }

        var downloaded = await DownloadGithubAssets(url, _assetsObject, request);
        if (downloaded.Success)
        {
            UpdatePackageName = packageName;
            OutputDownloadProgress(downloading: false, output: LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadCompleted.Title"));
        }
        else
        {
            if (request is not null && SettingsViewModel.VersionUpdateSettings.GitHubCredential.HasError)
            {
                OutputDownloadProgress(SettingsViewModel.VersionUpdateSettings.GitHubCredential.StatusText, downloading: false);
                return CheckUpdateRetT.CredentialError;
            }

            if (ShowDownloadRateLimit(downloaded))
            {
                return CheckUpdateRetT.NoNeedToUpdate;
            }

            OutputDownloadProgress(downloading: false, output: LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadFailed.Title"));
            {
                using var toast = new ToastNotification(LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadFailed.Title"));
                toast.AppendContentText(LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadFailed.Description"))
                    .AddButton(LocalizationHelper.GetString("VersionUpdate.NewVersionFound.GoWebpage"), ToastNotification.GetActionTagForOpenWeb(UpdateUrl))
                    .Show();
            }

            return CheckUpdateRetT.NoNeedToUpdate;
        }

        return CheckUpdateRetT.OK;

        string? ComparableHash(string version)
        {
            if (IsStdVersion(version) || IsBetaVersion(version))
            {
                return version;
            }

            if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var semVersion) ||
                !IsNightlyVersion(semVersion))
            {
                return null;
            }

            // v4.6.6-1.g{Hash}
            // v4.6.7-beta.2.8.g{Hash}
            var commitHash = semVersion.PrereleaseIdentifiers[^1].ToString();
            if (commitHash.StartsWith('g'))
            {
                commitHash = commitHash[1..];
            }

            return commitHash;
        }
    }

    private void ShowUpdateInfo(bool otaFound, string? text, bool globalSource)
    {
        bool goDownload = otaFound && SettingsViewModel.VersionUpdateSettings.AutoDownloadUpdatePackage;

        using var toast = new ToastNotification((otaFound ? LocalizationHelper.GetString("VersionUpdate.NewVersionFound.Title") : LocalizationHelper.GetString("VersionUpdate.NewVersionButNoPackage.Title")) + " : " + UpdateTag);
        if (goDownload)
        {
            OutputDownloadProgress(LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadPreparing"), false, globalSource);
            toast.AppendContentText(globalSource
                ? LocalizationHelper.GetString("VersionUpdate.NewVersionDownloading.GlobalSource")
                : LocalizationHelper.GetString("VersionUpdate.NewVersionDownloading.MirrorChyan"));
        }

        if (!otaFound)
        {
            toast.AppendContentText(LocalizationHelper.GetString("VersionUpdate.NewVersionButNoPackage.Description"));
        }

        int count = 0;
        foreach (var line in UpdateInfo.Split('\n'))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            toast.AppendContentText(line);
            if (++count >= 10)
            {
                break;
            }
        }

        if (!string.IsNullOrEmpty(text))
        {
            toast.AddButton(text, ToastNotification.GetActionTagForOpenWeb(globalSource ? UpdateUrl : MaaUrls.MirrorChyanManualUpdate));
        }

        toast.ShowUpdateVersion();
    }

    public async Task AskToRestart()
    {
        await AskToRestartCore(
            LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadCompleted.Description"),
            LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadCompleted.Title"));
    }

    public async Task AskToRestartForImportedPackage()
    {
        await AskToRestartCore(
            LocalizationHelper.GetString("VersionUpdate.LocalUpdatePackageImported.Description"),
            LocalizationHelper.GetString("VersionUpdate.LocalUpdatePackageImported.Title"));
    }

    /// <summary>
    /// 完整性修复流程的结果。
    /// </summary>
    public enum IntegrityRepairResult
    {
        /// <summary>
        /// 完整包已注册为待应用更新，等待重启。
        /// </summary>
        Succeeded,

        /// <summary>
        /// 另一条链路的修复已在进行，本次调用未启动新流程。
        /// </summary>
        AlreadyRunning,

        /// <summary>
        /// 用户在完整包风险确认弹窗中主动取消。
        /// </summary>
        Canceled,

        /// <summary>
        /// 解析下载源、下载或注册包失败。
        /// </summary>
        Failed,
    }

    private bool _isIntegrityRepairRunning;

    /// <summary>
    /// Gets a value indicating whether an integrity repair is in progress.
    /// 另一入口已在修复时，资源损坏弹窗据此跳过弹窗与退出，其余入口不再重复触发下载。
    /// </summary>
    public bool IsIntegrityRepairRunning => _isIntegrityRepairRunning;

    /// <summary>
    /// 资源完整性修复：重新下载当前渠道的完整包并注册为待应用更新。
    /// 完整包覆盖确认弹窗在流程内对所有入口一律弹出，部分入口（安装文件缺失、资源损坏弹窗）进入前已另弹确认，会先后确认两次。
    /// 不做版本新旧判断（允许重装同版本）。
    /// 修复进行中重复调用返回 <see cref="IntegrityRepairResult.AlreadyRunning"/>，由执行中的链路收尾，避免并发下载。
    /// </summary>
    /// <param name="askRestartOnSuccess">注册成功后是否执行重启收尾（自动安装更新包开启时等空闲后直接重启，否则弹窗询问）；调用方自行按返回值提示重启时传 <c>false</c>。</param>
    /// <returns>修复流程的结果，用于区分成功、已在执行、用户取消与失败。</returns>
    public Task<IntegrityRepairResult> RunIntegrityRepairAsync(bool askRestartOnSuccess = true)
    {
        return RunIntegrityRepairAsync(askRestartOnSuccess, updateCheckAlreadyOwned: false);
    }

    private async Task<IntegrityRepairResult> RunIntegrityRepairAsync(bool askRestartOnSuccess, bool updateCheckAlreadyOwned)
    {
        if (_isIntegrityRepairRunning)
        {
            _logger.Information("Integrity repair already running, deferring to the running flow");
            return IntegrityRepairResult.AlreadyRunning;
        }

        var settings = SettingsViewModel.VersionUpdateSettings;
        if (!updateCheckAlreadyOwned && settings.IsCheckingForUpdates)
        {
            _logger.Information("Integrity repair deferred while another update check is running");
            return IntegrityRepairResult.AlreadyRunning;
        }

        if (!updateCheckAlreadyOwned)
        {
            settings.IsCheckingForUpdates = true;
        }

        _isIntegrityRepairRunning = true;
        try
        {
            return await RunIntegrityRepairCoreAsync(askRestartOnSuccess);
        }
        catch (OperationCanceledException)
        {
            OutputDownloadProgress(string.Empty, downloading: false);
            return IntegrityRepairResult.Canceled;
        }
        finally
        {
            _isIntegrityRepairRunning = false;
            if (!updateCheckAlreadyOwned)
            {
                settings.IsCheckingForUpdates = false;
            }
        }
    }

    /// <summary>
    /// 执行完整性修复的主体流程，由 <see cref="RunIntegrityRepairAsync(bool)"/> 包装调用。
    /// </summary>
    /// <param name="askRestartOnSuccess">注册成功后是否执行重启收尾（自动安装更新包开启时等空闲后直接重启，否则弹窗询问）；调用方自行按返回值提示重启时传 <c>false</c>。</param>
    /// <returns>修复流程的结果，用于区分成功、用户取消与失败。</returns>
    private async Task<IntegrityRepairResult> RunIntegrityRepairCoreAsync(bool askRestartOnSuccess)
    {
        _logger.Information("Starting integrity repair");

        // 解释性提示放在流程内部而非重定向入口输出，命中防重入静默返回的调用不会进入流程内部，
        // 重定向入口的进度区因此保持执行中链路的输出；下载日志区为单条目替换，两条提示合并为一次输出避免互相覆盖
        OutputDownloadProgress(
            LocalizationHelper.GetString("VersionUpdate.IntegrityRepair.FullPackageNotice") + "\n" + LocalizationHelper.GetString("VersionUpdate.IntegrityRepair.Downloading"),
            downloading: false);

        // 后续需要弹窗询问重启，保持 UI 上下文，不使用 ConfigureAwait(false)

        // 先解析下载源并确认完整包覆盖风险，再执行下载；
        // 与其他完整包更新入口保持一致，用户拒绝时不必下载 200MB+ 的完整包
        // 软件本体完整包只从本仓库的 GitHub Release 获取，不再走上游 MirrorChyan / MaaApi；
        // 选择海外源并填写 Token 时使用 Token 认证请求，否则匿名请求 GitHub 公开 API。
        var settings = SettingsViewModel.VersionUpdateSettings;
        var source = settings.UpdateSource;
        var credential = settings.CurrentUpdateCredential;
        int revision = credential.Revision;
        var request = source == UpdateSource.GitHub ? await credential.BeginCheckAsync() : null;
        if (source != settings.UpdateSource || revision != credential.Revision)
        {
            return IntegrityRepairResult.Canceled;
        }

        var releasePackage = await ResolveReleaseRepairPackageAsync(request);
        if (source != settings.UpdateSource || revision != credential.Revision)
        {
            return IntegrityRepairResult.Canceled;
        }

        if (releasePackage is null)
        {
            FailIntegrityRepair("Integrity repair: no full package source resolved");
            return IntegrityRepairResult.Failed;
        }

        string plannedPackagePath = GetPlannedUpdatePackagePath(releasePackage.PackageName);
        if (!ConfirmFullPackageUpdate(plannedPackagePath))
        {
            _logger.Information("Integrity repair full package application canceled by user: {PackagePath}", plannedPackagePath);

            // 反馈走任务队列的下载日志，与更新包下载失败的提示通道一致
            OutputDownloadProgress(LocalizationHelper.GetString("VersionUpdate.IntegrityRepair.Canceled"), downloading: false);
            return IntegrityRepairResult.Canceled;
        }

        string? packagePath = null;
        _logger.Information("Integrity repair: downloading release full package {PackageName}", releasePackage.PackageName);
        OutputDownloadProgress(LocalizationHelper.GetString("VersionUpdate.IntegrityRepair.Downloading"), downloading: true, globalSource: true);
        if ((await DownloadGithubAssets(releasePackage.DownloadUrl, releasePackage.Asset, request)).Success)
        {
            packagePath = GetPlannedUpdatePackagePath(releasePackage.PackageName);
        }

        if (packagePath is null)
        {
            FailIntegrityRepair("Integrity repair download failed from all sources");
            return IntegrityRepairResult.Failed;
        }

        string arch = IsArm ? "arm64" : "x64";

        var importResult = PendingUpdateApplier.TryRegisterLocalPackage(
            packagePath,
            _curVersion,
            arch,
            inspection: null,
            allowSameVersion: true);

        // 修复流程只取完整包资产，此处再按注册结果防御，
        // 避免 OTA 包（只含差异文件）进入修复链路
        if (importResult.Status != PendingUpdateApplier.LocalPackageImportStatus.FullPackageRegistered)
        {
            _logger.Error("Integrity repair package rejected: status={Status}, packagePath={PackagePath}", importResult.Status, packagePath);
            FailIntegrityRepair("Integrity repair package rejected");
            return IntegrityRepairResult.Failed;
        }

        _logger.Information("Integrity repair package registered: {PackagePath}", packagePath);
        OutputDownloadProgress(downloading: false, output: LocalizationHelper.GetString("VersionUpdate.NewVersionDownloadCompleted.Title"));
        if (askRestartOnSuccess)
        {
            await AskToRestartForImportedPackage();
        }

        return IntegrityRepairResult.Succeeded;
    }

    /// <summary>
    /// 输出完整性修复失败的状态（gui.log 与任务队列下载日志，不弹 Toast）。
    /// </summary>
    /// <param name="reason">失败原因（仅记录日志）。</param>
    private static void FailIntegrityRepair(string reason)
    {
        _logger.Error(reason);
        OutputDownloadProgress(downloading: false, output: LocalizationHelper.GetString("VersionUpdate.IntegrityRepair.Failed"));
    }

    private sealed record ReleaseRepairPackage(string PackageName, JObject Asset, string DownloadUrl);

    /// <summary>
    /// 获取当前更新渠道的标识（stable / beta / alpha），用于从本仓库的 Release 中按渠道选择版本。
    /// </summary>
    /// <returns>渠道标识字符串。</returns>
    private static string GetUpdateChannel()
    {
        return SettingsViewModel.VersionUpdateSettings.VersionType switch {
            VersionUpdateSettingsUserControlModel.UpdateVersionType.Beta => "beta",
            VersionUpdateSettingsUserControlModel.UpdateVersionType.Nightly => "alpha",
            _ => "stable",
        };
    }

    /// <summary>
    /// 从本仓库（<see cref="MaaUrls.AppUpdateRepository"/>）的 GitHub Release 解析当前渠道完整包的下载信息（不执行下载）。
    /// </summary>
    /// <param name="request">本次 GitHub 凭据快照；为空时匿名请求 GitHub 公开 API。</param>
    /// <returns>解析结果；解析失败时返回 null。</returns>
    private static async Task<ReleaseRepairPackage?> ResolveReleaseRepairPackageAsync(UpdateCredentialRequest? request)
    {
        var cancellationToken = request?.CancellationToken ?? default;
        try
        {
            string versionType = GetUpdateChannel();

            var (release, _) = await GitHubUpdateHelper.GetLatestReleaseAsync(request, versionType).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (release is null && request is not null)
            {
                request = null;
                _logger.Warning("Authenticated GitHub repair metadata unavailable, falling back to anonymous request");
                (release, _) = await GitHubUpdateHelper.GetLatestReleaseAsync(null, versionType).ConfigureAwait(false);
            }

            string? latestVersion = release?["tag_name"]?.ToString();
            if (release?["assets"] is not JArray assets)
            {
                return null;
            }

            // 与 ReadReleaseDetails 一致，只认 MAA-<版本>-win-<架构>.zip 命名，
            // 避免误选同样含 win 的 DebugSymbol 等组件资产
            if (string.IsNullOrEmpty(latestVersion))
            {
                _logger.Error("No release version found for integrity repair");
                return null;
            }

            string packageName = $"MAA-{latestVersion}-win-{(IsArm ? "arm64" : "x64")}.zip";
            foreach (var asset in assets)
            {
                if (!string.Equals(asset["name"]?.ToString(), packageName, StringComparison.OrdinalIgnoreCase) ||
                    asset is not JObject fullPackage)
                {
                    continue;
                }

                string? rawUrl = await GetGitHubAssetDownloadUrlAsync(fullPackage, request).ConfigureAwait(false);
                rawUrl ??= GitHubUpdateHelper.GetBrowserDownloadUrl(fullPackage);
                if (string.IsNullOrEmpty(rawUrl))
                {
                    return null;
                }

                return new ReleaseRepairPackage(fullPackage["name"]!.ToString(), fullPackage, rawUrl);
            }

            _logger.Error("No full package asset found in release for integrity repair");
            return null;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.Error(ex, "Failed to resolve release full package for integrity repair");
            return null;
        }
    }

    public static bool ConfirmFullPackageUpdate(string packagePath)
    {
        string baseDir = Path.GetFullPath(PathsHelper.BaseDir);
        string normalizedPackagePath = Path.IsPathRooted(packagePath)
            ? Path.GetFullPath(packagePath)
            : GetPlannedUpdatePackagePath(packagePath);

        MessageBoxResult result = MessageBoxHelper.Show(
            LocalizationHelper.GetStringFormat("VersionUpdate.PendingFullUpdate.Description", baseDir, normalizedPackagePath),
            LocalizationHelper.GetString("VersionUpdate.PendingFullUpdate.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            yes: LocalizationHelper.GetString("VersionUpdate.PendingFullUpdate.Yes"),
            no: LocalizationHelper.GetString("VersionUpdate.PendingFullUpdate.No"));

        return result == MessageBoxResult.Yes;
    }

    private async Task AskToRestartCore(string description, string title)
    {
        // 自动安装，或用户点 ｢立即更新/确定｣ ：按启动设置决定是否写入 --skip-startup-auto-run。
        // 选 ｢稍后｣ 不会走到这里，之后手动启动是正常流程，不会带 skip 参数。
        string[] updateRestartArgs = Bootstrapper.GetUpdateRestartArgsIfEnabled();

        if (SettingsViewModel.VersionUpdateSettings.AutoInstallUpdatePackage)
        {
            if (FakeUpdateHelper.HasPendingFakeUpdate)
            {
                await _runningState.UntilIdleAsync();
                _ = FakeUpdateHelper.Updating();
                return;
            }

            await Bootstrapper.RestartAfterIdleAsync(updateRestartArgs);
            return;
        }

        await _runningState.UntilIdleAsync();

        var result = MessageBoxHelper.Show(
            description,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            ok: LocalizationHelper.GetString("Common.Confirm"),
            cancel: LocalizationHelper.GetString("Common.ManualRestart"));
        if (result == MessageBoxResult.OK)
        {
            if (FakeUpdateHelper.HasPendingFakeUpdate)
            {
                _ = FakeUpdateHelper.Updating();
                return;
            }

            if (updateRestartArgs.Length > 0)
            {
                Bootstrapper.ShutdownAndRestartWithArgs(updateRestartArgs);
            }
            else
            {
                Bootstrapper.ShutdownAndRestartWithoutArgs();
            }
        }
    }

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <returns>检查到更新返回 <see langword="true"/>，反之则返回 <see langword="false"/>。</returns>
    private async Task<(CheckUpdateRetT Ret, AppUpdateSource? Source)> CheckUpdate()
    {
        _githubUpdateRequest = null;

        // 调试版不检查更新
        if (IsDebugVersion())
        {
            return (CheckUpdateRetT.NoNeedToUpdateDebugVersion, null);
        }

        // 软件本体只从本仓库（MaaUrls.AppUpdateRepository）的 GitHub Release 检查更新，
        // 不再走上游 MaaApi / MaaRelease / MirrorChyan，避免本仓库的自定义修改被上游本体覆盖。
        // ｢更新源｣ 设置（海外源 / Mirror酱）此后只影响资源更新；选择海外源并填写 GitHub Token 时
        // 使用 Token 认证请求以获得更高的速率限制，否则匿名请求 GitHub 公开 API。
        var settings = SettingsViewModel.VersionUpdateSettings;
        var source = settings.UpdateSource;
        var credential = settings.CurrentUpdateCredential;
        int revision = credential.Revision;
        var request = source == UpdateSource.GitHub ? await credential.BeginCheckAsync() : null;
        if (source != settings.UpdateSource || revision != credential.Revision)
        {
            return (CheckUpdateRetT.NoNeedToUpdate, null);
        }

        _githubUpdateRequest = request;
        var (release, ret) = await GitHubUpdateHelper.GetLatestReleaseAsync(request, GetUpdateChannel());
        if (ret == CheckUpdateRetT.NoNeedToUpdate || source != settings.UpdateSource || revision != credential.Revision)
        {
            return (CheckUpdateRetT.NoNeedToUpdate, null);
        }

        if (release is null && request is not null && ret != CheckUpdateRetT.AlreadyLatest)
        {
            _logger.Warning("Authenticated GitHub update metadata unavailable, falling back to anonymous request");
            _githubUpdateRequest = null;
            (release, ret) = await GitHubUpdateHelper.GetLatestReleaseAsync(null, GetUpdateChannel());
            if (source != settings.UpdateSource || revision != credential.Revision)
            {
                return (CheckUpdateRetT.NoNeedToUpdate, null);
            }
        }

        if (release is null)
        {
            return (ret, null);
        }

        return (ReadReleaseDetails(release, release["tag_name"]?.ToString()), AppUpdateSource.GitHub);
    }

    private UpdateCredentialRequest? _githubUpdateRequest;

    private CheckUpdateRetT ReadReleaseDetails(JObject? release, string? latestVersion)
    {
        _requiresFullPackageConfirmation = false;
        _assetsObject = null;

        if (string.IsNullOrEmpty(latestVersion) || release?["assets"] is not JArray assets)
        {
            return CheckUpdateRetT.FailedToGetInfo;
        }

        if (!NeedToUpdate(latestVersion))
        {
            return CheckUpdateRetT.AlreadyLatest;
        }

        _latestVersion = latestVersion;
        _latestJson = release;

        JObject? fullPackage = null;

        var curVersionLower = _curVersion.ToLower();
        var latestVersionLower = _latestVersion.ToLower();
        foreach (var curAssets in assets)
        {
            string? name = curAssets["name"]?.ToString().ToLower();
            if (name == null)
            {
                continue;
            }

            if (name == $"maa-{latestVersionLower}-win-{(IsArm ? "arm64" : "x64")}.zip")
            {
                fullPackage = curAssets as JObject;
            }

            if (name == $"maacomponent-ota-{curVersionLower}_{latestVersionLower}-win-{(IsArm ? "arm64" : "x64")}.zip")
            {
                _assetsObject = curAssets as JObject;
                break;
            }
        }

        if (_assetsObject == null && fullPackage != null && SettingsViewModel.VersionUpdateSettings.AutoDownloadUpdatePackage)
        {
            _assetsObject = fullPackage;
            _requiresFullPackageConfirmation = true;
            _logger.Warning("No OTA package found, but full package found.");
            using var toast = new ToastNotification(LocalizationHelper.GetString("VersionUpdate.NewVersionNoOtaPackage"));
            toast.Show(30);
            Instances.TaskQueueViewModel.AddLog(LocalizationHelper.GetString("VersionUpdate.NewVersionNoOtaPackage"), UiLogColor.Warning);
        }

        return CheckUpdateRetT.OK;
    }

    private bool NeedToUpdate(string latestVersion)
    {
        if (IsDebugVersion())
        {
            return false;
        }

        bool curParsed = SemVersion.TryParse(_curVersion, SemVersionStyles.AllowLowerV, out var curVersionObj);
        bool latestPared = SemVersion.TryParse(latestVersion, SemVersionStyles.AllowLowerV, out var latestVersionObj);
        if (curParsed && latestPared && curVersionObj != null && latestVersionObj != null)
        {
            return curVersionObj.CompareSortOrderTo(latestVersionObj) < 0;
        }

        return string.CompareOrdinal(_curVersion, latestVersion) < 0;
    }

    private static string GetPlannedUpdatePackagePath(string packageName)
    {
        return Path.GetFullPath(Path.Combine(PathsHelper.BaseDir, packageName));
    }

    /// <summary>
    /// 获取 GitHub Assets 对象对应的文件
    /// </summary>
    /// <param name="url">下载链接</param>
    /// <param name="assetsObject">Github Assets 对象</param>
    /// <param name="request">本次 GitHub 凭据快照；为空时使用浏览器直链。</param>
    /// <returns>下载结果及服务器提供的重试信息。</returns>
    private static async Task<HttpDownloadResult> DownloadGithubAssets(string url, JObject assetsObject, UpdateCredentialRequest? request = null)
    {
        var cancellationToken = request?.CancellationToken ?? default;
        try
        {
            var uri = new Uri(url);
            var headers = request is { } check && GitHubUpdateHelper.IsGitHubApiUrl(uri)
                ? GitHubUpdateHelper.CreateHeaders(check.Value)
                : null;
            async Task<HttpDownloadResult> DownloadAsync()
            {
                var result = await Instances.HttpService.DownloadFileWithResultAsync(
                    uri,
                    assetsObject["name"]!.ToString(),
                    headers is not null ? "application/octet-stream" : assetsObject["content_type"]?.ToString(),
                    headers, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (headers is not null && request is { } check)
                {
                    if (result.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        await SettingsViewModel.VersionUpdateSettings.GitHubCredential
                            .ApplyAsync(check, new(UpdateCredentialStatus.Invalid)).ConfigureAwait(false);
                    }
                    else if (result.IsRateLimited)
                    {
                        await SettingsViewModel.VersionUpdateSettings.GitHubCredential.ApplyAsync(
                            check,
                            new(UpdateCredentialStatus.Failed, ErrorKey: GitHubUpdateHelper.RateLimitedErrorKey)).ConfigureAwait(false);
                    }
                }

                return result;
            }

            var downloaded = await DownloadUpdatePackageWithRetryAsync(DownloadAsync, uri, cancellationToken).ConfigureAwait(false);
            if (!downloaded.Success && headers is not null &&
                GitHubUpdateHelper.GetBrowserDownloadUrl(assetsObject) is { } fallbackUrl)
            {
                uri = new Uri(fallbackUrl);
                headers = null;
                downloaded = await DownloadUpdatePackageWithRetryAsync(DownloadAsync, uri, cancellationToken).ConfigureAwait(false);
            }

            return downloaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false);
        }
    }

    private static async Task<string?> GetGitHubAssetDownloadUrlAsync(JObject asset, UpdateCredentialRequest? request)
    {
        if (request is { } check)
        {
            return await GitHubUpdateHelper.ResolveAssetApiUrlAsync(asset, check).ConfigureAwait(false);
        }

        // 只接受指向本仓库 Release 的下载地址，防止下载到上游本体
        return GitHubUpdateHelper.GetBrowserDownloadUrl(asset);
    }

    private static async Task<HttpDownloadResult> DownloadUpdatePackageWithRetryAsync(Func<Task<HttpDownloadResult>> download, Uri uri, CancellationToken cancellationToken = default)
    {
        HttpDownloadResult result = new(false);
        for (var attempt = 1; attempt <= UpdatePackageDownloadMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Success)
            {
                return result;
            }

            var retryDelay = GetDownloadRetryDelay(result, attempt);
            if (retryDelay is null)
            {
                _logger.Warning("Update package download refused for {Uri}: {StatusCode}", uri.GetLeftPart(UriPartial.Path), result.StatusCode);
                return result;
            }

            // 较长的限流等待交给用户稍后重试，避免更新状态长时间阻塞；绝不提前重试。
            if (retryDelay.Value >= TimeSpan.FromMinutes(1))
            {
                _logger.Warning("Update package download deferred for {Uri}: retry after {Delay}", uri.GetLeftPart(UriPartial.Path), retryDelay.Value);
                return result with { RetryAfter = retryDelay };
            }

            if (attempt < UpdatePackageDownloadMaxAttempts)
            {
                var delay = retryDelay.Value;
                _logger.Warning(
                    "Update package download failed for {Uri} (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                    uri.GetLeftPart(UriPartial.Path),
                    attempt,
                    UpdatePackageDownloadMaxAttempts,
                    delay);
                OutputDownloadProgress(
                    LocalizationHelper.GetStringFormat(
                        "VersionUpdate.NewVersionDownloadRetrying",
                        delay.TotalSeconds,
                        attempt,
                        UpdatePackageDownloadMaxAttempts),
                    downloading: false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.Error(
            "Update package download finally failed after reaching maximum attempts for {Uri} (max attempts {MaxAttempts})",
            uri.GetLeftPart(UriPartial.Path),
            UpdatePackageDownloadMaxAttempts);
        return result;
    }

    private static bool ShowDownloadRateLimit(HttpDownloadResult result)
    {
        if (!result.IsRateLimited)
        {
            return false;
        }

        string message = LocalizationHelper.GetString(GitHubUpdateHelper.RateLimitedErrorKey);
        OutputDownloadProgress(message, downloading: false);
        ToastNotification.ShowDirect(message);
        return true;
    }

    private static TimeSpan? GetDownloadRetryDelay(HttpDownloadResult result, int attempt)
    {
        if (result.StatusCode is { } status && (int)status is >= 400 and < 500 &&
            status != HttpStatusCode.RequestTimeout && !result.IsRateLimited)
        {
            return null;
        }

        var delay = TimeSpan.FromSeconds(attempt);
        if (result.RetryAfter is { } serverDelay)
        {
            return serverDelay > delay ? serverDelay : delay;
        }

        return delay;
    }

    public static void OutputDownloadProgress(long value = 0, long maximum = 1, int len = 0, double ts = 1, string? toolTip = null)
    {
        string progress = $"[{value / 1048576.0:F}MiB/{maximum / 1048576.0:F}MiB ({value * 100.0 / maximum:F}%)";

        double speedInKiBPerSecond = len / ts / 1024.0;

        var speedDisplay = speedInKiBPerSecond >= 1024
            ? $"{speedInKiBPerSecond / 1024.0:F} MiB/s"
            : $"{speedInKiBPerSecond:F} KiB/s";

        OutputDownloadProgress(progress + $" {speedDisplay}", toolTip: toolTip);
    }

    private static bool _globalSource = true;

    public static void OutputDownloadProgress(string output, bool downloading = true, bool? globalSource = null, string? toolTip = null)
    {
        globalSource ??= _globalSource;
        _globalSource = globalSource.Value;

        string fullText;
        if (downloading)
        {
            string key = globalSource.Value ? "VersionUpdate.NewVersionDownloading.GlobalSource" : "VersionUpdate.NewVersionDownloading.MirrorChyan";
            fullText = LocalizationHelper.GetString(key) + "\n" + output;
        }
        else
        {
            fullText = output;
        }

        Instances.TaskQueueViewModel?.UpdateDownloadLog(fullText, toolTip);
    }

    public bool IsDebugVersion(string? version = null)
    {
        // return false;
        version ??= _curVersion;

        // match case 1: DEBUG_VERSION
        // match case 2: v{Major}.{Minor}.{Patch}-{CommitDistance}-g{CommitHash}
        // match case 3: {CommitHash}
        return Regex.IsMatch(version, @"^(.*DEBUG.*|v\d+(\.\d+){1,3}-\d+-g[0-9a-f]{6,}|[^v][0-9a-f]{6,})$");
    }

    public bool IsStdVersion(string? version = null)
    {
        // 正式版：vX.X.X
        // DevBuild (CI)：yyyy-MM-dd-HH-mm-ss-{CommitHash[..7]}
        // DevBuild (Local)：yyyy-MM-dd-HH-mm-ss-{CommitHash[..7]}-Local
        // Release (Local Commit)：v.{CommitHash[..7]}-Local
        // Release (Local Tag)：{Tag}-Local
        // Debug (Local)：DEBUG_VERSION
        // Script Compiled：c{CommitHash[..7]}
        version ??= _curVersion;

        if (IsDebugVersion(version))
        {
            return false;
        }

        if (version.StartsWith('c') || version.StartsWith("20") || version.Contains("Local"))
        {
            return false;
        }

        if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var semVersion))
        {
            return false;
        }

        return !semVersion.IsPrerelease;
    }

    public bool IsBetaVersion(string? version = null)
    {
        version ??= _curVersion;

        if (IsDebugVersion(version))
        {
            return false;
        }

        if (version.StartsWith('c') || version.StartsWith("20") || version.Contains("Local"))
        {
            return false;
        }

        if (!SemVersion.TryParse(version, SemVersionStyles.AllowLowerV, out var semVersion))
        {
            return false;
        }

        return semVersion.IsPrerelease && !IsNightlyVersion(semVersion);
    }

    public static bool IsNightlyVersion(SemVersion version)
    {
        if (!version.IsPrerelease)
        {
            return false;
        }

        // v{Major}.{Minor}.{Patch}-{Prerelease}.{CommitDistance}.g{CommitHash}
        // v4.6.7-beta.2.1.g1234567
        // v4.6.8-5.g1234567
        var lastId = version.PrereleaseIdentifiers.LastOrDefault().ToString();
        return lastId.StartsWith('g') && lastId.Length >= 7;
    }

    [GeneratedRegex(@"(?<=<details\s)open(?=[\s>])")]
    private static partial Regex MyRegex();
}

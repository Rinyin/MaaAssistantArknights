// <copyright file="GitHubUpdateHelper.cs" company="MaaAssistantArknights">
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaaWpfGui.Constants;
using MaaWpfGui.Services.Web;
using MaaWpfGui.ViewModels.UI;
using Newtonsoft.Json.Linq;
using Semver;
using Serilog;
using static MaaWpfGui.Models.UpdateCredentialModel;
using static MaaWpfGui.ViewModels.Dialogs.VersionUpdateDialogViewModel;

namespace MaaWpfGui.Helper;

public static class GitHubUpdateHelper
{
    private static readonly ILogger _logger = Log.ForContext(typeof(GitHubUpdateHelper));
    private static readonly ConcurrentDictionary<string, CachedManifest> _manifestCache = new();
    internal const string RateLimitedErrorKey = "VersionUpdate.RateLimited";

    // 匿名请求（未填写 GitHub Token）使用的缓存修订号，与凭据修订号区分
    private const int AnonymousCacheRevision = int.MinValue;

    private sealed record CachedManifest(string ETag, JToken Body, int CredentialRevision);

    public static Dictionary<string, string> CreateHeaders(string token) => new() {
        ["Accept"] = "application/vnd.github+json",
        ["Authorization"] = $"Bearer {token}",
        ["X-GitHub-Api-Version"] = "2026-03-10",
    };

    public static bool IsGitHubApiUrl(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static string? GetBrowserDownloadUrl(JObject asset)
    {
        if (asset["name"]?.Type != JTokenType.String ||
            !Uri.TryCreate(asset["browser_download_url"]?.ToString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 6 && IsUpdateRepository(segments[0], segments[1]) &&
            segments[2] == "releases" && segments[3] == "download" &&
            !string.IsNullOrEmpty(Uri.UnescapeDataString(segments[4])) &&
            Uri.UnescapeDataString(segments[5]) == asset.Value<string>("name")
            ? uri.AbsoluteUri
            : null;
    }

    private static UpdateCredentialResult ParseCredentialResult(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new(UpdateCredentialStatus.Invalid);
        }

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotModified))
        {
            return new(UpdateCredentialStatus.Failed);
        }

        DateTimeOffset? expiresAt = null;
        if (response.Headers.TryGetValues("GitHub-Authentication-Token-Expiration", out var expirationValues))
        {
            foreach (string expirationValue in expirationValues)
            {
                string value = expirationValue.Trim();
                if (value.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
                {
                    value = value[..^4] + " +00:00";
                }

                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiration))
                {
                    expiresAt = expiration;
                    break;
                }
            }
        }

        return new(UpdateCredentialStatus.Valid, expiresAt);
    }

    public static async Task<(JObject? Data, CheckUpdateRetT Ret)> RequestAsync(string url, UpdateCredentialRequest request, bool cacheManifest = false, bool rawContent = false)
    {
        var (data, ret) = await RequestJsonAsync(url, request, cacheManifest, rawContent).ConfigureAwait(false);
        if (ret != CheckUpdateRetT.OK)
        {
            return (null, ret);
        }

        return data is JObject obj ? (obj, CheckUpdateRetT.OK) : (null, CheckUpdateRetT.FailedToGetInfo);
    }

    private static async Task<(JToken? Data, CheckUpdateRetT Ret)> RequestJsonAsync(string url, UpdateCredentialRequest request, bool cacheManifest = false, bool rawContent = false)
    {
        var credential = SettingsViewModel.VersionUpdateSettings.GitHubCredential;
        try
        {
            var data = await GetJsonAsync(url, request, cacheManifest, rawContent).ConfigureAwait(false);
            if (!credential.IsCurrent(request))
            {
                return (null, CheckUpdateRetT.NoNeedToUpdate);
            }

            if (credential.HasError)
            {
                return (null, CheckUpdateRetT.CredentialError);
            }

            return (data, data is null ? CheckUpdateRetT.FailedToGetInfo : CheckUpdateRetT.OK);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            return (null, CheckUpdateRetT.NoNeedToUpdate);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to request GitHub update: {ExceptionType}", ex.GetType().Name);
            if (credential.Status != UpdateCredentialStatus.Valid)
            {
                await credential.ApplyAsync(request, new(UpdateCredentialStatus.Failed)).ConfigureAwait(false);
            }

            return (null, CheckUpdateRetT.NetworkError);
        }
    }

    /// <summary>
    /// 从本仓库（<see cref="MaaUrls.AppUpdateRepository"/>）的 GitHub Release 列表中获取当前渠道的最新版本。
    /// 软件本体不再读取上游 MaaRelease 发布清单，避免本仓库的自定义修改被上游本体覆盖。
    /// </summary>
    /// <param name="request">本次 GitHub 凭据快照；为空时匿名请求公开 API（速率限制较低）。</param>
    /// <param name="channel">更新渠道：stable / beta / alpha。</param>
    /// <returns>GitHub Release 对象；没有符合渠道的 Release 时返回 <see cref="CheckUpdateRetT.AlreadyLatest"/>。</returns>
    public static async Task<(JObject? Data, CheckUpdateRetT Ret)> GetLatestReleaseAsync(UpdateCredentialRequest? request, string channel)
    {
        if (channel is not ("stable" or "beta" or "alpha"))
        {
            return (null, CheckUpdateRetT.FailedToGetInfo);
        }

        string url = $"{MaaUrls.AppUpdateReleasesApi}?per_page=30";
        var (releases, ret) = request is { } check
            ? await RequestJsonAsync(url, check, cacheManifest: true).ConfigureAwait(false)
            : await RequestAnonymousJsonAsync(url).ConfigureAwait(false);
        if (ret != CheckUpdateRetT.OK)
        {
            return (null, ret);
        }

        if (releases is not JArray list)
        {
            return (null, CheckUpdateRetT.FailedToGetInfo);
        }

        if (SelectRelease(list, channel)?.DeepClone() is not JObject release)
        {
            // 本仓库尚未发布符合该渠道的 Release，视为没有可用的新版本
            _logger.Information("No {Channel} release found in {Repository}", channel, MaaUrls.AppUpdateRepository);
            return (null, CheckUpdateRetT.AlreadyLatest);
        }

        return (release, CheckUpdateRetT.OK);
    }

    /// <summary>
    /// 按更新渠道选出目标 Release：stable 只取正式版；beta 取正式版与公测版（排除 nightly）；alpha 取全部。
    /// 草稿、缺少资产列表或 tag 不是 SemVer 的 Release 一律跳过，同渠道内取版本号最大者。
    /// </summary>
    private static JObject? SelectRelease(JArray releases, string channel)
    {
        JObject? best = null;
        SemVersion? bestVersion = null;
        foreach (var release in releases.OfType<JObject>())
        {
            if (release.Value<bool>("draft") || release["assets"] is not JArray ||
                release["tag_name"]?.Type != JTokenType.String ||
                !SemVersion.TryParse(release.Value<string>("tag_name"), SemVersionStyles.AllowLowerV, out var version))
            {
                continue;
            }

            bool accepted = channel switch {
                "stable" => !version.IsPrerelease && !release.Value<bool>("prerelease"),
                "beta" => !IsNightlyVersion(version),
                _ => true,
            };

            if (accepted && (bestVersion is null || version.CompareSortOrderTo(bestVersion) > 0))
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// 匿名请求 GitHub 公开 API（未填写 GitHub Token 时使用）。使用 ETag 条件请求，304 响应不计入速率限制。
    /// </summary>
    private static async Task<(JToken? Data, CheckUpdateRetT Ret)> RequestAnonymousJsonAsync(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (!IsGitHubApiUrl(uri))
            {
                return (null, CheckUpdateRetT.FailedToGetInfo);
            }

            var headers = new Dictionary<string, string> {
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2026-03-10",
            };

            if (_manifestCache.TryGetValue(url, out var cached) && cached.CredentialRevision == AnonymousCacheRevision)
            {
                headers["If-None-Match"] = cached.ETag;
            }
            else
            {
                cached = null;
            }

            using var response = await Instances.HttpService.GetAsync(uri, headers, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                return (cached.Body.DeepClone(), CheckUpdateRetT.OK);
            }

            if (!response.IsSuccessStatusCode)
            {
                var (_, isRateLimited) = await HttpService.GetRetryInfoAsync(response).ConfigureAwait(false);
                _logger.Warning("Anonymous GitHub request failed: {StatusCode}, rate limited: {IsRateLimited}", (int)response.StatusCode, isRateLimited);
                return (null, response.StatusCode == HttpStatusCode.NotFound ? CheckUpdateRetT.FailedToGetInfo : CheckUpdateRetT.NetworkError);
            }

            var json = JToken.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (response.Headers.ETag is { } etag)
            {
                _manifestCache[url] = new(etag.ToString(), json.DeepClone(), AnonymousCacheRevision);
            }
            else
            {
                _manifestCache.TryRemove(url, out _);
            }

            return (json, CheckUpdateRetT.OK);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to request GitHub anonymously: {ExceptionType}", ex.GetType().Name);
            return (null, CheckUpdateRetT.NetworkError);
        }
    }

    public static async Task<string?> ResolveAssetApiUrlAsync(JObject asset, UpdateCredentialRequest request)
    {
        var cancellationToken = request.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (asset["name"]?.Type != JTokenType.String || string.IsNullOrEmpty(asset.Value<string>("name")))
        {
            return null;
        }

        string assetName = asset.Value<string>("name")!;
        if (Uri.TryCreate(asset["url"]?.ToString(), UriKind.Absolute, out var assetUri) && IsReleaseAssetApiUrl(assetUri))
        {
            return assetUri.AbsoluteUri;
        }

        var browserUrl = GetBrowserDownloadUrl(asset);
        if (browserUrl is null)
        {
            return null;
        }

        var browserUri = new Uri(browserUrl);
        var segments = browserUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6 || !IsUpdateRepository(segments[0], segments[1]) ||
            segments[2] != "releases" || segments[3] != "download")
        {
            return null;
        }

        string tag = Uri.UnescapeDataString(segments[4]);
        if (string.IsNullOrEmpty(tag) || Uri.UnescapeDataString(segments[5]) != assetName)
        {
            return null;
        }

        try
        {
            var release = (await GetJsonAsync(
                $"{MaaUrls.GitHubApi}/repos/{segments[0]}/{segments[1]}/releases/tags/{Uri.EscapeDataString(tag)}",
                request).ConfigureAwait(false)) as JObject;
            if (release?["tag_name"]?.Value<string>() != tag || release.Value<bool>("draft") || release["assets"] is not JArray assets)
            {
                return null;
            }

            foreach (var candidate in assets.OfType<JObject>())
            {
                if (candidate["name"]?.Value<string>() == assetName &&
                    Uri.TryCreate(candidate["url"]?.ToString(), UriKind.Absolute, out assetUri) &&
                    IsReleaseAssetApiUrl(assetUri, segments[1]))
                {
                    return assetUri.AbsoluteUri;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to resolve GitHub release asset: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    // 软件本体更新包只允许来自本仓库的 Release，不再接受上游 MaaAssistantArknights / MaaRelease 的资产
    private static bool IsUpdateRepository(string owner, string repository) =>
        owner.Equals(MaaUrls.AppUpdateRepositoryOwner, StringComparison.OrdinalIgnoreCase) &&
        repository.Equals(MaaUrls.AppUpdateRepositoryName, StringComparison.OrdinalIgnoreCase);

    private static bool IsReleaseAssetApiUrl(Uri uri, string? repository = null)
    {
        if (!IsGitHubApiUrl(uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 6 && segments[0] == "repos" && IsUpdateRepository(segments[1], segments[2]) &&
            (repository is null || repository.Equals(segments[2], StringComparison.OrdinalIgnoreCase)) &&
            segments[3] == "releases" && segments[4] == "assets" && long.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0;
    }

    private static async Task<JToken?> GetJsonAsync(string url, UpdateCredentialRequest request, bool cacheManifest = false, bool rawContent = false)
    {
        var uri = new Uri(url);
        if (!IsGitHubApiUrl(uri))
        {
            return null;
        }

        var credential = SettingsViewModel.VersionUpdateSettings.GitHubCredential;
        if (!credential.IsCurrent(request))
        {
            return null;
        }

        // Reject malformed header values before sending them or logging an HTTP exception.
        if (string.IsNullOrWhiteSpace(request.Value) || request.Value.Any(c => c <= ' ' || c >= '\u007f'))
        {
            await credential.ApplyAsync(request, new(UpdateCredentialStatus.Invalid)).ConfigureAwait(false);
            return null;
        }

        var headers = CreateHeaders(request.Value);
        if (rawContent)
        {
            // Contents API 的 raw 媒体类型不适用于 Releases API。
            headers["Accept"] = "application/vnd.github.raw+json";
        }

        CachedManifest? cached = null;

        // 新输入的 Token 需要完整响应来读取有效期，不能只复用旧 Token 的 304 正文。
        if (cacheManifest && _manifestCache.TryGetValue(url, out cached) && cached.CredentialRevision == request.Revision)
        {
            headers["If-None-Match"] = cached.ETag;
        }

        using var response = await Instances.HttpService.GetAsync(
            uri, headers, HttpCompletionOption.ResponseContentRead, token: request.CancellationToken).ConfigureAwait(false);
        request.CancellationToken.ThrowIfCancellationRequested();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // A missing API resource does not mean the token is invalid.
            await credential.ApplyAsync(request, new(UpdateCredentialStatus.Valid, credential.ExpiresAt)).ConfigureAwait(false);
            return null;
        }

        var result = ParseCredentialResult(response);
        var (_, isRateLimited) = await HttpService.GetRetryInfoAsync(response, request.CancellationToken).ConfigureAwait(false);
        if (isRateLimited)
        {
            result = new(UpdateCredentialStatus.Failed, ErrorKey: RateLimitedErrorKey);
        }

        if (result.Status != UpdateCredentialStatus.Valid)
        {
            await credential.ApplyAsync(request, result).ConfigureAwait(false);
            return null;
        }

        var json = response.StatusCode == HttpStatusCode.NotModified
            ? cached?.Body.DeepClone()
            : JToken.Parse(await response.Content.ReadAsStringAsync(request.CancellationToken).ConfigureAwait(false));
        if (response.StatusCode == HttpStatusCode.NotModified &&
            !response.Headers.Contains("GitHub-Authentication-Token-Expiration"))
        {
            // A 304 response may omit authentication metadata; retain the known expiration.
            result = result with { ExpiresAt = credential.ExpiresAt };
        }

        if (!await credential.ApplyAsync(request, json is null ? new(UpdateCredentialStatus.Failed) : result).ConfigureAwait(false) ||
            credential.HasError)
        {
            return null;
        }

        if (cacheManifest && response.StatusCode != HttpStatusCode.NotModified && json is { } manifest)
        {
            if (response.Headers.ETag is { } etag)
            {
                _manifestCache[url] = new(etag.ToString(), manifest.DeepClone(), request.Revision);
            }
            else
            {
                _manifestCache.TryRemove(url, out _);
            }
        }

        return json;
    }
}

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace IMYSHook;

/// <summary>启动时只检查小型版本标记；时间戳变化后才下载词典，失败时使用有效缓存。</summary>
internal static class UiI18nRemote
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxVersionBytes = 4096;

    internal static string RefreshAndGetCache(string directory, string language)
    {
        if (!UiI18nConfig.RemoteEnabled) return null;
        if (!IsSafeLanguage(language))
        {
            Plugin.Global.Log.LogWarning("[i18n] invalid remote language code");
            return null;
        }

        var cache = Path.Combine(directory, "cache", language + ".json");
        var cachedVersion = Path.Combine(directory, "cache", language + ".version.json");
        try
        {
            var baseUrl = UiI18nConfig.RemoteBaseUrl.TrimEnd('/') + "/";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("remoteBaseUrl must be HTTPS");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IMYSHook-UI-i18n/1.0");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            // 查询串绕开 CDN 缓存；请求体只有一小段版本信息，不再每次下载整个词典。
            var versionUri = new Uri(baseUri, Uri.EscapeDataString(language) + ".version.json?ts=" +
                                              DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var versionBytes = Download(client, versionUri, MaxVersionBytes, timeout.Token);
            var version = ParseVersion(versionBytes);

            if (File.Exists(cache) && TryReadCachedVersion(cachedVersion, out var localVersion) &&
                localVersion == version &&
                IsValidCachedDictionary(cache, version.Hash))
            {
                Plugin.Global.Log.LogInfo($"[i18n] remote dictionary unchanged: {language}, updatedAt={version.UpdatedAt}");
                return cache;
            }

            var uri = new Uri(baseUri, Uri.EscapeDataString(language) + ".json?sha256=" + version.Hash);
            var bytes = Download(client, uri, MaxBytes, timeout.Token);
            if (!IsValidDictionary(bytes))
                throw new InvalidDataException("remote dictionary has an invalid JSON schema");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("remote dictionary hash does not match version marker");

            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var staging = cache + ".download";
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, cache, true);
            var versionStaging = cachedVersion + ".download";
            File.WriteAllBytes(versionStaging, versionBytes);
            File.Move(versionStaging, cachedVersion, true);
            Plugin.Global.Log.LogInfo($"[i18n] remote dictionary updated: {language}, bytes={bytes.Length}, sha256={hash[..12]}");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] remote dictionary unavailable: {e.GetType().Name}: {e.Message}");
        }

        if (!File.Exists(cache)) return null;
        try
        {
            if (IsValidDictionary(File.ReadAllBytes(cache))) return cache;
            Plugin.Global.Log.LogWarning("[i18n] cached remote dictionary is invalid; using bundled dictionary");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] cached remote dictionary unreadable: {e.Message}");
        }
        return null;
    }

    private static byte[] Download(HttpClient client, Uri uri, int maxBytes, CancellationToken token)
    {
        using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("remote file is too large");

        using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = source.ReadAsync(chunk, 0, chunk.Length, token).GetAwaiter().GetResult()) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("remote file is too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static (string UpdatedAt, string Hash) ParseVersion(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxVersionBytes)
            throw new InvalidDataException("invalid remote version marker size");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var updatedAt = root.GetProperty("updatedAt").GetString();
        var hash = root.GetProperty("sha256").GetString();
        if (string.IsNullOrWhiteSpace(updatedAt) || !DateTimeOffset.TryParse(updatedAt, out _) ||
            hash == null || !IsHexHash(hash))
            throw new InvalidDataException("invalid remote version marker");
        return (updatedAt, hash.ToUpperInvariant());
    }

    private static bool IsHexHash(string hash)
    {
        if (hash.Length != 64) return false;
        foreach (var c in hash)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static bool TryReadCachedVersion(string path, out (string UpdatedAt, string Hash) version)
    {
        version = default;
        try
        {
            if (!File.Exists(path)) return false;
            version = ParseVersion(File.ReadAllBytes(path));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidCachedDictionary(string path, string expectedHash)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return IsValidDictionary(bytes) &&
                   string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSafeLanguage(string language)
    {
        if (string.IsNullOrEmpty(language) || language.Length > 32) return false;
        foreach (var c in language)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
        return true;
    }

    private static bool IsValidDictionary(byte[] bytes)
    {
        if (bytes.Length < 100 || bytes.Length > MaxBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

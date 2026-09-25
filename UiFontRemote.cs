using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using BepInEx;

namespace IMYSHook;

internal static class UiFontRemote
{
    private const string FontName = "NotoSerifCJKtc-SemiBold.otf";
    private const string LicenseName = "OFL-NotoCJK.txt";
    private const int MaxFontBytes = 32 * 1024 * 1024;
    private const int MaxLicenseBytes = 64 * 1024;
    private const int MaxManifestBytes = 4096;

    internal static string CachedFontPath { get; private set; }

    internal static void Init()
    {
        CachedFontPath = null;
        if (!UiI18nConfig.TextOn || UiI18nConfig.FontMode == "off") return;

        var cacheDir = Path.Combine(Paths.PluginPath, "font", "cache");
        var manifestPath = Path.Combine(cacheDir, "manifest.json");
        var old = ReadManifest(manifestPath);
        CachedFontPath = ValidCachedPair(cacheDir, old);
        if (!UiI18nConfig.RemoteEnabled) return;

        try
        {
            var root = UiI18nConfig.FontBaseUrl.TrimEnd('/') + "/";
            if (!Uri.TryCreate(root, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("fontBaseUrl must be HTTPS");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IMYSHook-UI-font/1.0");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var manifestBytes = Download(client,
                new Uri(baseUri, "manifest.json?ts=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                MaxManifestBytes);
            var manifest = ParseManifest(manifestBytes);

            var fontPath = EnsureFile(client, baseUri, cacheDir, FontName, manifest.FontHash, MaxFontBytes);
            EnsureFile(client, baseUri, cacheDir, LicenseName, manifest.LicenseHash, MaxLicenseBytes);

            Directory.CreateDirectory(cacheDir);
            var staging = manifestPath + ".download";
            File.WriteAllBytes(staging, manifestBytes);
            File.Move(staging, manifestPath, true);
            CachedFontPath = fontPath;
            Plugin.Global.Log.LogInfo($"[font] remote ready: {FontName}, sha256={manifest.FontHash[..12]}");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[font] remote unavailable: {e.GetType().Name}: {e.Message}");
            if (CachedFontPath != null)
                Plugin.Global.Log.LogInfo("[font] using verified cache");
        }
    }

    private static (string FontHash, string LicenseHash)? ReadManifest(string path)
    {
        try { return File.Exists(path) ? ParseManifest(File.ReadAllBytes(path)) : null; }
        catch (Exception) { return null; }
    }

    private static (string FontHash, string LicenseHash) ParseManifest(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("invalid font manifest size");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("updatedAt", out var stamp) ||
            stamp.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(stamp.GetString(), out _) ||
            !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object ||
            !files.TryGetProperty(FontName, out var font) || font.ValueKind != JsonValueKind.String ||
            !files.TryGetProperty(LicenseName, out var license) || license.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("invalid font manifest schema");
        var fontHash = font.GetString();
        var licenseHash = license.GetString();
        if (!SafeHash(fontHash) || !SafeHash(licenseHash))
            throw new InvalidDataException("invalid font manifest hash");
        return (fontHash.ToUpperInvariant(), licenseHash.ToUpperInvariant());
    }

    private static string ValidCachedPair(string dir, (string FontHash, string LicenseHash)? manifest)
    {
        if (manifest == null) return null;
        var font = CachePath(dir, FontName, manifest.Value.FontHash);
        var license = CachePath(dir, LicenseName, manifest.Value.LicenseHash);
        return ValidFile(font, manifest.Value.FontHash, MaxFontBytes) &&
               ValidFile(license, manifest.Value.LicenseHash, MaxLicenseBytes) ? font : null;
    }

    private static string EnsureFile(HttpClient client, Uri baseUri, string dir, string name,
        string hash, int maxBytes)
    {
        var path = CachePath(dir, name, hash);
        if (ValidFile(path, hash, maxBytes)) return path;
        var bytes = Download(client, new Uri(baseUri, name + "?sha256=" + hash), maxBytes);
        if (!HashMatches(bytes, hash) || bytes.Length < 100)
            throw new InvalidDataException($"font file hash or size mismatch: {name}");
        Directory.CreateDirectory(dir);
        var staging = path + ".download";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, path, true);
        return path;
    }

    private static string CachePath(string dir, string name, string hash) =>
        Path.Combine(dir, hash + Path.GetExtension(name));

    private static bool ValidFile(string path, string hash, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 100 || info.Length > maxBytes) return false;
            return HashMatches(File.ReadAllBytes(path), hash);
        }
        catch (Exception) { return false; }
    }

    private static bool HashMatches(byte[] bytes, string hash) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash,
            StringComparison.OrdinalIgnoreCase);

    private static bool SafeHash(string hash)
    {
        if (hash == null || hash.Length != 64) return false;
        foreach (var c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static byte[] Download(HttpClient client, Uri uri, int maxBytes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("remote font file is too large");
        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var memory = new MemoryStream();
        var chunk = new byte[32768];
        int read;
        while ((read = stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token)
                   .GetAwaiter().GetResult()) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new InvalidDataException("remote font file is too large");
            memory.Write(chunk, 0, read);
        }
        return memory.ToArray();
    }
}

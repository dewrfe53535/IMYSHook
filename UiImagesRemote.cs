using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace IMYSHook;

internal static class UiImagesRemote
{
    private const int MaxManifestBytes = 64 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    internal static IReadOnlyList<string> RefreshAndGetPaths(string outDir, string language)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!UiI18nConfig.RemoteEnabled || !UiI18nConfig.ImageOn || !SafeName(language))
            return new List<string>();

        var cacheDir = Path.Combine(outDir, "cache", language);
        var manifestPath = Path.Combine(cacheDir, "manifest.json");
        var oldManifest = ReadManifest(manifestPath);
        AddValidFiles(oldManifest, cacheDir, paths);

        try
        {
            var root = UiI18nConfig.ImageBaseUrl.TrimEnd('/') + "/";
            if (!Uri.TryCreate(root, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("imageBaseUrl must be HTTPS");
            var langUri = new Uri(baseUri, Uri.EscapeDataString(language) + "/");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IMYSHook-UI-images/1.0");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            var manifestUri = new Uri(langUri, "manifest.json?ts=" +
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var manifestBytes = Download(client, manifestUri, MaxManifestBytes);
            var manifest = ParseManifest(manifestBytes);
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var downloaded = 0;

            foreach (var entry in manifest)
            {
                var path = Path.Combine(cacheDir, entry.Key);
                if (!ValidFile(path, entry.Value))
                {
                    try
                    {
                        var bytes = Download(client, new Uri(langUri,
                                Uri.EscapeDataString(entry.Key) + "?sha256=" + entry.Value),
                            MaxImageBytes);
                        if (!ValidPng(bytes) || !HashMatches(bytes, entry.Value))
                            throw new InvalidDataException("PNG signature or SHA-256 mismatch");
                        Directory.CreateDirectory(cacheDir);
                        var staging = path + ".download";
                        File.WriteAllBytes(staging, bytes);
                        File.Move(staging, path, true);
                        downloaded++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Global.Log.LogWarning($"[img] download failed {entry.Key}: {e.Message}");
                        if (paths.TryGetValue(entry.Key, out var oldPath)) selected[entry.Key] = oldPath;
                        continue;
                    }
                }
                selected[entry.Key] = path;
            }

            Directory.CreateDirectory(cacheDir);
            var manifestStaging = manifestPath + ".download";
            File.WriteAllBytes(manifestStaging, manifestBytes);
            File.Move(manifestStaging, manifestPath, true);
            paths = selected;
            Plugin.Global.Log.LogInfo($"[img] remote manifest={manifest.Count} downloaded={downloaded} cached={paths.Count - downloaded}");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[img] remote manifest unavailable: {e.GetType().Name}: {e.Message}");
        }

        return new List<string>(paths.Values);
    }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        try { return File.Exists(path) ? ParseManifest(File.ReadAllBytes(path)) : null; }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, string> ParseManifest(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("invalid image manifest size");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("updatedAt", out var stamp) ||
            stamp.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(stamp.GetString(), out _) ||
            !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("invalid image manifest schema");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateObject())
        {
            if (result.Count >= 300 || !item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                !SafeName(Path.GetFileNameWithoutExtension(item.Name)) ||
                item.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("invalid image manifest entry");
            var hash = item.Value.GetString();
            if (!SafeHash(hash) || !result.TryAdd(item.Name, hash))
                throw new InvalidDataException("invalid image manifest hash or duplicate");
        }
        return result;
    }

    private static void AddValidFiles(Dictionary<string, string> manifest, string dir,
        Dictionary<string, string> paths)
    {
        if (manifest == null) return;
        foreach (var entry in manifest)
        {
            var path = Path.Combine(dir, entry.Key);
            if (ValidFile(path, entry.Value)) paths[entry.Key] = path;
        }
    }

    private static bool ValidFile(string path, string hash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxImageBytes) return false;
            var bytes = File.ReadAllBytes(path);
            return ValidPng(bytes) && HashMatches(bytes, hash);
        }
        catch (Exception) { return false; }
    }

    private static bool HashMatches(byte[] bytes, string hash) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash,
            StringComparison.OrdinalIgnoreCase);

    private static bool ValidPng(byte[] bytes)
    {
        if (bytes.Length < PngSignature.Length) return false;
        for (var i = 0; i < PngSignature.Length; i++)
            if (bytes[i] != PngSignature[i]) return false;
        return true;
    }

    private static bool SafeHash(string hash)
    {
        if (hash == null || hash.Length != 64) return false;
        foreach (var c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
        return true;
    }

    private static byte[] Download(HttpClient client, Uri uri, int maxBytes)
    {
        using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("remote image file is too large");
        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var memory = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new InvalidDataException("remote image file is too large");
            memory.Write(chunk, 0, read);
        }
        return memory.ToArray();
    }
}

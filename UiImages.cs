using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace IMYSHook;

/// <summary>
/// UI 图片替换。
/// <para>约定：成品译文图放 <c>BepInEx/plugins/img_out/&lt;sprite 名&gt;.png</c>
/// （也支持按语言分目录 <c>img_out/&lt;imageLang&gt;/</c>，语言目录优先）。</para>
/// <para>替换点：<c>UI.Image.set_sprite / set_overrideSprite</c>、<c>SpriteRenderer.set_sprite</c>；
/// prefab 反序列化直接写字段的场景由 <see cref="Sweep"/> 周期补齐。</para>
/// <para>待改原图用 tools/extract_atlas_images.py 从游戏缓存离线导出到 <c>img_src/</c>。</para>
/// </summary>
public static class UiImages
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Sprite> Replacements = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PngPathByName = new(StringComparer.OrdinalIgnoreCase);

    private static bool _inited;

    public static string OutDir { get; private set; } = "";
    public static string LangDir { get; private set; } = "";

    /// <summary>已建立的替换表条目数（0 表示无需周期补齐）。</summary>
    public static int ReplacementCount
    {
        get
        {
            lock (Gate) return PngPathByName.Count;
        }
    }

    public static void Init()
    {
        if (_inited) return;
        _inited = true;
        try
        {
            OutDir = Path.Combine(Paths.PluginPath, "img_out");
            LangDir = string.IsNullOrWhiteSpace(UiI18nConfig.ImageLang)
                ? ""
                : Path.Combine(OutDir, UiI18nConfig.ImageLang.Trim());

            foreach (var path in UiImagesRemote.RefreshAndGetPaths(OutDir, UiI18nConfig.ImageLang))
                PngPathByName[Path.GetFileNameWithoutExtension(path)] = path;
            if (Directory.Exists(OutDir)) ScanPngs(OutDir);
            if (LangDir.Length > 0 && Directory.Exists(LangDir)) ScanPngs(LangDir);

            Plugin.Global.Log.LogInfo(
                $"[img] image={UiI18nConfig.ImageOn} replacements={PngPathByName.Count} dir={OutDir}");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogError($"[img] init failed: {e}");
        }
    }

    private static void ScanPngs(string dir)
    {
        foreach (var file in Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 0) continue;
            PngPathByName[name] = file;
        }
    }

    /// <summary>替换贴图；无对应译文图时原样返回。</summary>
    public static Sprite Replace(Sprite original)
    {
        if (original == null) return null;
        if (!UiI18nConfig.ImageOn) return original;

        var name = original.name;
        if (string.IsNullOrEmpty(name)) return original;

        lock (Gate)
        {
            if (Replacements.TryGetValue(name, out var cached) && cached != null) return cached;
            if (!PngPathByName.TryGetValue(name, out var path)) return original;

            var built = Build(name, path, original);
            Replacements[name] = built;
            return built ?? original;
        }
    }

    private static Sprite Build(string name, string path, Sprite original)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.name = name + "_i18n";
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
            {
                UnityEngine.Object.Destroy(tex);
                Plugin.Global.Log.LogWarning($"[img] not an image: {Path.GetFileName(path)}");
                return null;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            var rect = new Rect(0f, 0f, tex.width, tex.height);
            // Sprite.pivot 是原图矩形内的像素坐标，而 Sprite.Create 的 pivot 要求 0..1。
            // 沿用原图的相对轴心，避免替换图在 UI/世界空间偏移。
            var originalRect = original.rect;
            var pivot = originalRect.width > 0f && originalRect.height > 0f
                ? new Vector2(original.pivot.x / originalRect.width,
                    original.pivot.y / originalRect.height)
                : new Vector2(0.5f, 0.5f);
            var sprite = TryCreateSprite(tex, rect, pivot,
                original.pixelsPerUnit > 0f ? original.pixelsPerUnit : 100f, original.border);
            if (sprite == null)
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }

            sprite.name = name;
            Plugin.Global.Log.LogInfo($"[img] replaced '{name}' <- {Path.GetFileName(path)} ({tex.width}x{tex.height})");
            return sprite;
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogError($"[img] build failed '{name}': {e.Message}");
            return null;
        }
    }

    private static Sprite TryCreateSprite(Texture2D tex, Rect rect, Vector2 pivot, float ppu, Vector4 border)
    {
        try
        {
            return Sprite.Create(tex, rect, pivot, ppu, 0, SpriteMeshType.FullRect, border, false);
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[img] Sprite.Create(full) failed, fallback: {e.Message}");
        }

        try
        {
            return Sprite.Create(tex, rect, pivot, ppu);
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogError($"[img] Sprite.Create failed: {e.Message}");
            return null;
        }
    }

    /// <summary>周期补齐：prefab 反序列化不走 set_sprite，需要主动扫一遍。</summary>
    public static void Sweep()
    {
        if (!UiI18nConfig.ImageOn) return;
        try
        {
#if DEBUG
            var scanned = 0;
            var replaced = 0;
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#endif

            foreach (var img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
            {
                if (img == null || img.sprite == null) continue;
                // 资源搜索还会返回大量未激活的预制体；仅处理可见实例，避免切页时
                // 同一帧批量创建/替换上百张根本尚未显示的图片。
                if (!img.gameObject.activeInHierarchy) continue;
#if DEBUG
                scanned++;
#endif
                var name = img.sprite.name;
                if (string.IsNullOrEmpty(name)) continue;
#if DEBUG
                if (distinct.Count < 30 && seen.Add(name)) distinct.Add(name);
#endif

                bool has;
                lock (Gate) has = PngPathByName.ContainsKey(name);
                if (!has) continue;
                var swapped = Replace(img.sprite);
                if (!ReferenceEquals(swapped, img.sprite))
                {
                    img.sprite = swapped;
#if DEBUG
                    replaced++;
#endif
                }
            }

#if DEBUG
            DebugProbe.Hit("Sweep.images", $"scanned={scanned} replaced={replaced} names=[{string.Join(", ", distinct)}]");
#endif
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[img] sweep failed: {e.Message}");
        }
    }
}

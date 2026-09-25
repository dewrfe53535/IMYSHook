using System;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace IMYSHook;

/// <summary>
/// UI 汉化的进阶配置，独立于主 <c>config.json</c>，位置：<c>BepInEx/plugins/i18n/config.json</c>。
/// 主配置里只留一个总开关 <c>uiTranslation</c>；这里放语言、字体、文本/图片子开关等细项。
/// </summary>
public static class UiI18nConfig
{
    /// <summary>文本汉化子开关（还要主配置 uiTranslation 打开）。</summary>
    public static bool TextEnabled = true;

    /// <summary>图片替换子开关（还要主配置 uiTranslation 打开）。</summary>
    public static bool ImageEnabled = false;

    /// <summary>词典语言，对应 <c>i18n/&lt;lang&gt;.json</c>。</summary>
    public static string Lang = "zh_Hant";

    /// <summary>启动时从 HTTPS 下载一次词典；失败时使用上次有效缓存或内置词典。</summary>
    public static bool RemoteEnabled = true;

    /// <summary>词典的 HTTPS 目录（读取 <c>&lt;lang&gt;.version.json</c> 与 <c>&lt;lang&gt;.json</c>）。</summary>
    public static string RemoteBaseUrl = "https://imyshook-staging.24245353.xyz/i18n/";

    /// <summary>图片的 HTTPS 目录（读取 <c>&lt;imageLang&gt;/manifest.json</c> 与对应 PNG）。</summary>
    public static string ImageBaseUrl = "https://imyshook-staging.24245353.xyz/img_out/";

    /// <summary>字体的 HTTPS 目录（读取 <c>manifest.json</c>、字体文件与许可文本）。</summary>
    public static string FontBaseUrl = "https://imyshook-staging.24245353.xyz/font/";

    /// <summary>图片语言子目录 <c>img_out/&lt;imageLang&gt;/</c>；留空只读 <c>img_out/</c> 根目录。</summary>
    public static string ImageLang = "zh_Hant";

    /// <summary>
    /// 汉化字体注入方式：
    /// <list type="bullet">
    /// <item><c>off</c> —— 不动字体（缺字会显示空白/方块）</item>
    /// <item><c>fallback</c> —— 只把汉化字体追加到游戏字体的后备表（缺字时才用；</item>
    /// <item><c>replace</c> —— 命中译文的文本换成汉化字体（游戏字体作为它的后备）。
    /// 默认值：**日文与繁中同形字会用日文字形**（如「過/遠/直/骨」），只有 replace 才能得到正确的繁中字形。</item>
    /// </list>
    /// </summary>
    public static string FontMode = "replace";

    /// <summary>
    /// 指定汉化字体文件（留空则按内置候选顺序找）。可用
    /// <c>C:\Windows\Fonts\msjh.ttc</c>（微軟正黑體，现代黑体）、
    /// <c>C:\Windows\Fonts\mingliu.ttc</c>（细明体，衬线风格更接近原版日文明朝体）。
    /// </summary>
    public static string FontFile = "";

    public static string Dir => Path.Combine(Paths.PluginPath, "i18n");
    public static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>文本汉化最终是否生效。</summary>
    public static bool TextOn => IMYSConfig.UiTranslation && TextEnabled;

    /// <summary>图片替换最终是否生效。</summary>
    public static bool ImageOn => IMYSConfig.UiTranslation && ImageEnabled;

    public static void Read()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

            if (!File.Exists(FilePath))
            {
                WriteJsonFile();
                Plugin.Global.Log.LogInfo($"[i18n] created default {FilePath}");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            var needWrite = false;

            TextEnabled = ReadBool(root, "textEnabled", TextEnabled, ref needWrite);
            ImageEnabled = ReadBool(root, "imageEnabled", ImageEnabled, ref needWrite);
            Lang = ReadString(root, "lang", Lang, ref needWrite);
            RemoteEnabled = ReadBool(root, "remoteEnabled", RemoteEnabled, ref needWrite);
            RemoteBaseUrl = ReadString(root, "remoteBaseUrl", RemoteBaseUrl, ref needWrite);
            ImageBaseUrl = ReadString(root, "imageBaseUrl", ImageBaseUrl, ref needWrite);
            FontBaseUrl = ReadString(root, "fontBaseUrl", FontBaseUrl, ref needWrite);
            ImageLang = ReadString(root, "imageLang", ImageLang, ref needWrite, allowEmpty: true);

            // 兼容旧的 applyFontToUi 布尔开关
            if (root.TryGetProperty("applyFontToUi", out var legacy) &&
                (legacy.ValueKind == JsonValueKind.True || legacy.ValueKind == JsonValueKind.False))
                FontMode = legacy.GetBoolean() ? "fallback" : "off";

            if (root.TryGetProperty("fontMode", out var fm) && fm.ValueKind == JsonValueKind.String)
            {
                var v = (fm.GetString() ?? "").Trim().ToLowerInvariant();
                if (v is "off" or "fallback" or "replace") FontMode = v;
            }
            else
            {
                needWrite = true;
            }

            FontFile = ReadString(root, "fontFile", FontFile, ref needWrite, allowEmpty: true);

            if (needWrite) WriteJsonFile();

            Plugin.Global.Log.LogInfo("UI i18n setting:");
            Plugin.Global.Log.LogInfo("  textEnabled: " + TextEnabled);
            Plugin.Global.Log.LogInfo("  imageEnabled: " + ImageEnabled);
            Plugin.Global.Log.LogInfo("  lang: " + Lang);
            Plugin.Global.Log.LogInfo("  remoteEnabled: " + RemoteEnabled);
            Plugin.Global.Log.LogInfo("  remoteBaseUrl: " + RemoteBaseUrl);
            Plugin.Global.Log.LogInfo("  imageBaseUrl: " + ImageBaseUrl);
            Plugin.Global.Log.LogInfo("  fontBaseUrl: " + FontBaseUrl);
            Plugin.Global.Log.LogInfo("  imageLang: " + (ImageLang.Length == 0 ? "(root)" : ImageLang));
            Plugin.Global.Log.LogInfo("  fontMode: " + FontMode);
            Plugin.Global.Log.LogInfo("  fontFile: " + (FontFile.Length == 0 ? "(auto)" : FontFile));
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogError($"[i18n] config read failed: {e.Message}");
        }
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback, ref bool needWrite)
    {
        if (root.TryGetProperty(name, out var v) &&
            (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            return v.GetBoolean();
        needWrite = true;
        return fallback;
    }

    private static string ReadString(JsonElement root, string name, string fallback, ref bool needWrite,
        bool allowEmpty = false)
    {
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            if (allowEmpty || !string.IsNullOrWhiteSpace(s)) return s;
        }

        needWrite = true;
        return fallback;
    }

    public static void WriteJsonFile()
    {
        var config = new config
        {
            textEnabled = TextEnabled,
            imageEnabled = ImageEnabled,
            lang = Lang,
            remoteEnabled = RemoteEnabled,
            remoteBaseUrl = RemoteBaseUrl,
            imageBaseUrl = ImageBaseUrl,
            fontBaseUrl = FontBaseUrl,
            imageLang = ImageLang,
            fontMode = FontMode,
            fontFile = FontFile
        };
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    public class config
    {
        public bool textEnabled { get; set; }
        public bool imageEnabled { get; set; }
        public string lang { get; set; }
        public bool remoteEnabled { get; set; }
        public string remoteBaseUrl { get; set; }
        public string imageBaseUrl { get; set; }
        public string fontBaseUrl { get; set; }
        public string imageLang { get; set; }
        public string fontMode { get; set; }
        public string fontFile { get; set; }
    }
}

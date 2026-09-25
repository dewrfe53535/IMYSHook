using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx;
using DMM.OLG.Unity.Engine;
using Hachiroku.Novel;
using Hachiroku.Novel.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace IMYSHook;

public class Patch
{
    private static string currentAdvId;
    public static TMP_FontAsset TMPTranslateFont;
    private static bool _fontTried;
    private static readonly Dictionary<int, Material> StyledFontMaterials = new();

    public static void Initialize()
    {
        Harmony.CreateAndPatchAll(typeof(Patch));
    }

    /// <summary>惰性加载已缓存的汉化字体。</summary>
    public static TMP_FontAsset EnsureFont()
    {
        if (TMPTranslateFont != null) return TMPTranslateFont;
        if (_fontTried) return null;
        _fontTried = true;

        try
        {
            // Unity 6 的 TMP 可直接从字体文件创建动态字库。
            var winFonts = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "Fonts");
            var userFont = UiI18nConfig.FontFile;
            if (!string.IsNullOrWhiteSpace(userFont))
            {
                userFont = userFont.Trim();
                if (!Path.IsPathRooted(userFont)) userFont = Path.Combine(Paths.PluginPath, userFont);
            }

            var systemFontPaths = new[]
            {
                // 1) 用户指定（相对路径按 plugins 目录解析）
                string.IsNullOrWhiteSpace(userFont) ? null : userFont,
                // 2) 服务器下载并验证的思源宋体 TC
                UiFontRemote.CachedFontPath,
                // 3) 系统字体兜底
                Path.Combine(winFonts, "msjhbd.ttc"),
                Path.Combine(winFonts, "msjh.ttc"),
                Path.Combine(winFonts, "msyh.ttc")
            };
            foreach (var systemFontPath in systemFontPaths)
            {
                if (string.IsNullOrWhiteSpace(systemFontPath) || !File.Exists(systemFontPath)) continue;

                var dynamicFont = TMP_FontAsset.CreateFontAsset(
                    systemFontPath, 0, 90, 9, GlyphRenderMode.SDFAA, 2048, 2048);
                if (dynamicFont == null || dynamicFont.material == null || dynamicFont.atlasTexture == null)
                    continue;

                dynamicFont.name = "IMYS zh-Hant Dynamic Font";
                TMPTranslateFont = dynamicFont;
                Plugin.Global.Log.LogInfo(
                    $"[i18n] dynamic font loaded: {systemFontPath} mat=True atlas=True");
                return TMPTranslateFont;
            }

        }
        catch (System.Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] translate font load failed: {e.Message}");
        }

        return TMPTranslateFont;
    }

    /// <summary>
    /// 按 <c>i18n/config.json</c> 的 <c>fontMode</c> 给文本挂汉化字体。
    /// <para><c>fallback</c>：把汉化字体追加到游戏字体的后备表（缺字时才用）。</para>
    /// <para><c>replace</c>：文本直接换成汉化字体，并把游戏字体挂成它的后备（保住图标字形）。</para>
    /// </summary>
    public static void ApplyUiFont(TMP_Text text)
    {
        var mode = UiI18nConfig.FontMode;
        if (text == null || mode == "off") return;

        try
        {
            var font = EnsureFont();
            if (font == null) return;

            var current = text.font;
            if (mode == "replace")
            {
                if (current == font) return;
                var originalMaterial = text.fontSharedMaterial;
                DebugProbe.Hit("Font.replace",
                    $"font={current?.name ?? "<null>"} mat={originalMaterial?.name ?? "<null>"}");

                // 不把每个游戏字体反向塞进同一个动态字体的 fallback 表。页面越多，
                // 该共享表越容易积累失效或循环引用；replace 模式也不该再混入原字形。
                text.font = font;

                // font setter 会换掉字体材质。以新字体材质为底，仅复制原 UI 的描边、
                // 粗细、阴影和遮罩参数，保住界面风格而不破坏新图集参数。
                var styledMaterial = GetStyledFontMaterial(font, originalMaterial);
                if (styledMaterial != null) text.fontSharedMaterial = styledMaterial;
                return;
            }

            if (current == null)
            {
                text.font = font;
                return;
            }

            AddFallbackTo(current, font);
        }
        catch (System.Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] apply ui font failed: {e.Message}");
        }
    }

    private static Material GetStyledFontMaterial(TMP_FontAsset font, Material original)
    {
        if (font == null || font.material == null || original == null) return font?.material;

        var key = original.GetInstanceID();
        if (StyledFontMaterials.TryGetValue(key, out var cached) && cached != null) return cached;

        // 从原材质复制可保留 shader keywords 与完整的描边/阴影预设。
        // 动态字体只提供新的 SDF 图集和与图集绑定的度量参数。
        var material = new Material(original)
        {
            name = $"IMYS zh-Hant ({original.name})"
        };
        var source = font.material;
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", font.atlasTexture);
        CopyFloat(source, material, "_GradientScale");
        CopyFloat(source, material, "_TextureWidth");
        CopyFloat(source, material, "_TextureHeight");
        CopyFloat(source, material, "_ScaleX");
        CopyFloat(source, material, "_ScaleY");

        DebugProbe.Hit("Font.material",
            $"original={original.name} shader={original.shader?.name} new={source.shader?.name} " +
            $"gradient={source.GetFloat("_GradientScale")}");

        StyledFontMaterials[key] = material;
        return material;
    }

    private static void CopyFloat(Material source, Material target, string property)
    {
        if (source.HasProperty(property) && target.HasProperty(property))
            target.SetFloat(property, source.GetFloat(property));
    }

    private static void AddFallbackTo(TMP_FontAsset host, TMP_FontAsset item)
    {
        if (host == null || item == null || host == item) return;

        var list = host.fallbackFontAssetTable;
        if (list == null)
        {
            list = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            host.fallbackFontAssetTable = list;
        }

        for (var i = 0; i < list.Count; i++)
            if (list[i] == item)
                return;

        list.Add(item);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NovelRoot), "Start")]
    public static void NovelStart(ref NovelRoot __instance)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        EnsureFont();

        currentAdvId = __instance.Linker.ScenarioId;

        if (!Translation.chapterDicts.ContainsKey(currentAdvId)) Translation.FetchChapterTranslationAsync(currentAdvId).Wait();
        Plugin.Global.Log.LogInfo(currentAdvId);
    }

    // Message
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BurikoParseScript), "_SetMssageCommand")]
    public static void Novel_SetMssageCommand(ref int lineNum, ref string line, ref bool isSelectedCaseArea,
        ref int caseCount)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        if (Translation.chapterDicts.ContainsKey(currentAdvId) && line.Contains("「") && line.EndsWith("」"))
        {
            var idx = line.IndexOf('「');
            var name = line.Substring(0, idx);
            var text = line.Substring(idx);

            var full = "";

            string name_replace;
            if (Translation.nameDicts.TryGetValue(name, out name_replace))
                full = name_replace.IsNullOrWhiteSpace() ? text : name_replace;

            string text_replace;
            if (Translation.chapterDicts[currentAdvId].TryGetValue(text, out text_replace))
            {
                text_replace = text_replace.IsNullOrWhiteSpace() ? text : text_replace;
                text_replace = text_replace.Substring(1, text_replace.Length - 2);
                text_replace = text_replace.Replace("「", "『").Replace("」", "』");
                string final_text = "「" + text_replace + "」";
                full += final_text;
            }
            else
            {
                full += text;
            }

            line = full;
        }
        else
        {
            string text_replace;
            if (Translation.chapterDicts.ContainsKey(currentAdvId) &&
                Translation.chapterDicts[currentAdvId].TryGetValue(line, out text_replace))
            {
                text_replace = text_replace.IsNullOrWhiteSpace() ? line : text_replace;
                text_replace = text_replace.Replace("「", "『").Replace("」", "』");
                line = text_replace;
            }
        }
    }

    // Option
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BurikoParseScript), "_ToParamList")]
    public static void Novel_ToParamList(ref string param)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        var re = new Regex(@"{(.*)}");
        var match = re.Match(param);
        if (match.Success)
            for (var i = 0; i < match.Groups.Count; i++)
            {
                var options = match.Groups[i].Value.Split(",");

                for (var i2 = 0; i2 < options.Length; i2++)
                {
                    string text_replace;
                    if (Translation.chapterDicts.ContainsKey(currentAdvId) && Translation.chapterDicts[currentAdvId]
                            .TryGetValue(options[i2], out text_replace))
                    {
                        var option_tr = text_replace.IsNullOrWhiteSpace() ? options[i2] : text_replace;
                        param = param.Replace(options[i2], option_tr);
                    }
                }
            }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MessageScrollView), "CreateItem")]
    public static void CreateItem(ref MessageScrollViewItem item)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        if (TMPTranslateFont != null)
        {
            item._name.font = TMPTranslateFont;
            item._message.font = TMPTranslateFont;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChoicesContent), "SetChoiceButtonText")]
    public static void SetChoiceButtonText(ref ChoicesContent __instance)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        if (TMPTranslateFont != null)
            for (var i = 0; i < __instance.choiceTextList.Length; i++)
                __instance.choiceTextList[i].font = TMPTranslateFont;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TextRoot), "DeleteRuby")]
    public static void DeleteRuby(ref TextRoot __instance)
    {
        if (!IMYSConfig.TranslationEnabled) return;

        if (TMPTranslateFont != null)
        {
            if (__instance.CharaName) __instance.CharaName.font = TMPTranslateFont;
            if (__instance.Message) __instance.Message.font = TMPTranslateFont;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LoginResponse), "Parse")]
    public static void ParseLoginResp(ref ResponseData res)
    {
        Plugin.Global.Log.LogInfo("Account created at: " + res.contents["created_at"].ToString());
        if (File.Exists($"{Paths.PluginPath}/user.txt") && File.ReadAllText($"{Paths.PluginPath}/user.txt", Encoding.UTF8).IsNullOrWhiteSpace())
        {
            var token = res.contents["token"].ToString();
            Plugin.Global.Log.LogInfo("Account token: " + token);
            File.WriteAllText($"{Paths.PluginPath}/user.txt", token);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AdvSoundPlayer), "StopSoundHelperVoice")]
    public static bool StopSoundHelperVoice(ref AdvSoundPlayer __instance)
    {
        if (IMYSConfig.DoNotVoiceCut)
        {
            var novelRoot = GameObject.FindObjectOfType<NovelRoot>();
            if (novelRoot == null) return true;
            if (novelRoot._facilitator._LastVoiceName.IsNullOrWhiteSpace())
                return false;
            return true;
        }
        else return true;
    }
}

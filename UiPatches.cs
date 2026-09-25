using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using DMM.OLG.Unity.Engine;
using Hachiroku;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IMYSHook;

/// <summary>
/// 全 UI 文本汉化的三层补丁。
/// <para>L1 键层：<c>Localize.Get</c> 两个重载的后置替换。</para>
/// <para>L2 注入层：<c>Localize.Append</c> 前置替换，把译文写进游戏自己的词典（连带覆盖 <c>Format/FormatGroup</c>、
/// 以及直接读词典的 <c>LocalizeComponent</c>）。</para>
/// <para>L3 兜底层：<c>TMP_Text.SetText/set_text</c>、<c>UI.Text.set_text</c> 的短语替换，
/// 覆盖 masterdata 名称、prefab 内写死的文本等一切来源。</para>
/// </summary>
public static class UiPatches
{
    private static Harmony _harmony;
#if DEBUG
    private static bool _menuHierarchyProbed;
#endif
    private static readonly Dictionary<string, (string Original, string Translated)> SplitLoadingGlyphs =
        new(StringComparer.Ordinal)
    {
        ["1yo"] = ("読", "資"),
        ["2mi"] = ("み", "料"),
        ["3ko"] = ("込", "讀"),
        ["4mi"] = ("み", "取"),
        ["5chu"] = ("中", "中")
    };

    public static void Initialize()
    {
        _harmony = new Harmony("imys.ui.i18n");
        var ok = 0;

        // ---------- L1: 键层 ----------
        ok += Try("Localize.Get(string)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Localize), "Get", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(Localize_Get_Post))));

        ok += Try("Localize.Get(string,string)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Localize), "Get", new[] { typeof(string), typeof(string) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(Localize_GetGroup_Post))));

        // ---------- L2: 注入层 ----------
        ok += Try("Localize.Append(string,ref string,ref string,byte)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Localize), "Append",
                    new[]
                    {
                        typeof(string), typeof(string).MakeByRefType(), typeof(string).MakeByRefType(), typeof(byte)
                    }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Localize_Append_Pre))));

        // ---------- L3: 兜底层 ----------
        ok += Try("TMP_Text.SetText(string,bool)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetText_Pre))));

        ok += Try("TMP_Text.SetText(string,float)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetText_Pre))));

        // prefab 里烤好的文本不经过 SetText，靠 OnEnable 在组件启用瞬间补翻译（避免轮询带来的延迟）
        ok += Try("TextMeshProUGUI.OnEnable",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(TmpUgui_OnEnable_Post))));

        ok += Try("TMP_Text.set_text",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "set_text", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetText_Pre))));

        // 有些标签/滚动公告走 char[] / StringBuilder 重载设置文本，绕过了 SetText(string)
        // —— 这些是「翻译后又被打回日文」的元凶，必须补挂
        ok += Try("TMP_Text.SetText(char[])",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(Il2CppStructArray<char>) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetTextArray_Post))));

        ok += Try("TMP_Text.SetText(char[],int,int)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "SetText",
                    new[] { typeof(Il2CppStructArray<char>), typeof(int), typeof(int) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetTextArray_Post))));

        ok += Try("TMP_Text.SetText(StringBuilder)",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(Il2CppSystem.Text.StringBuilder) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(Tmp_SetTextArray_Post))));

        ok += Try("UI.Text.set_text",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Text), "set_text", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(UiText_SetText_Pre))));

        // ---------- 图片层 ----------
        var spriteType = typeof(Sprite);
        ok += Try("UI.Image.set_sprite",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Image), "set_sprite", new[] { spriteType }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Image_SetSprite_Pre))));

        ok += Try("UI.Image.set_overrideSprite",
            () => _harmony.Patch(
                AccessTools.Method(typeof(Image), "set_overrideSprite", new[] { spriteType }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Image_SetSprite_Pre))));

        ok += Try("SpriteRenderer.set_sprite",
            () => _harmony.Patch(
                AccessTools.Method(typeof(SpriteRenderer), "set_sprite", new[] { spriteType }),
                prefix: new HarmonyMethod(typeof(UiPatches), nameof(Image_SetSprite_Pre))));

        // ---------- 探针：确认 TextManager 是否才是界面文本来源 ----------
        ok += Try("TextManager.GetPrefabText",
            () => _harmony.Patch(
                AccessTools.Method(typeof(TextManager), "GetPrefabText", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(UiPatches), nameof(TextManager_GetPrefabText_Post))));

        Plugin.Global.Log.LogInfo($"[i18n] patches applied: {ok}");
    }

    private static int Try(string label, Action action)
    {
        try
        {
            action();
            Plugin.Global.Log.LogInfo($"[i18n] patch ok: {label}");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] patch SKIPPED: {label} -> {e.Message}");
            return 0;
        }
    }

    // ---------------- L1 ----------------

    private static void Localize_Get_Post(string __0, ref string __result)
    {
        DebugProbe.Hit("Localize.Get(key)", __0 + " => " + __result);
        if (!UiI18nConfig.TextOn || string.IsNullOrEmpty(__0)) return;
        if (UiI18n.TryKey(__0, out var translated)) __result = translated;
    }

    private static void Localize_GetGroup_Post(string __0, string __1, ref string __result)
    {
        DebugProbe.Hit("Localize.Get(group,key)", __0 + "/" + __1 + " => " + __result);
        if (!UiI18nConfig.TextOn || string.IsNullOrEmpty(__1)) return;
        if (UiI18n.TryGroup(__0, __1, out var translated)) __result = translated;
    }

    // ---------------- L2 ----------------

    /// <summary><c>Append(group, key, value, priority)</c>：把原文替换为译文后再交给游戏存储。</summary>
    private static void Localize_Append_Pre(string __0, ref string __1, ref string __2, byte __3)
    {
        DebugProbe.Hit("Localize.Append", "group=" + (__0 ?? "<null>") + " key=" + __1 + " value=" + __2);
        if (!UiI18nConfig.TextOn || string.IsNullOrEmpty(__1)) return;

        var translated = UiI18n.TranslateKeyed(__0, __1, __2);
        if (string.IsNullOrEmpty(translated) || string.Equals(translated, __2, StringComparison.Ordinal)) return;

        // 游戏内部会拿这个 value 去 string.Format；占位符数量不一致就宁可不换，避免 FormatException 把弹窗搞成空白
        if (UiI18n.PlaceholderCount(translated) != UiI18n.PlaceholderCount(__2)) return;

        __2 = translated;
    }

    // ---------------- 探针 ----------------

    private static void TextManager_GetPrefabText_Post(string __0, ref string __result)
    {
        DebugProbe.Hit("TextManager.GetPrefabText", __0 + " => " + __result);
    }

    // ---------------- 周期补齐（prefab 烤好的文本） ----------------

    /// <summary>
    /// prefab 里烤好的文本在组件启用时补翻译（即时，无轮询延迟）。
    /// <para>必须挂**声明了 OnEnable 的具体类** <see cref="TextMeshProUGUI"/>；
    /// 之前挂 <c>TMP_Text.OnEnable</c> 会被 Harmony 解析成 <c>MaskableGraphic.OnEnable</c>，
    /// 于是每个 Graphic 都会进来、参数类型不符直接 AccessViolation 崩游戏。</para>
    /// </summary>
    private static void TmpUgui_OnEnable_Post(TextMeshProUGUI __instance)
    {
        if (!UiI18nConfig.TextOn || __instance == null) return;

        string current;
        try
        {
            current = __instance.text;
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(current)) return;
        DebugProbe.Hit("TMP.OnEnable", current);
        if (TryMapSplitLoadingGlyph(__instance, current, out var glyph))
        {
            try
            {
                __instance.text = glyph;
                Patch.ApplyUiFont(__instance);
                DebugProbe.Hit("APPLY.SplitLoading.glyph", __instance.name + " " + current + " => " + glyph);
            }
            catch (Exception e)
            {
                DebugProbe.Hit("SplitLoading.err", e.Message);
            }
            return;
        }
        if (SplitLoadingGlyphs.ContainsKey(current))
            DebugProbe.Hit("SplitLoading.path", current + " " + TextPath(__instance.transform));

        // 标题加载动画是同一 TextRoot 下的五个独立对象，两个「み」必须靠对象名区分。
        if (SplitLoadingGlyphs.ContainsKey(__instance.name) && TryTranslateSplitLoading(__instance)) return;

        var translated = UiI18n.Translate(current);
        if (string.Equals(translated, current, StringComparison.Ordinal)) return;

        try
        {
            __instance.text = translated;
            Patch.ApplyUiFont(__instance);
            DebugProbe.Hit("APPLY.OnEnable", current + "  =>  " + translated);
        }
        catch (Exception e)
        {
            DebugProbe.Hit("OnEnable.err", e.Message);
        }
    }

    /// <summary>
    /// 补齐 prefab 里烤好的文本（不走 SetText/set_text）。
    /// <para>不走 <c>TMP_Text.OnEnable</c> 补丁：TMP 组件在启动早期状态未就绪，
    /// 在那里读写 <c>text</c> 会把游戏打崩（实测过），所以改用和图片同款的轮询方案。</para>
    /// </summary>
    public static void SweepText()
    {
        if (!UiI18nConfig.TextOn) return;
        try
        {
#if DEBUG
            var scanned = 0;
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
#endif
            var changed = 0;
            var splitLoadingSeeds = new List<TMP_Text>();

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null) continue;

                string current;
                try
                {
                    current = text.text;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(current)) continue;
                if (SplitLoadingGlyphs.ContainsKey(text.name)) splitLoadingSeeds.Add(text);
                // FindObjectsOfTypeAll 还包含尚未显示的 prefab/页面。预翻译那些对象会在
                // 出击切页时集中修改数百个 TMP 与字体，造成明显卡顿；启用时已有 OnEnable 兜底。
                if (!text.gameObject.activeInHierarchy) continue;
#if DEBUG
                scanned++;
                if (distinct.Count < 30 && seen.Add(current)) distinct.Add(current);
#endif
                if (current is "ギルド" or "公會")
                    DebugProbe.Hit("Guild.path", current + " " + TextPath(text.transform) +
                        " active=" + text.gameObject.activeInHierarchy + " font=" + text.font?.name);
                if (text.gameObject.activeInHierarchy &&
                    current is "ギルド" or "公會" or "学園" or "學園" or "試練" or "召喚")
                    DebugProbe.Hit("Menu.visible", current + " " + TextPath(text.transform) +
                        " mat=" + text.fontSharedMaterial?.name);

                var translated = TryMapSplitLoadingGlyph(text, current, out var mapped)
                    ? mapped : UiI18n.Translate(current);
                if (string.Equals(translated, current, StringComparison.Ordinal)) continue;

                try
                {
                    text.text = translated;
                    Patch.ApplyUiFont(text);
                    changed++;
                    DebugProbe.Hit("APPLY", current + "  =>  " + translated);
                }
                catch (Exception)
                {
                    // 单个组件失败不影响其它
                }

                if (changed >= 300) break;
            }

            foreach (var seed in splitLoadingSeeds)
                if (TryTranslateSplitLoading(seed)) break;

#if DEBUG
            ProbeMenuHierarchy();
#endif

            // 旧式 UnityEngine.UI.Text 的 prefab 文本不经过 TMP 补丁；菜单上叠着的
            // 「ギルド」一类标签可能正走这条路径，因此也做同样的周期兜底。
#if DEBUG
            var legacyScanned = 0;
            var legacyChanged = 0;
#endif
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (text == null) continue;
                if (!text.gameObject.activeInHierarchy) continue;
                string current;
                try { current = text.text; }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(current)) continue;
#if DEBUG
                legacyScanned++;
#endif

                var translated = UiI18n.Translate(current);
                if (string.Equals(translated, current, StringComparison.Ordinal)) continue;
                try
                {
                    text.text = translated;
#if DEBUG
                    legacyChanged++;
#endif
                    DebugProbe.Hit("APPLY.UIText", current + "  =>  " + translated);
                }
                catch (Exception)
                {
                }
            }

#if DEBUG
            DebugProbe.Hit("Sweep.text",
                $"tmp={scanned}/{changed} uiText={legacyScanned}/{legacyChanged} sample=[{string.Join(" | ", distinct)}]");
#endif
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] text sweep failed: {e.Message}");
        }
    }

#if DEBUG
    private static void ProbeMenuHierarchy()
    {
        if (_menuHierarchyProbed) return;
        foreach (var root in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (root == null || root.name != "GlobalMenuModal(Clone)" || !root.gameObject.activeInHierarchy)
                continue;
            _menuHierarchyProbed = true;
            var count = 0;
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (node == null) continue;
                var path = TextPath(node);
                var image = node.GetComponent<Image>();
                var raw = node.GetComponent<RawImage>();
                var tmp = node.GetComponent<TMP_Text>();
                var sprite = node.GetComponent<SpriteRenderer>();
                var detail = (tmp != null ? " TMP=" + tmp.text : "") +
                             (image != null ? " Image=" + image.sprite?.name : "") +
                             (raw != null ? " Raw=" + raw.texture?.name : "") +
                             (sprite != null ? " Sprite=" + sprite.sprite?.name : "");
                if (path.IndexOf("guild", StringComparison.OrdinalIgnoreCase) < 0 &&
                    detail.IndexOf("guild", StringComparison.OrdinalIgnoreCase) < 0 &&
                    detail.IndexOf("ギルド", StringComparison.Ordinal) < 0 &&
                    detail.IndexOf("公會", StringComparison.Ordinal) < 0) continue;
                DebugProbe.Hit("Menu.guild.tree", path + " active=" + node.gameObject.activeInHierarchy + detail);
                if (++count >= 60) break;
            }
            DebugProbe.Hit("Menu.guild.tree", "TOTAL=" + count);
            break;
        }
    }
#endif

    private static bool TryTranslateSplitLoading(TMP_Text seed)
    {
        if (seed == null) return false;
        try
        {
            var root = seed.transform.parent;
            if (root == null || root.name != "TextRoot") return false;
            var texts = root.GetComponentsInChildren<TMP_Text>(true);
            if (texts == null || texts.Length > 16) return false;

            var found = new Dictionary<string, TMP_Text>(StringComparer.Ordinal);
            foreach (var text in texts)
            {
                if (text == null || !SplitLoadingGlyphs.ContainsKey(text.name)) continue;
                if (found.ContainsKey(text.name)) return false;
                found[text.name] = text;
            }

            if (found.Count != SplitLoadingGlyphs.Count) return false;
            var changed = false;
            foreach (var pair in SplitLoadingGlyphs)
            {
                var text = found[pair.Key];
                var value = text.text;
                if (value != pair.Value.Original && value != pair.Value.Translated) return false;
                if (value == pair.Value.Original) changed = true;
            }

            if (!changed) return false;
            foreach (var pair in SplitLoadingGlyphs)
            {
                var text = found[pair.Key];
                if (text.text != pair.Value.Translated) text.text = pair.Value.Translated;
                Patch.ApplyUiFont(text);
            }

            DebugProbe.Hit("APPLY.SplitLoading", "読み込み中  =>  資料讀取中");
            return true;
        }
        catch (Exception e)
        {
            DebugProbe.Hit("SplitLoading.err", e.Message);
        }

        return false;
    }

    private static bool TryMapSplitLoadingGlyph(TMP_Text text, string current, out string translated)
    {
        translated = null;
        if (text == null || !SplitLoadingGlyphs.TryGetValue(text.name, out var glyph) ||
            current != glyph.Original || current == glyph.Translated)
            return false;
        // 加载画面会创建多份/重设单字对象；对象名与原字双重校验后可逐字即时翻译，
        // 不再要求五个兄弟节点已全部启用或父节点恰好叫 TextRoot。
        translated = glyph.Translated;
        return true;
    }

    private static string TextPath(Transform transform)
    {
        var parts = new List<string>();
        for (var i = 0; transform != null && i < 8; i++, transform = transform.parent)
            parts.Add(transform.name);
        return string.Join("/", parts);
    }

    // ---------------- L3 ----------------

    private static void Tmp_SetText_Pre(TMP_Text __instance, ref string __0)
    {
        DebugProbe.Hit("TMP.SetText", __0);
        if (!UiI18nConfig.TextOn || __instance == null || string.IsNullOrEmpty(__0)) return;

        // 加载动画的五个字是独立 TMP，场景切换时动画会再次写入原字。
        // 在写入入口拦截，避免仅靠 OnEnable/周期扫描时短暂显示日文。
        if (TryMapSplitLoadingGlyph(__instance, __0, out var mappedGlyph))
        {
            try
            {
                __0 = mappedGlyph;
                Patch.ApplyUiFont(__instance);
                DebugProbe.Hit("APPLY.SplitLoading.write", __instance.name + " => " + __0);
                return;
            }
            catch (Exception)
            {
                // 生命周期切换期间 transform 可能失效；交给 OnEnable/扫描兜底。
            }
        }

        var translated = UiI18n.Translate(__0);
        if (string.Equals(translated, __0, StringComparison.Ordinal)) return;

        __0 = translated;
        Patch.ApplyUiFont(__instance);
        DebugProbe.Hit("APPLY.SetText", translated);
    }

    private static void UiText_SetText_Pre(Text __instance, ref string __0)
    {
        DebugProbe.Hit("UI.Text.set_text", __0);
        if (!UiI18nConfig.TextOn || __instance == null || string.IsNullOrEmpty(__0)) return;
        var translated = UiI18n.Translate(__0);
        if (string.Equals(translated, __0, StringComparison.Ordinal)) return;
        __0 = translated;
    }

    /// <summary>char[] / StringBuilder 重载的后置：读回 <c>.text</c> 再走一遍短语替换。</summary>
    private static void Tmp_SetTextArray_Post(TMP_Text __instance)
    {
        if (!UiI18nConfig.TextOn || __instance == null) return;

        string current;
        try
        {
            current = __instance.text;
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(current)) return;
        var translated = UiI18n.Translate(current);
        if (string.Equals(translated, current, StringComparison.Ordinal)) return;

        try
        {
            __instance.text = translated;
            Patch.ApplyUiFont(__instance);
            DebugProbe.Hit("APPLY.Array", current + "  =>  " + translated);
        }
        catch (Exception)
        {
        }
    }

    // ---------------- 图片层 ----------------

    private static void Image_SetSprite_Pre(ref Sprite __0)
    {
        if (__0 == null) return;
        var replaced = UiImages.Replace(__0);
        DebugProbe.Hit("Image.set_sprite", __0.name + (ReferenceEquals(replaced, __0) ? " (keep)" : " (REPLACED)"));
        __0 = replaced;
    }
}

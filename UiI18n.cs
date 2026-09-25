using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;

namespace IMYSHook;

/// <summary>
/// UI 汉化词典。
/// <para>目录：<c>BepInEx/plugins/i18n/</c></para>
/// <para>词典文件：<c>&lt;lang&gt;.json</c>、<c>&lt;lang&gt;.local.json</c>（后者覆盖前者）。</para>
/// <code>
/// {
///   "keys":    { "CAVE_MODAL_TITLE": "未開放" },
///   "groups":  { "commontext": { "Detail": "詳細" } },
///   "phrases": { "期間外です": "期間外" }
/// }
/// </code>
/// </summary>
public static class UiI18n
{
    private const char GroupSeparator = '\u0001';
    private static readonly object Gate = new();

    private static readonly Dictionary<string, string> KeyDict = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> GroupDict = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> PhraseDict = new(StringComparer.Ordinal);

    /// <summary>带占位符的模板规则（原文 `残り{0}` → 译文 `剩餘{0}`），用于匹配「已代入实参」的运行时文本。</summary>
    private static readonly List<TemplateRule> Templates = new();

    /// <summary>模板匹配结果缓存（含未命中），避免重复跑正则。</summary>
    private static readonly Dictionary<string, string> Resolved = new(StringComparer.Ordinal);

    private sealed class TemplateRule
    {
        public string Leading;                       // 第一个占位符之前的字面量，用于快速预筛
        public Regex Pattern;
        public Regex TargetPattern;                  // 已翻译文本的形态，避免再次套用同一模板
        public string Target;                        // 译文模板
        public List<(int Start, int Length)> Slots;  // 译文模板里占位符的位置（按出现顺序）
    }

    public static string Dir { get; private set; } = "";
    public static string Lang { get; private set; } = "zh_Hant";

    /// <summary>已编译的占位符模板条数（日志用）。</summary>
    public static int TemplateCount => Templates.Count;

    private static string GroupKey(string group, string key) => group + GroupSeparator + key;

    public static void Init()
    {
        Dir = Path.Combine(Paths.PluginPath, "i18n");
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogWarning($"[i18n] create dir failed: {e.Message}");
        }

        Lang = string.IsNullOrWhiteSpace(UiI18nConfig.Lang) ? "zh_Hant" : UiI18nConfig.Lang.Trim();
        lock (Gate)
        {
            KeyDict.Clear();
            GroupDict.Clear();
            PhraseDict.Clear();
        }
        LoadFile(Path.Combine(Dir, Lang + ".json"));
        var remoteCache = UiI18nRemote.RefreshAndGetCache(Dir, Lang);
        if (remoteCache != null) LoadFile(remoteCache);
        LoadFile(Path.Combine(Dir, Lang + ".local.json"));
        BuildTemplates();

        Plugin.Global.Log.LogInfo(
            $"[i18n] text={UiI18nConfig.TextEnabled} lang={Lang} " +
            $"keys={KeyDict.Count} groups={GroupDict.Count} phrases={PhraseDict.Count} " +
            $"templates={Templates.Count}");
    }

    private static void LoadFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            var before = KeyDict.Count + GroupDict.Count + PhraseDict.Count;

            if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
                foreach (var p in keys.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) KeyDict[p.Name] = p.Value.GetString();

            if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Object)
                foreach (var g in groups.EnumerateObject())
                    if (g.Value.ValueKind == JsonValueKind.Object)
                        foreach (var p in g.Value.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String)
                                GroupDict[GroupKey(g.Name, p.Name)] = p.Value.GetString();

            if (root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Object)
                foreach (var p in phrases.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) PhraseDict[p.Name] = p.Value.GetString();

            // 容错：顶层「key -> 字符串」直接当 keys 收下（下划线开头的元信息键除外）
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !p.Name.StartsWith("_"))
                    KeyDict[p.Name] = p.Value.GetString();

            Plugin.Global.Log.LogInfo(
                $"[i18n] loaded {Path.GetFileName(path)}: +{KeyDict.Count + GroupDict.Count + PhraseDict.Count - before} entries");
        }
        catch (Exception e)
        {
            Plugin.Global.Log.LogError($"[i18n] load failed {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>
    /// 把含占位符的「原文 → 译文」编成正则规则。
    /// <para>运行时文本里的实参已经被代入（例：模板 `{0}ウェーブ毎にセーブされます。` 实际是
    /// `10ウェーブ毎にセーブされます。`），精确短语匹配必然落空，只能靠模板匹配补回来。</para>
    /// </summary>
    private static void BuildTemplates()
    {
        Templates.Clear();
        lock (Gate)
        {
            foreach (var kv in PhraseDict)
            {
                var jp = kv.Key;
                if (jp.IndexOf('{') < 0) continue;

                var body = new StringBuilder();
                var leading = new StringBuilder();
                var seenSlot = false;
                var literalLen = 0;
                var ok = true;

                for (var i = 0; i < jp.Length; i++)
                {
                    if (jp[i] == '{')
                    {
                        var close = jp.IndexOf('}', i);
                        if (close < 0)
                        {
                            ok = false;
                            break;
                        }

                        // 「ステップ{0}」的参数只能是阶段数字，不能把「ステップアップ」
                        // 误当成参数「アップ」，生成「第アップ階段」。
                        body.Append(jp == "ステップ{0}" ? "([0-9０-９]+)" : "(.+?)");
                        seenSlot = true;
                        i = close;
                    }
                    else
                    {
                        body.Append(Regex.Escape(jp[i].ToString()));
                        if (!seenSlot) leading.Append(jp[i]);
                        literalLen++;
                    }
                }

                // 整条都是占位符（如 `{0}`）会匹配一切，直接丢弃；其余靠 ^...$ 锚定足够安全
                if (!ok || !seenSlot || literalLen == 0 || jp.Length < 4) continue;

                // 译文与原文完全相同（繁中与日文同形，例 `獲得{0}`→`獲得{0}`、`{0}【{1}】` 原样）
                // 编成模板后会“吞掉整串却原样返回”，把后面真正能翻的规则挡住 → 必须丢弃
                if (string.Equals(kv.Value, jp, StringComparison.Ordinal)) continue;

                int slots;
                var zhSlots = ExtractSlots(kv.Value, out slots);
                if (slots != CountSlots(jp)) continue;

                var targetBody = new StringBuilder();
                var targetCursor = 0;
                foreach (var (start, length) in zhSlots)
                {
                    targetBody.Append(Regex.Escape(kv.Value.Substring(targetCursor, start - targetCursor)));
                    targetBody.Append("(.+?)");
                    targetCursor = start + length;
                }
                targetBody.Append(Regex.Escape(kv.Value.Substring(targetCursor)));

                Templates.Add(new TemplateRule
                {
                    Leading = leading.ToString(),
                    Pattern = new Regex("^" + body + "$", RegexOptions.Singleline),
                    TargetPattern = new Regex("^" + targetBody + "$", RegexOptions.Singleline),
                    Target = kv.Value,
                    Slots = zhSlots
                });
            }

            // 长字面量优先，减少误匹配
            Templates.Sort((a, b) => b.Leading.Length.CompareTo(a.Leading.Length));
            Resolved.Clear();
        }

        Plugin.Global.Log.LogInfo($"[i18n] placeholder templates compiled: {Templates.Count}");
    }

    private static List<(int, int)> ExtractSlots(string s, out int count)
    {
        var slots = new List<(int, int)>();
        count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;
            var close = s.IndexOf('}', i);
            if (close < 0) break;
            slots.Add((i, close - i + 1));
            count++;
            i = close;
        }

        return slots;
    }

    private static int CountSlots(string s)
    {
        int dummy;
        return ExtractSlots(s, out dummy).Count;
    }

    private static bool TryTemplate(string text, out string result)
    {
        result = null;
        if (Templates.Count == 0) return false;

        lock (Gate)
        {
            if (Resolved.TryGetValue(text, out var cached))
            {
                if (string.Equals(cached, text, StringComparison.Ordinal)) return false;
                result = cached;
                return true;
            }
        }

        string hit = null;
        lock (Gate)
        {
            foreach (var rule in Templates)
            {
                if (rule.Leading.Length > 0 &&
                    !text.StartsWith(rule.Leading, StringComparison.Ordinal))
                    continue;

                var m = rule.Pattern.Match(text);
                if (!m.Success) continue;
                // 例如「{0}話」→「第{0}話」：译文仍符合原模板，下一轮扫描
                // 会变成「第第{0}話」。已是目标形态时不再应用该规则。
                if (rule.TargetPattern.IsMatch(text)) continue;

                if (rule.Slots.Count == 0)
                {
                    hit = rule.Target;
                }
                else
                {
                    var sb = new StringBuilder();
                    var cur = 0;
                    var gi = 1;
                    foreach (var (start, len) in rule.Slots)
                    {
                        sb.Append(rule.Target, cur, start - cur);
                        if (gi < m.Groups.Count) sb.Append(m.Groups[gi].Value);
                        gi++;
                        cur = start + len;
                    }

                    sb.Append(rule.Target, cur, rule.Target.Length - cur);
                    hit = sb.ToString();
                }

                break;
            }

            if (Resolved.Count > 8192) Resolved.Clear();
            Resolved[text] = hit ?? text;
        }

        result = hit;
        return hit != null;
    }

    /// <summary>无分组 key 查询。</summary>
    public static bool TryKey(string key, out string value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;
        lock (Gate)
        {
            return KeyDict.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
        }
    }

    /// <summary>分组 key 查询（先精确 group/key，再退化到裸 key）。</summary>
    public static bool TryGroup(string group, string key, out string value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(group) && GroupDict.TryGetValue(GroupKey(group, key), out value) &&
                !string.IsNullOrWhiteSpace(value))
                return true;
            return KeyDict.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
        }
    }

    /// <summary>按「原文短语」查询译文；查不到再试占位符模板；都没有则原样返回。</summary>
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (PhraseDict.Count == 0) return text;
        if (!HasCjk(text)) return text;

        lock (Gate)
        {
            if (PhraseDict.TryGetValue(text, out var v) && !string.IsNullOrWhiteSpace(v)) return v;

            // 序列化来源同时出现 LF、CRLF 和单独 CR；统一换行后再匹配完整短语。
            var normalized = text.IndexOf('\r') >= 0
                ? text.Replace("\r\n", "\n").Replace('\r', '\n')
                : text;
            if (!ReferenceEquals(normalized, text) &&
                PhraseDict.TryGetValue(normalized, out v) && !string.IsNullOrWhiteSpace(v)) return v;

            // prefab 标签有时在词尾附带换行（Menu 的「ギルド\n」即如此）。
            // 只在完整短语命中时翻译，并把首尾空白原样放回，避免改变排版。
            var start = 0;
            var end = normalized.Length;
            while (start < end && char.IsWhiteSpace(normalized[start])) start++;
            while (end > start && char.IsWhiteSpace(normalized[end - 1])) end--;
            if ((start != 0 || end != normalized.Length) && start < end &&
                PhraseDict.TryGetValue(normalized.Substring(start, end - start), out v) &&
                !string.IsNullOrWhiteSpace(v))
                return normalized.Substring(0, start) + v + normalized.Substring(end);
        }

        // 剧情翻译已下载角色名/称号对照表，UI 里的同名标签直接复用。
        // 仅做完整字符串命中，不对普通 UI 文案进行子串替换。
        var names = Translation.nameDicts;
        if (names != null && names.TryGetValue(text, out var translatedName) &&
            !string.IsNullOrWhiteSpace(translatedName))
            return translatedName;

        var templateInput = text.IndexOf('\r') >= 0
            ? text.Replace("\r\n", "\n").Replace('\r', '\n')
            : text;
        return TryTemplate(templateInput, out var templated) && !string.IsNullOrWhiteSpace(templated)
            ? templated
            : text;
    }

    /// <summary>格式占位符个数（`{0}` / `{0:s}` / `{1:#,#}` 均算 1 个）。</summary>
    public static int PlaceholderCount(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;
            var close = s.IndexOf('}', i);
            if (close < 0) break;
            n++;
            i = close;
        }

        return n;
    }

    /// <summary>按 key 查询，失败再按原文短语查询；查不到原样返回。</summary>
    public static string TranslateKeyed(string group, string key, string fallback)
    {
        if (TryGroup(group, key, out var byKey)) return byKey;
        if (!string.IsNullOrEmpty(fallback) && TryKey(fallback, out var byKey2)) return byKey2;
        return Translate(fallback);
    }

    /// <summary>是否含 CJK 字符（假名 / 汉字 / 全角）。纯 ASCII 串无需翻译，快速跳过。</summary>
    public static bool HasCjk(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c >= '\u3000' && c <= '\u30ff') return true;
            if (c >= '\u3400' && c <= '\u9fff') return true;
            if (c >= '\uf900' && c <= '\ufaff') return true;
            if (c >= '\uff00' && c <= '\uffef') return true;
        }
        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;

namespace IMYSHook;

/// <summary>Debug 构建的文本与图片诊断日志。</summary>
public static class DebugProbe
{
    private const int MaxLines = 120;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> Seen = new();
    private static readonly Dictionary<string, int> Count = new();

    private static ManualLogSource Log => Plugin.Global.Log;

    [Conditional("DEBUG")]
    public static void Hit(string channel, string sample)
    {
        lock (Gate)
        {
            if (!Count.TryGetValue(channel, out var n)) Count[channel] = 0;
            Count[channel] = n + 1;

            if (!Seen.TryGetValue(channel, out var set))
                Seen[channel] = set = new HashSet<string>(StringComparer.Ordinal);
            if (set.Count >= MaxLines || !set.Add(sample ?? "<null>")) return;
        }

        Log.LogInfo($"[probe:{channel}] {Trunc(sample)}");
    }

    [Conditional("DEBUG")]
    public static void Summary()
    {
        string body;
        lock (Gate)
        {
            if (Count.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in Count) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            body = sb.ToString();
            Count.Clear();
        }

        Log.LogInfo($"[probe:count] {body}");
    }

    private static string Trunc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        s = s.Replace("\n", "\\n").Replace("\r", "");
        return s.Length <= 110 ? s : s.Substring(0, 110) + "…";
    }
}

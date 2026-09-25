using BepInEx;
using System.IO;
using System.Text;
using System.Text.Json;

namespace IMYSHook;

public class IMYSConfig
{
    public static double Speed;
    public static int FPS;
    public static bool TranslationEnabled;
    public static bool DoNotVoiceCut;

    /// <summary>全 UI 汉化总开关（文本 + 图片）。细项见 <c>i18n/config.json</c>（<see cref="UiI18nConfig"/>）。</summary>
    public static bool UiTranslation;

    public static void Read()
    {
        if (File.Exists($"{Paths.PluginPath}/config.json"))
        {
            var content = File.ReadAllText($"{Paths.PluginPath}/config.json", Encoding.UTF8);
            var doc = JsonDocument.Parse(content);
            var config = doc.RootElement;

            var needWrite = false;

            if (config.TryGetProperty("speed", out var sValue))
            {
                Speed = sValue.GetDouble();
            }
            else
            {
                Speed = 0.5;
                needWrite = true;
            }

            if (config.TryGetProperty("fps", out var fValue))
            {
                FPS = fValue.GetInt32();
            }
            else
            {
                FPS = 60;
                needWrite = true;
            }

            if (config.TryGetProperty("translation", out var tValue))
            {
                TranslationEnabled = tValue.GetBoolean();
            }
            else
            {
                TranslationEnabled = true;
                needWrite = true;
            }

            if (config.TryGetProperty("DoNotVoiceCut", out var vValue))
            {
                DoNotVoiceCut = vValue.GetBoolean();
            }
            else
            {
                DoNotVoiceCut = false;
                needWrite = true;
            }

            if (config.TryGetProperty("uiTranslation", out var uValue))
            {
                UiTranslation = uValue.GetBoolean();
            }
            else
            {
                UiTranslation = true;
                needWrite = true;
            }

            if (needWrite) WriteJsonFile();

            Plugin.Global.Log.LogInfo("Current setting:");
            Plugin.Global.Log.LogInfo("Game speed(each step): " + Speed);
            Plugin.Global.Log.LogInfo("FPS: " + FPS);
            Plugin.Global.Log.LogInfo("Translation: " + (TranslationEnabled ? "Enabled" : "Disabled"));
            Plugin.Global.Log.LogInfo("Disable Voice cut: " + (DoNotVoiceCut ? "Enabled" : "Disabled"));
            Plugin.Global.Log.LogInfo("UI translation: " + (UiTranslation ? "Enabled" : "Disabled"));
        }
        else
        {
            Plugin.Global.Log.LogWarning("config.json not found!!!");
            Plugin.Global.Log.LogWarning("Using default config.");
            Speed = 0.5;
            FPS = 60;
            TranslationEnabled = true;
            DoNotVoiceCut = false;
            UiTranslation = true;

            // Create default JSON file
            WriteJsonFile();
        }
    }

    public static void WriteJsonFile()
    {
        var config = new config
        {
            speed = Speed,
            fps = FPS,
            translation = TranslationEnabled,
            DoNotVoiceCut = DoNotVoiceCut,
            uiTranslation = UiTranslation
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText($"{Paths.PluginPath}/config.json", json);
    }

    public class config
    {
        public double speed { get; set; }
        public int fps { get; set; }
        public bool translation { get; set; }
        public bool DoNotVoiceCut { get; set; }
        public bool uiTranslation { get; set; }
    }
}

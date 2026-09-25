using System;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace IMYSHook;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        if (Console.LargestWindowWidth > 0)
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

        var log = Log;
        Global.Log = log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        IMYSConfig.Read();
        Translation.InitAsync().Wait();
        Patch.Initialize();

        // 全 UI 汉化（开关在主 config.json，细项在 i18n/config.json）
        UiI18nConfig.Read();
        UiFontRemote.Init();
        UiI18n.Init();
        UiImages.Init();
        UiPatches.Initialize();

        AddComponent<PluginBehavior>();
    }

    public class Global
    {
        public static ManualLogSource Log { get; set; }
    }
}

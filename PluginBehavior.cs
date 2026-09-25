using System;
using UnityEngine;

namespace IMYSHook;

public class PluginBehavior : MonoBehaviour
{
    private static readonly float WaitTime = 1.0f;
    public static bool IsGameSpeedChanged { get; set; }
    public static float CurrentGameSpeed { get; set; }
    private static float LastGSExecuteTime { get; set; }
    private static float LastFPSExecuteTime { get; set; }
    private float _lastSweepTime;
    private float _lastImageSweepTime = -1.5f;
#if DEBUG
    private float _lastProbeTime;
#endif

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            CurrentGameSpeed = Time.timeScale + (float)IMYSConfig.Speed;
            Time.timeScale += (float)IMYSConfig.Speed;
            IsGameSpeedChanged = (int)Time.timeScale != 1;
            LastGSExecuteTime = Time.deltaTime;
            var currSpeed = Time.timeScale.ToString();
            var text = "Game speed increased. Current: " + currSpeed + "x";
            Plugin.Global.Log.LogInfo(text);

            Notification.Popup("Game Speed", text);
        }

        if (Input.GetKeyDown(KeyCode.F7))
        {
            CurrentGameSpeed = Time.timeScale - (float)IMYSConfig.Speed;
            Time.timeScale -= (float)IMYSConfig.Speed;
            IsGameSpeedChanged = (int)Time.timeScale != 1;
            LastGSExecuteTime = Time.deltaTime;
            var currSpeed = Time.timeScale.ToString();
            var text = "Game speed decreased. Current: " + currSpeed + "x";
            Plugin.Global.Log.LogInfo(text);

            Notification.Popup("Game Speed", text);
        }

        if (Input.GetKeyDown(KeyCode.F6))
        {
            CurrentGameSpeed = 1.0f;
            Time.timeScale = 1.0f;
            IsGameSpeedChanged = (int)Time.timeScale != 1;
            var currSpeed = Time.timeScale.ToString();
            var text = "Game speed restored. Current: " + currSpeed + "x";
            Plugin.Global.Log.LogInfo(text);

            Notification.Popup("Game Speed", text);
        }

        if (Input.GetKeyDown(KeyCode.F5))
        {
            CurrentGameSpeed = 0.0f;
            Time.timeScale = 0.0f;
            IsGameSpeedChanged = (int)Time.timeScale != 1;
            LastGSExecuteTime = Time.deltaTime;
            var currSpeed = Time.timeScale.ToString();
            var text = "Game speed freezed. Current: " + currSpeed + "x";
            Plugin.Global.Log.LogInfo(text);

            Notification.Popup("Game Speed", text);
        }

        if (Input.GetKeyDown(KeyCode.F10))
        {
            Translation.chapterDicts = new();
            Plugin.Global.Log.LogInfo("[Translator] cache cleared.");
            Notification.Popup("Translation", "Translation cache cleared.");
        }

        if (Input.GetKeyDown(KeyCode.F11))
        {
            IMYSConfig.TranslationEnabled = !IMYSConfig.TranslationEnabled;
            Plugin.Global.Log.LogInfo("Translation: " + (IMYSConfig.TranslationEnabled ? "Enabled" : "Disabled"));
            Notification.Popup("Translation", IMYSConfig.TranslationEnabled ? "Enabled" : "Disabled");
        }

        if (Input.GetKeyDown(KeyCode.F12))
        {
            var username = Environment.UserName;
            var timeFormat = DateTime.Now.ToString("yyyyMMdd_HHmmssff");
            var location = string.Format("C:\\Users\\{0}\\Pictures\\imys_{1}.png", username, timeFormat);
            ScreenCapture.CaptureScreenshot(location);

            Notification.SsPopup(location);
        }

        LastGSExecuteTime += Time.deltaTime;
        if (IsGameSpeedChanged && LastGSExecuteTime >= WaitTime && Time.timeScale != CurrentGameSpeed)
        {
            LastGSExecuteTime = 0.0f;
            Time.timeScale = CurrentGameSpeed;
            Plugin.Global.Log.LogInfo("Game speed changed. Reset to: " + CurrentGameSpeed + "x");
        }

        LastFPSExecuteTime += Time.deltaTime;
        if (IMYSConfig.FPS >= 30 && LastFPSExecuteTime >= WaitTime && Application.targetFrameRate < IMYSConfig.FPS)
        {
            LastFPSExecuteTime = 0.0f;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = IMYSConfig.FPS;
            Plugin.Global.Log.LogInfo("FPS changed. Reset to: " + IMYSConfig.FPS);
        }

        // prefab 反序列化不走 set_sprite / SetText，周期补齐
        _lastSweepTime += Time.deltaTime;
        if (_lastSweepTime >= 3.0f)
        {
            _lastSweepTime = 0.0f;
            UiPatches.SweepText();
        }
        // 图片与文字扫描错开半个周期，避免两种批量替换叠在同一帧。
        _lastImageSweepTime += Time.deltaTime;
        if (_lastImageSweepTime >= 3.0f)
        {
            _lastImageSweepTime = 0.0f;
            if (UiImages.ReplacementCount > 0) UiImages.Sweep();
        }

#if DEBUG
        _lastProbeTime += Time.deltaTime;
        if (_lastProbeTime >= 5.0f)
        {
            _lastProbeTime = 0.0f;
            DebugProbe.Summary();
        }
#endif
    }
}

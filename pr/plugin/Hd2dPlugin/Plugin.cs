using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace Hd2d
{
    // HD-2D look for FF6 Pixel Remaster: tilt-shift depth of field on the field and world
    // map, a blurred and hazed battle backdrop behind sharp characters, bloom, dust, colour
    // grade and vignette. Works with vanilla and with any Magicite mod; menus, text and the
    // HUD are left sharp.
    [BepInPlugin(Guid, "HD-2D (Pixel Remaster)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "ff6mod.hd2d";
        public const string Version = "0.4.2";
        internal static ManualLogSource L;

        public override void Load()
        {
            L = Log;
            Settings.Bind(Config);
            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var bundle = Path.Combine(dir, "hd2d.bundle");
            if (!Hd2dRenderer.LoadShader(bundle)) return;
            Hd2dRenderer.Install();
            AddComponent<Hd2dRunner>();
            L.LogInfo($"HD-2D {Version} loaded: {(Settings.Enabled ? "on" : "off")}, " +
                      $"{Settings.Toggle} toggles, {Settings.Reload} reloads the config");
        }
    }

    static class Settings
    {
        static ConfigFile cfg;
        static ConfigEntry<bool> enabled, sharp, debugBlur;
        static ConfigEntry<string> toggle, reload;
        static ConfigEntry<int> outputHeight, defaultsVersion;
        static ConfigEntry<float> selfTest;
        static readonly ProfileBinding[] bindings = new ProfileBinding[4];

        internal static bool Enabled;
        internal static bool SharpUpscale, DebugBlur;
        internal static int OutputHeight;
        internal static float SelfTestSeconds;
        internal static KeyCode Toggle = KeyCode.F7, Reload = KeyCode.F6;
        internal static readonly Profile[] Profiles = new Profile[4];  // by Mode

        internal static void Bind(ConfigFile c)
        {
            cfg = c;
            enabled = c.Bind("General", "Enabled", true, "Turn the HD-2D look on at startup (the toggle key switches it while playing).");
            toggle = c.Bind("General", "ToggleKey", "F7", "Key that turns the look on and off (a Unity KeyCode name). On the Steam Deck, map a back button to it in Steam Input.");
            reload = c.Bind("General", "ReloadKey", "F6", "Key that re-reads this file, so values can be tuned while playing.");
            sharp = c.Bind("General", "SharpUpscale", true, "Upscale the pixel art crisply (sharp bilinear). Off: plain bilinear, softer.");
            outputHeight = c.Bind("General", "OutputHeight", 0,
                "Height in pixels the image is processed at. 0 = match the screen (recommended); lower is faster on weak GPUs.");
            debugBlur = c.Bind("General", "ShowBlur", false,
                "Tuning aid: tint blurred areas red (stronger = blurrier) and draw the focus line in yellow.");
            defaultsVersion = c.Bind("General", "DefaultsVersion", 0,
                "Which version of the plugin's defaults this file has seen. On an update, values still at an old " +
                "default move to the new one; values you changed are kept. Leave it alone.");
            selfTest = c.Bind("Testing", "SelfTestQuitSeconds", 0f,
                "Developer check, leave at 0. Above 0: run the effect once on whatever the game shows, log the result, " +
                "and quit the game after this many seconds.");
            ProfileBinding.Warn = m => Plugin.L.LogWarning(m);
            foreach (var m in new[] { Mode.Field, Mode.WorldMap, Mode.Battle })
                bindings[(int)m] = new ProfileBinding(c, m.ToString(), m, Defaults.For(m));
            Upgrade(c);
            Read();
        }

        static void Upgrade(ConfigFile c)
        {
            var list = new List<(Mode, ProfileBinding)>();
            foreach (var m in new[] { Mode.Field, Mode.WorldMap, Mode.Battle }) list.Add((m, bindings[(int)m]));
            var (moved, retired) = ConfigUpgrade.Run(c, list, defaultsVersion);
            if (moved + retired > 0)
                Plugin.L.LogInfo($"settings: {moved} untouched values moved to the {Plugin.Version} defaults, {retired} retired keys removed");
        }

        internal static void ReloadFile()
        {
            cfg.Reload();
            Read();
        }

        static void Read()
        {
            Enabled = enabled.Value;
            SharpUpscale = sharp.Value;
            DebugBlur = debugBlur.Value;
            OutputHeight = Math.Max(0, outputHeight.Value);
            SelfTestSeconds = selfTest.Value;
            Toggle = Key(toggle.Value, KeyCode.F7);
            Reload = Key(reload.Value, KeyCode.F6);
            for (int i = 1; i < bindings.Length; i++) Profiles[i] = bindings[i].Read();
        }

        static KeyCode Key(string name, KeyCode fallback)
        {
            if (Enum.TryParse(name?.Trim(), true, out KeyCode k)) return k;
            Plugin.L.LogWarning($"unknown key '{name}', using {fallback}");
            return fallback;
        }
    }
}

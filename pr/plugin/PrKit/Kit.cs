using System;
using BepInEx.Logging;

namespace PrKit
{
    // PrKit: what every Pixel Remaster plugin in this project needs (pr/MODDING.md is the guide).
    //
    //   Kit         the log; Kit.Init(Log, "My Mod") first thing in the plugin's Load()
    //   Game        which PR is running (FF1..FF6), its folders; Game.Require(PrGame.FF5)
    //   Hooks       safe Harmony patching (shared-code refusal, type guards, warn-once)
    //   Feature     one switchable change: config entry + hooks + per-frame work + a self-check
    //   Master, GameText  the game's master data and message text
    //   TestRun     headless test runs: requests, no saves, no sound; Harness: commands over TCP
    //   MagiciteDir where Magicite loads mods from; LiveData: apply a Magicite folder without a restart
    //   SingleGamepad  one controller = one input (Steam Deck / Proton double presses)
    //
    // The kit is source, compiled into each plugin (PrKit.props adds plugin/PrKit/*.cs), so plugins
    // never share its state or its version: mods stay independent of each other. Nothing here is
    // registered with IL2CPP (no injected classes), so any number of kit plugins load side by side.
    public static class Kit
    {
        public const string Version = "1.0.0";
        public static ManualLogSource Log { get; private set; }
        public static string ModName { get; private set; } = "mod";

        public static void Init(ManualLogSource log, string modName)
        {
            Log = log;
            ModName = modName ?? "mod";
            Info($"[kit] PrKit {Version} in {ModName}; game: {Game.Describe()}");
        }

        public static void Info(string text) => Log?.LogInfo(text);
        public static void Warn(string text) => Log?.LogWarning(text);
        public static void Error(string text) => Log?.LogError(text);
    }
}

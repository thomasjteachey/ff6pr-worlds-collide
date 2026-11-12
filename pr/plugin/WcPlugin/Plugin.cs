using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Ff6Mod.Shared;
using HarmonyLib;
using UnityEngine;

namespace WorldsCollide.PR
{
    // Worlds Collide for the Pixel Remaster: make and play WC seeds inside the game, with the
    // website's options, no restart (WcScreen, WcGenerator, WcSeeds). A mod of its own: Brave
    // New World is a separate mod, never installed beside it (tools/deploy.sh --wc / --bnw).
    [BepInPlugin(Guid, "Worlds Collide (Pixel Remaster)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "ff6mod.worldscollide";
        public const string Version = "0.6.0";
        internal static ManualLogSource L;
        internal static string Folder;
        internal static bool WcMode;

        public override void Load()
        {
            L = Log;
            var loadClock = System.Diagnostics.Stopwatch.StartNew();
            var lapClock = System.Diagnostics.Stopwatch.StartNew();
            var laps = new System.Text.StringBuilder();
            void Lap(string name)   // startup time, one line at the end ([wc] load: ...)
            {
                laps.Append(laps.Length > 0 ? ", " : "").Append(name).Append(' ').Append(lapClock.ElapsedMilliseconds);
                lapClock.Restart();
            }
            Folder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            LiveData.Info = s => L.LogInfo(s);
            LiveData.Warn = s => L.LogWarning(s);
            WcGenerator.PythonSetting = Config.Bind("Generator", "Python", "",
                "python.exe for the seed generator. Empty: the bundled one (generator/python), else ProjectDir's pr/.venv.").Value.Trim();
            WcGenerator.ScriptSetting = Config.Bind("Generator", "Script", "",
                "wc_seed.py. Empty: the bundled one (generator/tools), else ProjectDir's pr/tools.").Value.Trim();
            WcGenerator.ProjectDir = Config.Bind("Generator", "ProjectDir", "",
                "The ff6-mod project folder, for a development PC without the bundle.").Value.Trim();
            WcSeeds.Keep = Config.Bind("Seeds", "Keep", 30, "How many generated seeds to keep (seeds that saves use are always kept).").Value;
            PadFix.Enabled = Config.Bind("Input", "SingleGamepad", true,
                "When the game sees the same controller as more than one gamepad (Steam Deck / Proton: every press counted twice), " +
                "keep one and disable the others. Every mod mode.").Value;
            WcEvents.SkipStory = Config.Bind("Story", "SkipDialogue", true,
                "In a Worlds Collide game, skip the story's lines in event scenes, as WC does (townsfolk, menus' questions, " +
                "rewards and WC's own messages stay).").Value;
            InputDiag.Enabled = Config.Bind("Input", "LogPresses", false,
                "Log controller/keyboard presses frame by frame ([press] lines, at most 600 a session) to track down double presses.").Value;

            // Worlds Collide's data: its Magicite folder in Magicite's import directory (a separate
            // mod from Brave New World, installed on its own: tools/deploy.sh --wc)
            Lap("config");
            WcGameData.EnsureSetUp();    // a player's first start: the game's files it works from, made from this game
            Lap("game data");
            WcMode = MagiciteDir.Has("WorldsCollide") && WcGameData.Error == null;
            if (WcMode) L.LogInfo($"Worlds Collide's data: {MagiciteDir.FolderPath("WorldsCollide")}");
            else L.LogWarning($"Worlds Collide's data isn't installed (no WorldsCollide folder in {MagiciteDir.ImportDir}): seeds are off");
            WcFlagSet defs;
            try
            {
                defs = WcFlagSet.Load(File.ReadAllText(Path.Combine(Folder, "wc_flags.json")));
            }
            catch (Exception e)
            {
                L.LogError($"can't read wc_flags.json: {e.Message}; the Worlds Collide screen is off");
                return;
            }
            Lap("flags");
            var harmony = new Harmony(Guid);
            WcInput.Patch(harmony);
            InputDiag.Patch(harmony);
            Lap("input");
            WcSeeds.Init();
            Lap("seeds");
            WcSeeds.Patch(harmony);
            Lap("seed hooks");
            // The rest waits for the game's first frames (WcRunner, one part a frame), so the console
            // before the game window is shorter (the user, 2026-11-12: "the command prompt before the
            // game is the thing taking forever"): the screen's setup (its first touch loads Unity's
            // GUI types, ~1.2 s) and the gameplay hooks. Nothing they hook runs before the title,
            // seconds later; WcRunner's Update and OnGUI wait until they're all in.
            Defer("screen", () => WcScreen.Init(defs));
            Defer("title menu", () => WcTitleMenu.Patch(harmony));
            Defer("events", () => WcEvents.Patch(harmony));
            Defer("rewards", () => WcRewards.Patch(harmony));
            Defer("steal", () => WcSteal.Patch(harmony));
            Defer("scaling", () => WcScaling.Patch(harmony));
            Defer("track", () => WcTrack.Patch(harmony));
            // the option hooks: one that fails (a game update, an ambiguous method) only turns its own
            // options off, not the whole plugin
            foreach (var (name, patch) in new (string, Action<Harmony>)[]
            {
                ("coliseum", WcColiseum.Patch), ("battles", WcBattles.Patch), ("espers", WcEspers.Patch),
                ("recruits", WcRecruits.Patch), ("rages", WcRages.Patch), ("items", WcItems.Patch), ("challenges", WcChallenges.Patch),
                ("achievements", WcAchievements.Patch), ("magitek", WcMagitek.Patch), ("veldt", WcVeldt.Patch),
                ("autobattle", WcAutobattle.Patch), ("ability scaling", WcAbilityScaling.Patch), ("atb", WcAtb.Patch), ("status ticks", WcStatusTicks.Patch), ("encounters", WcEncounters.Patch), ("entrances", WcEntrances.Patch), ("enemy ai", WcEnemyAi.Patch),
            })
                Defer(name, () => patch(harmony), $"{name} hooks failed, those options are off");
            WcTest.Init();
            LiveData.InitSelfTest();     // only when BepInEx/live_apply.request exists
            Lap("tests");
            var (py, script, err) = WcGenerator.Resolve();
            L.LogInfo($"Worlds Collide {defs.WcVersion}: {defs.Flags.Count} flags, {defs.Presets.Count} presets; generator: " +
                      (err ?? $"{py} {script}"));
            AddComponent<WcRunner>();
            Lap("runner");
            L.LogInfo($"[wc] load: {loadClock.Elapsed.TotalSeconds:0.00} s (ms: {laps}); {Deferred.Count} parts wait for the first frames");
        }

        // the start's later parts, one a frame from the first (WcRunner.Update)
        static readonly System.Collections.Generic.Queue<(string Name, Action Run, string OnError)> Deferred =
            new System.Collections.Generic.Queue<(string, Action, string)>();
        static readonly System.Text.StringBuilder deferredLaps = new System.Text.StringBuilder();
        static double deferredMs;
        static int deferredFrames;
        internal static bool Started => Deferred.Count == 0;

        static void Defer(string name, Action run, string onError = null) => Deferred.Enqueue((name, run, onError ?? $"{name} failed"));

        internal static void RunDeferred()
        {
            if (Deferred.Count == 0) return;
            var (name, run, onError) = Deferred.Dequeue();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { run(); }
            catch (Exception e) { L.LogError($"[wc] {onError}: {e.GetType().Name}: {e.Message}"); }
            deferredMs += sw.Elapsed.TotalMilliseconds;
            deferredFrames++;
            deferredLaps.Append(deferredLaps.Length > 0 ? ", " : "").Append(name).Append(' ').Append((long)sw.Elapsed.TotalMilliseconds);
            if (Deferred.Count == 0)
                L.LogInfo($"[wc] first frames: {deferredMs / 1000:0.00} s over {deferredFrames} frames (ms: {deferredLaps})");
        }
    }

    public class WcRunner : MonoBehaviour
    {
        public WcRunner(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            if (!Plugin.Started)
            {
                Plugin.RunDeferred();     // the start's later parts first (Plugin.Load), one a frame
                return;
            }
            try
            {
                long t = WcPerf.Now();
                PadFix.Update(); t = WcPerf.Lap(0, "padfix", t);
                InputDiag.Update(); t = WcPerf.Lap(1, "inputlog", t);
                WcSeeds.Update(); t = WcPerf.Lap(2, "seeds", t);
                WcScreen.Poll(); t = WcPerf.Lap(3, "screen-poll", t);
                WcObjectives.Poll(); t = WcPerf.Lap(4, "poll", t);
                WcScreen.Update(); t = WcPerf.Lap(5, "screen", t);
                WcScaling.Update(); t = WcPerf.Lap(6, "scaling", t);
                WcEnemyAi.Update(); t = WcPerf.Lap(11, "enemy-ai", t);
                WcStatusTicks.Track();
                WcAutobattle.Watch();
                WcTest.Update(); WcPerf.Lap(7, "test", t);
                LiveData.UpdateSelfTest();
                WcPerf.Frame();
            }
            catch (Exception e) { WarnOnce("update", e); }
        }

        void OnGUI()
        {
            if (!Plugin.Started) return;   // the screen isn't set up yet
            long t = WcPerf.Now();
            try { WcScreen.Draw(); }
            catch (Exception e) { WarnOnce("draw", e); }
            finally { WcPerf.Lap(8, "gui", t); }
        }

        static readonly System.Collections.Generic.HashSet<string> warned = new System.Collections.Generic.HashSet<string>();

        internal static void WarnOnce(string key, Exception e)
        {
            if (warned.Add(key + e.GetType().Name)) Plugin.L.LogWarning($"[wc] {key}: {e}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace PrKit
{
    // Headless test runs: the game started with no window and no sound by tools/kit.py run, never
    // while someone plays, never writing a save. The kit's request is BepInEx/prkit_test.request:
    //   harness=1 [port=47811]   the command harness (Harness.cs; client tools/game_harness.py)
    //   minutes=N                quit after N minutes (default 30)
    //   anything else            for the mod: TestRun.Settings["mykey"]
    // Every run blocks all saves (slots, autosaves, the bestiary, the config) and mutes the game
    // every frame; if the save hooks can't be installed the run quits instead of risking a save.
    // FeatureHost.ApplyAll starts it and FeatureHost.Update ticks it.
    public static class TestRun
    {
        public const string RequestName = "prkit_test";
        public static bool Running { get; private set; }
        public static Dictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>();
        static float quitAt = -1;

        public static bool Headless
        {
            get
            {
                try { return Application.isBatchMode; }
                catch (Exception) { return false; }
            }
        }

        public static string RequestPath(string name) => System.IO.Path.Combine(Game.BepInExDir, name + ".request");

        // Another request file of the mod's own (read and deleted): null when there is none.
        public static Dictionary<string, string> TakeRequest(string name) => RequestFile.Take(RequestPath(name));

        public static bool StartIfRequested(Harmony h)
        {
            if (Running) return true;
            var req = TakeRequest(RequestName);
            if (req == null) return false;
            Settings = req;
            Kit.Info($"[test] run requested: {string.Join(", ", req.Select(kv => kv.Key + "=" + kv.Value))}");
            if (BlockSaves(h) == 0)
            {
                Kit.Error("[test] saves couldn't be blocked: quitting rather than risk writing one");
                Application.Quit();
                return false;
            }
            Running = true;
            quitAt = Time.realtimeSinceStartup + 60f * Math.Max(1, RequestFile.Int(req, "minutes", 30));
            Mute();
            if (RequestFile.Flag(req, "harness")) Harness.Start(h, RequestFile.Int(req, "port", Harness.DefaultPort));
            return true;
        }

        public static void Update()
        {
            if (!Running) return;
            Mute();
            Harness.Update();
            if (quitAt > 0 && Time.realtimeSinceStartup > quitAt)
            {
                quitAt = -1;
                Quit("the run's time is up");
            }
        }

        public static void Quit(string why)
        {
            Kit.Info($"[test] done: {why}; quitting");
            Application.Quit();
        }

        // --- sound off: the game's own engine (SEAD) and Unity's, every frame (the game sets its
        // volume from the config while loading); the config itself is untouched -----------------------

        public static void Mute()
        {
            try { MuteSead(); }
            catch (Exception) { }
            try { AudioListener.volume = 0f; }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void MuteSead() => SEAD.API.seadSetMasterVolume(0f, 0f);

        // --- no saves -------------------------------------------------------------------------------
        // A test run may load the player's latest save and must never write one: a warp's map change
        // autosaves (BNW's shop check wrote the PC's slot 21 on 2026-11-07), a won battle writes the
        // bestiary. Every SaveSlotManager.Save(slot, ...) overload, a script's AutoSave, and the
        // bestiary's and config's own Save are skipped. Types by name: the same in every PR.

        static int saveHooks = -1;
        public static bool SavesBlocked => saveHooks > 0;

        // The number of save methods hooked (0: the slot saves couldn't be, so nothing is safe).
        public static int BlockSaves(Harmony h)
        {
            if (saveHooks >= 0) return saveHooks;
            int slotHooks = 0, n = 0;
            var slots = AccessTools.TypeByName("Last.Management.SaveSlotManager");
            if (slots != null)
                foreach (var m in slots.GetMethods().Where(m => m.Name == "Save" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(int)))
                {
                    bool withCallback = m.GetParameters().Any(p => p.Name == "callback");
                    if (TryPatch(h, m, withCallback ? nameof(NoSaveCallback) : nameof(NoSave))) slotHooks++;
                }
            n = slotHooks;
            var misc = AccessTools.TypeByName("Last.Interpreter.Instructions.External.Misc");
            var auto = misc == null ? null : AccessTools.Method(misc, "AutoSave");
            if (auto != null && TryPatch(h, auto, nameof(NoAutoSave))) n++;
            foreach (var name in new[] { "Last.Management.PictureBookSaveManager", "Last.Management.SystemConfigManager" })
            {
                var t = AccessTools.TypeByName(name);
                if (t == null) continue;
                foreach (var m in t.GetMethods().Where(m => m.Name == "Save"))
                    if (TryPatch(h, m, m.ReturnType == typeof(bool) ? nameof(NoDataSaveBool) : nameof(NoDataSave))) n++;
            }
            saveHooks = slotHooks == 0 ? 0 : n;
            Kit.Info($"[test] saves blocked for this run ({n} save methods hooked, {slotHooks} of them slot saves)");
            return saveHooks;
        }

        static bool TryPatch(Harmony h, MethodInfo m, string prefix)
        {
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(TestRun), prefix));
                return true;
            }
            catch (Exception e)
            {
                Kit.Warn($"[test] {m.DeclaringType?.Name}.{m.Name} not hooked: {e.Message}");
                return false;
            }
        }

        public static bool NoSave(int slotId)
        {
            Kit.Info($"[test] save to slot {slotId} skipped (test run)");
            return false;
        }

        public static bool NoSaveCallback(int slotId, Il2CppSystem.Action<bool> callback)
        {
            Kit.Info($"[test] save to slot {slotId} skipped (test run), reported done");
            try { callback?.Invoke(true); }
            catch (Exception e) { Kit.Warn($"[test] save callback: {e.Message}"); }
            return false;
        }

        static readonly HashSet<string> dataSaveNoted = new HashSet<string>();

        public static bool NoDataSave(MethodBase __originalMethod)
        {
            lock (dataSaveNoted)
                if (dataSaveNoted.Add(__originalMethod.DeclaringType?.Name))
                    Kit.Info($"[test] {__originalMethod.DeclaringType?.Name}.Save skipped (test run)");
            return false;
        }

        public static bool NoDataSaveBool(MethodBase __originalMethod, ref bool __result)
        {
            NoDataSave(__originalMethod);
            __result = true;
            return false;
        }

        public static bool NoAutoSave(ref int __result)
        {
            Kit.Info("[test] a script's autosave skipped (test run)");
            __result = 0;
            return false;
        }
    }
}

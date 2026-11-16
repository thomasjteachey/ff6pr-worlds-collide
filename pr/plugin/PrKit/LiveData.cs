using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Last.Data.Master;
using Last.Management;
using UnityEngine;

namespace PrKit
{
    // Applies a Magicite-format folder's master rows and text to the running game, no restart (the
    // way Worlds Collide switches seeds in game; from WcPlugin's LiveData, 2026-11):
    //  * master/.../Master/<table>.csv (header + rows): each row rebuilt with the table type's own
    //    CreateMaster(line) (columns by position: the game's column order) and set in
    //    MasterManager.GetList<T>()[id]
    //  * message/.../Message/*_en.txt ("key<TAB>text"): MessageManager's dictionary
    // Rows and texts this process already set to the same value are skipped, so switching between
    // folders only touches what differs. Map scripts and other assets aren't applied (the game reads
    // those when a map loads: Magicite's folder must hold them before the game starts).
    public static class LiveData
    {
        public const string MasterDir = "master/Assets/GameAssets/Serial/Data/Master";
        public const string MessageDir = "message/Assets/GameAssets/Serial/Data/Message";

        static readonly Dictionary<string, Dictionary<int, string>> appliedRows = new Dictionary<string, Dictionary<int, string>>();
        static readonly Dictionary<string, string> appliedText = new Dictionary<string, string>();

        public struct Result
        {
            public int Tables, Rows, Skipped, Failed, Messages;
            public double Seconds;
            public override string ToString() =>
                $"{Rows} rows set in {Tables} tables ({Skipped} unchanged, {Failed} failed), {Messages} texts, {Seconds:0.00} s";
        }

        public static bool Ready => Master.Ready;

        public static Result Apply(string dir)
        {
            var r = new Result();
            var t0 = Time.realtimeSinceStartup;
            var mm = MasterManager.Instance;
            if (mm == null || !mm.isInitialized) { r.Failed = -1; return r; }
            var getListOpen = typeof(MasterManager).GetMethod("GetList", BindingFlags.Public | BindingFlags.Instance);
            var master = Path.Combine(dir, MasterDir);
            if (Directory.Exists(master))
            {
                foreach (var csv in Directory.GetFiles(master, "*.csv"))
                {
                    var table = Path.GetFileNameWithoutExtension(csv);
                    var type = typeof(Monster).Assembly.GetType("Last.Data.Master." + Pascal(table));
                    if (type == null) { Kit.Warn($"[live] {table}: no master type"); r.Failed++; continue; }
                    if (!appliedRows.TryGetValue(table, out var done)) appliedRows[table] = done = new Dictionary<int, string>();
                    try
                    {
                        var live = getListOpen.MakeGenericMethod(type).Invoke(mm, null);
                        var setItem = live.GetType().GetMethod("set_Item");
                        var template = Activator.CreateInstance(type);
                        var create = type.GetMethod("CreateMaster", new[] { typeof(string) });
                        var cast = template.GetType().GetMethod("TryCast").MakeGenericMethod(type);
                        var lines = File.ReadAllLines(csv);
                        for (int i = 1; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            int comma = line.IndexOf(',');
                            if (comma <= 0 || !int.TryParse(line.Substring(0, comma), out int id)) continue;
                            if (done.TryGetValue(id, out var was) && was == line) { r.Skipped++; continue; }
                            var row = cast.Invoke(create.Invoke(template, new object[] { line }), null);
                            if (row == null) { r.Failed++; continue; }
                            setItem.Invoke(live, new[] { (object)id, row });
                            done[id] = line;
                            r.Rows++;
                        }
                        r.Tables++;
                    }
                    catch (Exception e)
                    {
                        r.Failed++;
                        Kit.Warn($"[live] {table}: {(e.InnerException ?? e).Message}");
                    }
                }
            }
            var text = Path.Combine(dir, MessageDir);
            if (Directory.Exists(text) && MessageManager.Instance != null)
            {
                foreach (var file in Directory.GetFiles(text, "*_en.txt"))
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        var key = line.Substring(0, tab);
                        var value = Unescape(line.Substring(tab + 1));
                        if (appliedText.TryGetValue(key, out var was) && was == value) continue;
                        if (GameText.Get(key) != value) GameText.Set(key, value);
                        appliedText[key] = value;
                        r.Messages++;
                    }
                }
            }
            r.Seconds = Time.realtimeSinceStartup - t0;
            return r;
        }

        // What the game's own loader (MessageManager.Parse) does to each text before storing it:
        // the files write line breaks as a literal "\n". Without it a menu text like the magicite
        // swap prompt (MSG_SYSTEM_183) shows the "\n" on screen.
        public static string Unescape(string text) => text.Replace("\\n", Environment.NewLine);

        // --- headless self-test: BepInEx/live_apply.request holds a folder path; the next launch
        // applies it once master data has loaded, logs a few rows before/after, and quits
        // (tools/probe_headless.ps1 -Live <dir>). The plugin calls InitSelfTest() in Load and
        // UpdateSelfTest() every frame.

        static string testDir;
        static bool tested;
        static float since = -1;

        public static void InitSelfTest()
        {
            var req = Path.Combine(Game.BepInExDir, "live_apply.request");
            if (!File.Exists(req)) return;
            testDir = File.ReadAllText(req).Trim();
            try { File.Delete(req); }
            catch (Exception) { }
            Kit.Info($"[live] test requested: {testDir}");
        }

        public static void UpdateSelfTest()
        {
            if (testDir == null || tested || !Ready) return;
            if (since < 0) since = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - since < 3) return;
            tested = true;
            var mm = MasterManager.Instance;
            try
            {
                string Show() => $"monster 1 hp {mm.GetData<Monster>(1)?.Hp} / monster 2 hp {mm.GetData<Monster>(2)?.Hp} / " +
                                 $"item 1 buy {mm.GetData<Item>(1)?.Buy} / character 1 str {mm.GetData<CharacterStatus>(1)?.Strength}";
                Kit.Info($"[live] before: {Show()}");
                Kit.Info($"[live] applied: {Apply(testDir)}");
                Kit.Info($"[live] after:  {Show()}");
            }
            catch (Exception e) { Kit.Warn($"[live] test failed: {e}"); }
            Kit.Info("[live] done; quitting");
            Application.Quit();
        }

        // monster_party -> MonsterParty (the master type's name)
        public static string Pascal(string snake)
        {
            var parts = snake.Split('_');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join("", parts);
        }
    }
}

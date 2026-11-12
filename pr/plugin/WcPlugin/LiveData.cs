using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Last.Data.Master;
using Last.Management;
using UnityEngine;

namespace Ff6Mod.Shared
{
    // Applies a Magicite-format folder's master rows and text to the running game, no restart:
    // the Worlds Collide seeds (tools/wc_seed.py, WcPlugin) and the BNW live-apply test.
    //  * master/.../Master/<table>.csv (header + rows): each row rebuilt with the table type's
    //    own CreateMaster(line) (MasterBase.LineParse: columns by position, our CSVs use the
    //    game's column order) and set in MasterManager.GetList<T>()[id]
    //  * message/.../Message/system_en.txt ("key<TAB>text"): MessageManager's dictionary
    // Rows and texts already set to the same value by an earlier Apply in this process are
    // skipped, so switching between full-table seeds only touches what differs.
    // (MasterBase.Parse<T>(csv) returned nothing for our CSVs; CreateMaster per line works.)
    public static class LiveData
    {
        public const string MasterDir = "master/Assets/GameAssets/Serial/Data/Master";
        public const string MessageDir = "message/Assets/GameAssets/Serial/Data/Message";
        public static Action<string> Info = _ => { }, Warn = _ => { };

        static readonly Dictionary<string, Dictionary<int, string>> appliedRows = new Dictionary<string, Dictionary<int, string>>();
        static readonly Dictionary<string, string> appliedText = new Dictionary<string, string>();

        public struct Result
        {
            public int Tables, Rows, Skipped, Failed, Messages;
            public double Seconds;
            public override string ToString() =>
                $"{Rows} rows set in {Tables} tables ({Skipped} unchanged, {Failed} failed), {Messages} texts, {Seconds:0.00} s";
        }

        // the master tables are there, not only the manager: at a start with a seed already active
        // isInitialized was true while GetList<T>() still returned nothing, and every table of the
        // seed failed ("21 failed": the user played seeds on vanilla data after each restart)
        public static bool Ready
        {
            get
            {
                var mm = MasterManager.Instance;
                if (mm == null || !mm.isInitialized || MessageManager.Instance == null) return false;
                try
                {
                    var monsters = mm.GetList<Monster>();
                    var abilities = mm.GetList<Ability>();
                    return monsters != null && monsters.Count > 0 && abilities != null && abilities.Count > 0;
                }
                catch (Exception) { return false; }
            }
        }

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
                    if (type == null) { Warn($"[live] {table}: no master type"); r.Failed++; continue; }
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
                        Warn($"[live] {table}: {(e.InnerException ?? e).Message}");
                    }
                }
            }
            var msg = MessageManager.Instance;
            var text = Path.Combine(dir, MessageDir);
            if (Directory.Exists(text) && msg != null)
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
                        if (msg.messageDictionary.ContainsKey(key))
                        {
                            if (msg.messageDictionary[key] != value) msg.messageDictionary[key] = value;
                        }
                        else msg.AddMessage(key, value);
                        appliedText[key] = value;
                        r.Messages++;
                    }
                }
            }
            r.Seconds = Time.realtimeSinceStartup - t0;
            return r;
        }

        // What the game's own loader (MessageManager.Parse) does to each text before storing it:
        // the files write line breaks as a literal "\n". Without it every seed text with a line
        // break showed the "\n" on screen once a seed was applied (the magicite swap prompt,
        // MSG_SYSTEM_183, on the Deck 2026-11-12).
        public static string Unescape(string text) => text.Replace("\\n", Environment.NewLine);

        static string Pascal(string snake)
        {
            var parts = snake.Split('_');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join("", parts);
        }

        // --- headless self-test: BepInEx/live_apply.request holds a folder path; the next launch
        // applies it once master data has loaded, logs a few rows before/after, and quits.

        static string testDir;
        static bool tested;
        static float since = -1;

        public static void InitSelfTest()
        {
            var req = Path.Combine(BepInEx.Paths.BepInExRootPath, "live_apply.request");
            if (!File.Exists(req)) return;
            testDir = File.ReadAllText(req).Trim();
            try { File.Delete(req); } catch { }
            Info($"[live] test requested: {testDir}");
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
                Info($"[live] before: {Show()}");
                Info($"[live] applied: {Apply(testDir)}");
                Info($"[live] after:  {Show()}");
            }
            catch (Exception e) { Warn($"[live] test failed: {e}"); }
            Info("[live] done; quitting");
            Application.Quit();
        }
    }
}

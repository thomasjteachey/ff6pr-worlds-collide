using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ff6Mod.Shared;
using HarmonyLib;
using Last.Management;

namespace WorldsCollide.PR
{
    // The seeds made in game (plugin folder seeds/<id>/, see tools/wc_seed.py) and which one
    // the running game has: its master rows and text are set live (LiveData, full tables, so
    // the previous seed is undone), its chest maps / AI scripts copied into the WorldsCollide
    // Magicite folder over the baseline ones (tools/wc_live.py; Magicite reads them each time
    // the game loads one). Each save remembers the seed it was played with: loading it brings
    // that seed back. State: seeds/state.json {active, activeFiles, slots: {slot: id}}.
    static class WcSeeds
    {
        public sealed class Seed
        {
            public string Id, Dir, SeedText, Flags, Preset, Hash, Created, WcVersion;
            public string[] Files = Array.Empty<string>();
            public int Gold;                                      // WC's start: gil, items (PR content id, count), espers (content ids)
            public List<int[]> Items = new List<int[]>();
            public List<int> Espers = new List<int>();
            public List<int> StartCharacters = new List<int>();     // character status ids (Terra 1 .. Umaro 14)
            public HashSet<int> Magitek = new HashSet<int>();         // characters WC dealt MagiTek as a command (WcMagitek)
            public Dictionary<string, Check> Checks = new Dictionary<string, Check>();   // by WC event module
            public List<Objective> Objectives = new List<Objective>();
            public List<(string Name, int Party)> Bosses = new List<(string, int)>();   // WC's boss objectives, PR monster parties
            public List<int> SpellTable = new List<int>();     // "Learn Spells": WC's random order, PR ability ids
            public List<int> ForgetTable = new List<int>();    // "Forget Spells": its own order
            public Dictionary<string, int> Timers = new Dictionary<string, int>();   // -etr: seconds by countdown script
            public Dictionary<int, int[]> Battles = new Dictionary<int, int[]>();
            public List<int> RageTable = new List<int>();
            public int CursedShieldBattles = 256;   // -csb: battles to uncurse (the PR's: 256)
            public int[] Clock = { 2, 0, 4 };          // WC's Zozo clock (-rc): the menus' right options (vanilla 6:10:50)
            public string ClockTime = "6:10:50";
            public Dictionary<int, int> MonsterChests = new Dictionary<int, int>();   // -chrm: chest flag -> party
            public HashSet<string> Boxes = new HashSet<string>();   // the monster-in-a-box scripts the port decides (WcBattles)
            public Dictionary<int, int> LookSources = new Dictionary<int, int>();   // an event battle's copy (20000+) -> its party (WcVeldt)
            // -ccrs: WC's item / gil order for the n-th chest opened, and the chests of each kind (flag ids)
            public int[] ScaledItems = Array.Empty<int>(), ScaledGold = Array.Empty<int>(), ScaledItemFlags = Array.Empty<int>(), ScaledGoldFlags = Array.Empty<int>();
            public Dictionary<int, int[]> EsperEquip = new Dictionary<int, int[]>();   // -eer/-eebr: magicite -> who may equip it
            public Dictionary<int, int> AuctionItems = new Dictionary<int, int>();   // -ari: vanilla sale content -> the seed's
            public Dictionary<string, int[]> CommandWeapons = new Dictionary<string, int[]>();   // runic / bushido: weapon content ids
            public Dictionary<string, List<int>> StartSkills = new Dictionary<string, List<int>>();   // -srr/-slr/-sdr: PR abilities
            public Dictionary<int, int> EsperBonusVanilla = new Dictionary<int, int>();   // magicite content id -> SNES bonus code
            public Dictionary<int, int> EsperBonus = new Dictionary<int, int>();          // -ebs/-ebr: the seed's, where it differs      // "Learn Rages": WC's random order, PR Rage abilities
            public Dictionary<string, int[]> Quests = new Dictionary<string, int[]>();   // battle quests: the event's PR parties   // WC's boss shuffles / -fer: vanilla party -> its parties
            // WC's Coliseum by wagered item content id: (vanilla party, vanilla reward, WC party or 0, WC reward, hidden, WC monster)
            public Dictionary<int, int[]> Coliseum = new Dictionary<int, int[]>();
            public AbilityScaling Scaling;   // -ase / -asr: WC's spell tiers, picked as monsters act (WcAbilityScaling)
            public SnesAiData SnesAi;        // every monster's WC script, run by WcEnemyAi
            public int Rows;
            public double Seconds;
        }

        // the seed's ability_scaling (tools/ai_compile.py plugin_table): placeholder ability Base + k
        // = category k / 3 at tier offset k % 3; Tiers[category][tier] = PR abilities
        public sealed class AbilityScaling
        {
            public int Base, Count;
            public double Factor;
            public bool Random;
            public int[][][] Tiers = Array.Empty<int[][]>();
        }

        // the seed's snes_ai (tools/port_ai.py snes_ai_data): SNES monster id -> its WC AI script (main and
        // counter sections), its Battle / special abilities; SNES attack index -> PR ability; battle lines
        public sealed class SnesAiData
        {
            public Dictionary<int, byte[]> Scripts = new Dictionary<int, byte[]>();
            public Dictionary<int, int> Battle = new Dictionary<int, int>(), Special = new Dictionary<int, int>();
            public int[] Attacks = Array.Empty<int>(), Items = Array.Empty<int>();
            public Dictionary<int, string[]> Messages = new Dictionary<int, string[]>();      // own text (no PR line)
            public Dictionary<int, string[]> MessageKeys = new Dictionary<int, string[]>();   // the PR's own lines
            public HashSet<int> Plugin = new HashSet<int>();   // SNES ids whose turns the plugin runs (the shim)
            public Dictionary<int, int[]> PartySlots = new Dictionary<int, int[]>();   // party -> SNES slot of cells 1-9 (-1 none)
            public Dictionary<int, int> Copies = new Dictionary<int, int>();   // PR monster copy -> the SNES monster it stands for
            // WC's items and commands as PR abilities (port_ai.item_abilities): SNES item -> ability, command -> ability
            public Dictionary<int, int> ItemUse = new Dictionary<int, int>(), Throws = new Dictionary<int, int>(), Commands = new Dictionary<int, int>();
            // party -> each SNES slot's member: {SNES id, cell, shown at start (1/0), the PR party lists it (1/0)}, or null
            public Dictionary<int, int[][]> PartyMembers = new Dictionary<int, int[][]>();
            public Dictionary<int, int> Weak = new Dictionary<int, int>();          // SNES monster -> weak element bits
            public Dictionary<int, int> Formations = new Dictionary<int, int>();    // PR party -> SNES formation
        }

        internal static int Keep = 30;
        static string Root => Path.Combine(Plugin.Folder, "seeds");
        static string StatePath => Path.Combine(Root, "state.json");
        static string ModDir => MagiciteDir.FolderPath("WorldsCollide");
        static string BaselineDir => Path.Combine(Plugin.Folder, "baseline");

        public static Seed Active;
        static string[] activeFiles = Array.Empty<string>();
        static readonly Dictionary<int, string> slots = new Dictionary<int, string>();
        static bool startupApplied;
        static float startupRetryAt;
        static int startupTries;
        internal static int StartupFailed = -1;    // the startup apply's failed tables (-1: not done)
        internal static bool StartupDone => startupApplied;
        public static string LastApply = "";

        public static void Init()
        {
            Directory.CreateDirectory(Root);
            try
            {
                if (File.Exists(StatePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                    var r = doc.RootElement;
                    var id = r.TryGetProperty("active", out var a) ? a.GetString() : null;
                    if (r.TryGetProperty("activeFiles", out var af))
                        activeFiles = af.EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (r.TryGetProperty("slots", out var s))
                        foreach (var p in s.EnumerateObject())
                            if (int.TryParse(p.Name, out int slot)) slots[slot] = p.Value.GetString();
                    if (!string.IsNullOrEmpty(id)) Active = Load(id);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] {StatePath}: {e.Message}"); }
            Plugin.L.LogInfo($"[wc] {All().Count} seeds; active: {(Active == null ? "none (vanilla data)" : Describe(Active))}");
            if (Plugin.WcMode) SyncFiles();
        }

        // Make the Magicite folder's files the active seed's (baseline where it changes none):
        // deploy mirrors the baseline over the folder, and nothing reads these files before
        // the game loads a map or a battle, long after this.
        static void SyncFiles()
        {
            try
            {
                var basefiles = Path.Combine(BaselineDir, "files");
                if (!Directory.Exists(basefiles)) { Plugin.L.LogWarning($"[wc] no baseline at {basefiles}"); return; }
                var mine = new HashSet<string>(Active?.Files ?? Array.Empty<string>());
                int copied = 0, total = 0;
                foreach (var full in Directory.GetFiles(basefiles, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(basefiles, full).Replace(Path.DirectorySeparatorChar, '/');
                    var src = mine.Contains(rel) ? Path.Combine(Active.Dir, "files", rel) : full;
                    var dst = Path.Combine(ModDir, rel);
                    total++;
                    if (File.Exists(dst) && Same(src, dst)) continue;
                    Copy(src, dst);
                    copied++;
                }
                activeFiles = mine.ToArray();
                Plugin.L.LogInfo($"[wc] Magicite files: {copied} of {total} brought in line with {(Active == null ? "the baseline" : Describe(Active))}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] file sync: {e.Message}"); }
        }

        static bool Same(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        // one WC event's choices: rewards ("character" status id / "esper" / "item" content id)
        public sealed class Check
        {
            public string Event, Module;
            public int Gate;                                  // gating character status id, 0 = none
            public List<(string Kind, int Id)> Rewards = new List<(string, int)>();
            public int[] Prices = Array.Empty<int>();
        }

        // one of WC's objectives: its result once `Required` of its conditions are met
        public sealed class Objective
        {
            public string Letter, Result, ResultName;
            public int Required;
            public int[] IntArgs = Array.Empty<int>();            // result_args (strings as 0): count, ..., character index
            public List<int[]> Items = new List<int[]>();         // [PR content id, count] the result gives
            public List<Condition> Conditions = new List<Condition>();
        }

        // Type: Character (Status), Characters (Count), Esper (Content), Espers, Check (Name),
        // Checks, Dragon (Name), Dragons, Boss, Bosses, Quest (Name); Text as WC words it
        public sealed class Condition
        {
            public string Type, Text, Name = "";
            public int Status, Content, Count;
        }

        public static string Describe(Seed s) => $"{s.SeedText} ({s.Hash})";

        public static Seed Load(string id)
        {
            var dir = Path.Combine(Root, id);
            var json = Path.Combine(dir, "wc_seed.json");
            if (!File.Exists(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                var s = new Seed
                {
                    Id = id, Dir = dir, SeedText = S("seed"), Flags = S("flags"), Preset = S("preset"), Hash = S("hash"),
                    Created = S("created"), WcVersion = S("wc_version"),
                };
                if (r.TryGetProperty("files", out var f)) s.Files = f.EnumerateArray().Select(x => x.GetString()).ToArray();
                if (r.TryGetProperty("tables", out var t)) s.Rows = t.EnumerateObject().Sum(p => p.Value.GetInt32());
                if (r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number) s.Seconds = sec.GetDouble();
                if (r.TryGetProperty("start", out var st))
                {
                    if (st.TryGetProperty("gold", out var g)) s.Gold = g.GetInt32();
                    if (st.TryGetProperty("items", out var it))
                        foreach (var x in it.EnumerateArray()) s.Items.Add(new[] { x[0].GetInt32(), x[1].GetInt32() });
                    if (st.TryGetProperty("espers", out var es))
                        foreach (var x in es.EnumerateArray()) s.Espers.Add(x.GetInt32());
                    if (st.TryGetProperty("characters", out var ch))
                        foreach (var x in ch.EnumerateArray()) s.StartCharacters.Add(x.GetInt32());
                }
                if (r.TryGetProperty("magitek", out var mt))
                    foreach (var x in mt.EnumerateArray()) s.Magitek.Add(x.GetInt32());
                if (r.TryGetProperty("checks", out var cs))
                    foreach (var c in cs.EnumerateArray())
                    {
                        var k = new Check { Event = c.GetProperty("event").GetString(), Module = c.GetProperty("module").GetString() };
                        if (c.TryGetProperty("gate", out var g) && g.ValueKind == JsonValueKind.Number) k.Gate = g.GetInt32();
                        foreach (var rw in c.GetProperty("rewards").EnumerateArray())
                            k.Rewards.Add((rw[0].GetString(), rw[1].GetInt32()));
                        if (c.TryGetProperty("prices", out var pr)) k.Prices = pr.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        s.Checks[k.Module] = k;
                    }
                if (r.TryGetProperty("coliseum", out var col))
                    foreach (var x in col.EnumerateArray())
                        s.Coliseum[x[0].GetInt32()] = new[] { x[1].GetInt32(), x[2].GetInt32(), x[3].GetInt32(), x[4].GetInt32(), x[5].GetInt32(),
                                                              x.GetArrayLength() > 6 ? x[6].GetInt32() : 0 };
                if (r.TryGetProperty("esper_bonus", out var eb))
                {
                    foreach (var x in eb.GetProperty("vanilla").EnumerateArray()) s.EsperBonusVanilla[x[0].GetInt32()] = x[1].GetInt32();
                    foreach (var x in eb.GetProperty("seed").EnumerateArray()) s.EsperBonus[x[0].GetInt32()] = x[1].GetInt32();
                }
                if (r.TryGetProperty("start_skills", out var ssk))
                    foreach (var p in ssk.EnumerateObject()) s.StartSkills[p.Name] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToList();
                if (r.TryGetProperty("command_weapons", out var cwp))
                    foreach (var p in cwp.EnumerateObject())
                        s.CommandWeapons[p.Name] = p.Value.GetProperty("seed").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (r.TryGetProperty("auction_items", out var aui))
                    foreach (var p in aui.EnumerateObject()) s.AuctionItems[int.Parse(p.Name)] = p.Value.GetInt32();
                if (r.TryGetProperty("esper_equip", out var eeq))
                    foreach (var p in eeq.EnumerateObject())
                        s.EsperEquip[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (r.TryGetProperty("cursed_shield_battles", out var csb)) s.CursedShieldBattles = csb.GetInt32();
                if (r.TryGetProperty("clock", out var clk))
                {
                    s.Clock = clk.GetProperty("answer").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    s.ClockTime = clk.GetProperty("time").GetString();
                }
                if (r.TryGetProperty("look_sources", out var lks))
                    foreach (var p in lks.EnumerateObject()) s.LookSources[int.Parse(p.Name)] = p.Value.GetInt32();
                if (r.TryGetProperty("boxes", out var bxs))
                    foreach (var x in bxs.EnumerateArray()) s.Boxes.Add(x.GetString());
                if (r.TryGetProperty("monster_chests", out var mch))
                    foreach (var p in mch.EnumerateObject()) s.MonsterChests[int.Parse(p.Name)] = p.Value.GetInt32();
                if (r.TryGetProperty("scaled_chests", out var scc))
                {
                    int[] Ints(string k) => scc.GetProperty(k).EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    s.ScaledItems = Ints("items"); s.ScaledGold = Ints("gold");
                    s.ScaledItemFlags = Ints("item_flags"); s.ScaledGoldFlags = Ints("gold_flags");
                }
                if (r.TryGetProperty("rage_table", out var rtb))
                    foreach (var x in rtb.EnumerateArray()) s.RageTable.Add(x.GetInt32());
                if (r.TryGetProperty("quests", out var qts))
                    foreach (var p in qts.EnumerateObject())
                        s.Quests[p.Name] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (r.TryGetProperty("battles", out var bts))
                    foreach (var p in bts.EnumerateObject())
                        s.Battles[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (r.TryGetProperty("timers", out var tms))
                    foreach (var p in tms.EnumerateObject()) s.Timers[p.Name] = p.Value.GetInt32();
                if (r.TryGetProperty("forget_table", out var ftb))
                    foreach (var x in ftb.EnumerateArray()) s.ForgetTable.Add(x.GetInt32());
                if (r.TryGetProperty("spell_table", out var stb))
                    foreach (var x in stb.EnumerateArray()) s.SpellTable.Add(x.GetInt32());
                if (r.TryGetProperty("ability_scaling", out var asc) && asc.ValueKind == JsonValueKind.Object)
                    s.Scaling = new AbilityScaling
                    {
                        Base = asc.GetProperty("base").GetInt32(),
                        Count = asc.GetProperty("count").GetInt32(),
                        Factor = asc.GetProperty("factor").GetDouble(),
                        Random = asc.TryGetProperty("random", out var rnd) && rnd.ValueKind == JsonValueKind.True,
                        Tiers = asc.GetProperty("tiers").EnumerateArray()
                            .Select(c => c.EnumerateArray().Select(t => t.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray())
                            .ToArray(),
                    };
                if (r.TryGetProperty("snes_ai", out var sai) && sai.ValueKind == JsonValueKind.Object)
                {
                    var d = new SnesAiData();
                    static byte[] Hex(string h)
                    {
                        var bytes = new byte[h.Length / 2];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
                        return bytes;
                    }
                    foreach (var p in sai.GetProperty("scripts").EnumerateObject()) d.Scripts[int.Parse(p.Name)] = Hex(p.Value.GetString() ?? "");
                    foreach (var p in sai.GetProperty("battle").EnumerateObject()) d.Battle[int.Parse(p.Name)] = p.Value.GetInt32();
                    foreach (var p in sai.GetProperty("special").EnumerateObject()) d.Special[int.Parse(p.Name)] = p.Value.GetInt32();
                    d.Attacks = sai.GetProperty("attacks").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    if (sai.TryGetProperty("items", out var its2)) d.Items = its2.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    if (sai.TryGetProperty("weak", out var wk))
                        foreach (var p in wk.EnumerateObject()) d.Weak[int.Parse(p.Name)] = p.Value.GetInt32();
                    if (sai.TryGetProperty("formations", out var fm))
                        foreach (var p in fm.EnumerateObject()) d.Formations[int.Parse(p.Name)] = p.Value.GetInt32();
                    if (sai.TryGetProperty("party_members", out var pm))
                        foreach (var p in pm.EnumerateObject())
                            d.PartyMembers[int.Parse(p.Name)] = p.Value.EnumerateArray()
                                .Select(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(v => v.GetInt32()).ToArray() : null).ToArray();
                    foreach (var (key, map) in new[] { ("item_use", d.ItemUse), ("throws", d.Throws), ("commands", d.Commands) })
                        if (sai.TryGetProperty(key, out var im))
                            foreach (var p in im.EnumerateObject()) map[int.Parse(p.Name)] = p.Value.GetInt32();
                    if (sai.TryGetProperty("copies", out var cp))
                        foreach (var p in cp.EnumerateObject()) d.Copies[int.Parse(p.Name)] = p.Value.GetInt32();
                    if (sai.TryGetProperty("party_slots", out var ps))
                        foreach (var p in ps.EnumerateObject()) d.PartySlots[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    if (sai.TryGetProperty("plugin", out var pl))
                        foreach (var x in pl.EnumerateArray()) d.Plugin.Add(x.GetInt32());
                    if (sai.TryGetProperty("message_keys", out var mk))
                        foreach (var p in mk.EnumerateObject()) d.MessageKeys[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    if (sai.TryGetProperty("messages", out var msg))
                        foreach (var p in msg.EnumerateObject()) d.Messages[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    s.SnesAi = d;
                }
                if (r.TryGetProperty("bosses", out var bs))
                    foreach (var b in bs.EnumerateArray()) s.Bosses.Add((b.GetProperty("name").GetString(), b.GetProperty("party").GetInt32()));
                if (r.TryGetProperty("objectives", out var objs))
                    foreach (var o in objs.EnumerateArray())
                    {
                        string O(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                        int N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                        var ob = new Objective { Letter = O(o, "letter"), Result = O(o, "result"), ResultName = O(o, "result_name"), Required = N(o, "required") };
                        if (o.TryGetProperty("result_args", out var ra))
                            ob.IntArgs = ra.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToArray();
                        if (o.TryGetProperty("items", out var its))
                            foreach (var x in its.EnumerateArray()) ob.Items.Add(new[] { x[0].GetInt32(), x[1].GetInt32() });
                        foreach (var c in o.GetProperty("conditions").EnumerateArray())
                            ob.Conditions.Add(new Condition { Type = O(c, "type"), Text = O(c, "text"), Name = O(c, "name"),
                                                              Status = N(c, "status"), Content = N(c, "content"), Count = N(c, "count") });
                        s.Objectives.Add(ob);
                    }
                return s;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[wc] {json}: {e.Message}");
                return null;
            }
        }

        // newest first
        public static List<Seed> All()
        {
            if (!Directory.Exists(Root)) return new List<Seed>();
            return Directory.GetDirectories(Root)
                .Where(d => !d.EndsWith(".work"))
                .Select(d => Load(Path.GetFileName(d))).Where(s => s != null)
                .OrderByDescending(s => s.Id, StringComparer.Ordinal).ToList();
        }

        public static string NewId(string seed)
        {
            var clean = new string(seed.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(24).ToArray());
            return $"{DateTime.Now:yyyyMMdd-HHmmss}-{(clean.Length > 0 ? clean : "seed")}";
        }

        public static string DirFor(string id) => Path.Combine(Root, id);

        // --- applying ---------------------------------------------------------------------

        // Make `s` the running game's seed (null: vanilla data where a seed had changed it).
        public static string Apply(Seed s)
        {
            if (!LiveData.Ready) return "the game's data isn't loaded yet";
            try
            {
                var files = SwapFiles(s);
                string data;
                if (s != null) data = LiveData.Apply(s.Dir).ToString();
                else
                {
                    // vanilla: every managed table/text back to the baseline copies
                    var vanilla = Path.Combine(BaselineDir, "vanilla_data");
                    data = Directory.Exists(vanilla) ? LiveData.Apply(vanilla).ToString() : "no vanilla tables to restore";
                }
                int evicted = Evict(files);
                Active = s;
                activeFiles = s?.Files ?? Array.Empty<string>();
                Save();
                LastApply = $"{(s == null ? "vanilla" : Describe(s))}: {data}; {files.Count} files, {evicted} cached assets dropped";
                Plugin.L.LogInfo("[wc] applied " + LastApply);
                return null;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"[wc] apply failed: {e}");
                return e.Message;
            }
        }

        // copy the seed's chest maps / AI scripts into the Magicite folder, and the baseline
        // back for files the previous seed had changed and this one doesn't
        static List<string> SwapFiles(Seed s)
        {
            var touched = new List<string>();
            var want = new HashSet<string>(s?.Files ?? Array.Empty<string>());
            foreach (var rel in activeFiles.Where(f => !want.Contains(f)))
            {
                var src = Path.Combine(BaselineDir, "files", rel);
                if (File.Exists(src)) { Copy(src, Path.Combine(ModDir, rel)); touched.Add(rel); }
                else Plugin.L.LogWarning($"[wc] no baseline copy of {rel}");
            }
            foreach (var rel in want)
            {
                Copy(Path.Combine(s.Dir, "files", rel), Path.Combine(ModDir, rel));
                touched.Add(rel);
            }
            return touched;
        }

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
        }

        // Assets the game already loaded keep their old contents in ResourceManager's cache;
        // drop the entries for files we just replaced so the next load reads the new ones.
        static int Evict(List<string> files)
        {
            if (files.Count == 0) return 0;
            var rm = ResourceManager.Instance;
            var dic = rm?.completeAssetDic;
            if (dic == null) return 0;
            var stems = new HashSet<string>(files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
            var maps = new HashSet<string>(files.Select(f => f.Split('/').Reverse().Skip(1).FirstOrDefault() ?? ""), StringComparer.OrdinalIgnoreCase);
            var drop = new List<string>();
            foreach (var key in dic.Keys)
            {
                var k = key.Replace('\\', '/');
                var last = k.Split('/').Last();
                var parent = k.Split('/').Reverse().Skip(1).FirstOrDefault() ?? "";
                if (last.StartsWith("sc_ai_bnw_") && stems.Contains(last)) drop.Add(key);
                else if (last == "entity_default" && maps.Contains(parent)) drop.Add(key);
            }
            foreach (var key in drop) dic.Remove(key);
            return drop.Count;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Root);
                var state = new Dictionary<string, object>
                {
                    ["active"] = Active?.Id ?? "",
                    ["activeFiles"] = activeFiles,
                    ["slots"] = slots.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                };
                // write a temporary file and swap it in: a crash mid-write (a power loss leaves zeros)
                // keeps the previous state instead of an unreadable one
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] {StatePath}: {e.Message}"); }
        }

        public static void Prune()
        {
            var keep = new HashSet<string>(slots.Values) { Active?.Id ?? "" };
            foreach (var s in All().Skip(Keep).Where(s => !keep.Contains(s.Id)))
            {
                try { Directory.Delete(s.Dir, true); Plugin.L.LogInfo($"[wc] removed old seed {s.Id}"); }
                catch (Exception e) { Plugin.L.LogWarning($"[wc] {s.Dir}: {e.Message}"); }
            }
        }

        // The player's Delete in the seed list (WcScreen): the seed's folder goes. Not the current
        // seed, and not one a save still uses (that save would load without its seed's data).
        // Returns why not, or null once it's gone.
        public static string Delete(Seed s)
        {
            if (s == null || !Directory.Exists(s.Dir)) return "that seed is already gone";
            if (s.Id == Active?.Id) return "it's the current seed: play another seed (or none) first";
            var saves = slots.Where(kv => kv.Value == s.Id && SaveExists(kv.Key)).Select(kv => kv.Key).OrderBy(k => k).ToList();
            if (saves.Count > 0)
                return $"{string.Join(", ", saves.Select(SlotName))} {(saves.Count == 1 ? "uses" : "use")} it: " +
                       "save over that with another seed first";
            try { Directory.Delete(s.Dir, true); }
            catch (Exception e) { return e.Message; }
            Plugin.L.LogInfo($"[wc] deleted seed {s.Id}");
            return null;
        }

        static string SlotName(int slot) => slot == SaveSlotManager.AutoSlotId ? "the autosave"
            : slot == SaveSlotManager.SuspendedSlotId ? "the quick save" : $"save slot {slot}";

        // whether a recorded save is still there (the game's own list; when it can't tell, yes)
        static bool SaveExists(int slot)
        {
            if (testSlots.Contains(slot)) return true;
            try
            {
                var have = SaveSlotManager.Instance()?.ExistenceSaveSlots;
                if (have != null && have.ContainsKey(slot)) return have[slot];
            }
            catch (Exception) { }
            return true;
        }

        // the headless test (WcTest): a save recorded for a seed, in memory only (a slot number
        // the game doesn't have, so no real save's record is touched)
        static readonly HashSet<int> testSlots = new HashSet<int>();
        internal static void TestSlot(int slot, string id)
        {
            if (slot <= SaveSlotManager.LoadMaxSlotCount) throw new ArgumentException($"slot {slot} is a real save slot");
            if (id == null) { slots.Remove(slot); testSlots.Remove(slot); }
            else { slots[slot] = id; testSlots.Add(slot); }
        }

        public static string SlotSeed(int slot) => slots.TryGetValue(slot, out var id) ? id : null;

        // the active seed's tables/text into the game once its data has loaded (its files are
        // already in the Magicite folder from when it was applied)
        public static void Update()
        {
            if (startupApplied || !Plugin.WcMode || !LiveData.Ready) return;
            if (Active == null) { startupApplied = true; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < startupRetryAt) return;
            var r = LiveData.Apply(Active.Dir);   // tables already set are skipped on a retry
            LastApply = $"{Describe(Active)}: {r}";
            if (r.Failed != 0 && ++startupTries < 30)
            {
                // the game still loading its tables: again in 2 s (a minute at most)
                startupRetryAt = now + 2f;
                Plugin.L.LogInfo($"[wc] seed {LastApply} (startup, try {startupTries}: again in 2 s)");
                return;
            }
            startupApplied = true;
            StartupFailed = r.Failed;
            Plugin.L.LogInfo($"[wc] seed {LastApply} (startup{(startupTries > 0 ? $", try {startupTries + 1}" : "")})");
            if (r.Failed != 0) WcScreen.Toast("Couldn't load this seed's data at startup: restart the game, or pick the seed again on the Worlds Collide screen.");
        }

        // --- a New Game gets WC's starting gil, items and espers --------------------------
        // The WC opening script (tools/wc_events.py) calls WC:start, which grants them
        // (WcEvents). Fallback for a Magicite folder without that script: grant them once the
        // first map is up (leaving the title without loading a save = a New Game).
        static bool loadingSave, wasOnTitle, pendingStart;
        static float startSince = -1;

        public static void UpdateNewGame(bool onTitle)
        {
            if (!Plugin.WcMode) return;
            if (onTitle)
            {
                wasOnTitle = true;
                WcEvents.StartRan = false;
                loadingSave = false;
                pendingStart = false;
                startSince = -1;
                return;
            }
            if (wasOnTitle)
            {
                wasOnTitle = false;
                pendingStart = !loadingSave && Active != null && (Active.Gold > 0 || Active.Items.Count > 0 || Active.Espers.Count > 0);
                if (pendingStart) Plugin.L.LogInfo($"[wc] New Game with seed {Describe(Active)}: its starting gil/items/espers come once the first map is up");
            }
            if (!pendingStart) return;
            if (WcEvents.StartRan) { pendingStart = false; return; }
            if (UnityEngine.Object.FindObjectOfType<FieldMap>() == null || UserDataManager.Instance() == null) { startSince = -1; return; }
            if (startSince < 0) startSince = UnityEngine.Time.realtimeSinceStartup;
            if (UnityEngine.Time.realtimeSinceStartup - startSince < 2f) return;
            pendingStart = false;
            GrantStart(Active);
        }

        // setGil: the gil becomes exactly the seed's (a New Game: WC starts with only its gil)
        internal static string GrantStart(Seed s, bool setGil = false)
        {
            try
            {
                var client = new OwnedItemClient();
                var udm = UserDataManager.Instance();
                if (setGil) client.AddGil(s.Gold - udm.OwendGil);
                else if (s.Gold > 0) client.AddGil(s.Gold);
                foreach (var it in s.Items) client.AddOwnedItem(it[0], it[1]);
                int espers = 0;
                foreach (var c in s.Espers)
                {
                    if (udm.OwnedMagicStoneList.Contains(c)) continue;
                    client.AddMagicStone(c);
                    if (!udm.OwnedMagicStoneList.Contains(c)) udm.OwnedMagicStoneList.Add(c);
                    espers++;
                }
                var msg = $"{s.Gold} gil, {s.Items.Sum(i => i[1])} items, {espers} espers";
                Plugin.L.LogInfo($"[wc] starting state of {Describe(s)}: {msg}");
                return msg;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[wc] starting state: {e}");
                return null;
            }
        }

        // --- saves ------------------------------------------------------------------------

        public static void Patch(Harmony h)
        {
            var t = typeof(SaveSlotManager);
            foreach (var m in t.GetMethods().Where(m => m.Name == "Save" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(int)))
            {
                // the overload with a callback (autosaves) must hear "done", or whoever waits on it waits forever
                bool withCallback = m.GetParameters().Any(p => p.Name == "callback");
                h.Patch(m, prefix: new HarmonyMethod(typeof(WcSeeds), withCallback ? nameof(NoSaveInTestCallback) : nameof(NoSaveInTest)),
                        postfix: new HarmonyMethod(typeof(WcSeeds), nameof(Saved)));
            }
            var load = t.GetMethod("GotoLoadSaveData");
            if (load != null) h.Patch(load, prefix: new HarmonyMethod(typeof(WcSeeds), nameof(Loading)));
            var auto = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Misc), "AutoSave");
            if (auto != null) h.Patch(auto, prefix: new HarmonyMethod(typeof(WcSeeds), nameof(AutoSave_Prefix)));
            // the game's own data beside the slots, which test runs mustn't write either: the bestiary
            // (a won battle records its monsters: the WC test battles rewrote the PC's, 2026-11-11)
            // and the config (the BNW probe blocks both since 2026-11-07)
            foreach (var dt in new[] { typeof(PictureBookSaveManager), typeof(SystemConfigManager) })
                foreach (var m in dt.GetMethods().Where(m => m.Name == "Save"))
                    h.Patch(m, prefix: new HarmonyMethod(typeof(WcSeeds), m.ReturnType == typeof(bool) ? nameof(NoDataSaveInTestBool) : nameof(NoDataSaveInTest)));
        }

        public static bool NoDataSaveInTest(System.Reflection.MethodBase __originalMethod)
        {
            if (!WcTest.Running) return true;
            Plugin.L.LogInfo($"[wc test] {__originalMethod.DeclaringType?.Name}.Save skipped (test run)");
            return false;
        }

        public static bool NoDataSaveInTestBool(System.Reflection.MethodBase __originalMethod, ref bool __result)
        {
            if (!WcTest.Running) return true;
            Plugin.L.LogInfo($"[wc test] {__originalMethod.DeclaringType?.Name}.Save skipped (test run)");
            __result = true;
            return false;
        }

        // A script's AutoSave instruction that won't save (a test run, WC's -nosaves) ends at once.
        // Skipping only the save (its callback told "done") left the instruction waiting: the
        // Blackjack's boarding arrival (sc_e_BJinboard_1) saves before it fades in, and stayed black
        static bool AutoSave_Prefix(ref int __result)
        {
            bool test = WcTest.Running, ironMog = !test && IronMog();
            if (!test && !ironMog) return true;
            Plugin.L.LogInfo(test ? "[wc test] a script's autosave skipped (test run)" : "[wc] -nosaves: a script's autosave skipped");
            __result = 0;
            return false;
        }

        // a headless self-test never writes the player's saves (the airship deck autosaves)
        // WC's -nosaves (Ironmog mode) in a WC game: no saving, autosaves included (save points still
        // heal with tents/sleeping bags; the field menu hides Save/Quick Save: WcTrack)
        internal static bool IronMog() =>
            Plugin.WcMode && Active != null && Active.Flags.Split(' ').Contains("-nosaves") &&
            WcEvents.Flag(Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        static bool NoSaveInTest(int slotId)
        {
            if (!WcTest.Running && IronMog()) { Plugin.L.LogInfo($"[wc] -nosaves: save to slot {slotId} refused"); return false; }
            if (!WcTest.Running) return true;
            Plugin.L.LogInfo($"[wc test] save to slot {slotId} skipped (test run)");
            return false;
        }

        static bool NoSaveInTestCallback(int slotId, Il2CppSystem.Action<bool> callback)
        {
            if (!WcTest.Running && IronMog())
            {
                Plugin.L.LogInfo($"[wc] -nosaves: autosave to slot {slotId} skipped");
                try { callback?.Invoke(true); } catch (Exception e) { Plugin.L.LogWarning($"[wc] save callback: {e.Message}"); }
                return false;
            }
            if (!WcTest.Running) return true;
            Plugin.L.LogInfo($"[wc test] save to slot {slotId} skipped (test run), reported done");
            try { callback?.Invoke(true); } catch (Exception e) { Plugin.L.LogWarning($"[wc test] save callback: {e.Message}"); }
            return false;
        }

        static void Saved(int slotId)
        {
            if (WcTest.Running) return;
            try
            {
                // "" = saved in Worlds Collide with no seed: loading it goes back to the game's own data
                if (Plugin.WcMode) slots[slotId] = Active?.Id ?? "";
                else slots.Remove(slotId);
                Save();
                Plugin.L.LogInfo($"[wc] saved slot {slotId}: seed {(Plugin.WcMode && Active != null ? Active.Id : "none")}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] save hook: {e.Message}"); }
        }

        static void Loading(SaveSlotData saveData)
        {
            loadingSave = true;
            try
            {
                if (!Plugin.WcMode || saveData == null) return;
                int slot = saveData.Id;
                var id = SlotSeed(slot);
                Plugin.L.LogInfo($"[wc] loading slot {slot}: its seed {id ?? "(not recorded)"}; active {Active?.Id ?? "none"}");
                if (id == "")
                {
                    if (Active == null) return;
                    var verr = Apply(null);
                    if (verr != null) Plugin.L.LogWarning($"[wc] slot {slot} was saved without a seed; couldn't go back to the game's own data: {verr}");
                    return;
                }
                if (id == null || id == Active?.Id) return;
                var s = Load(id);
                if (s == null) { Plugin.L.LogWarning($"[wc] slot {slot}'s seed {id} is gone; keeping {Active?.Id ?? "vanilla"}"); return; }
                var err = Apply(s);
                if (err != null) Plugin.L.LogWarning($"[wc] couldn't bring back seed {id}: {err}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] load hook: {e.Message}"); }
        }
    }
}

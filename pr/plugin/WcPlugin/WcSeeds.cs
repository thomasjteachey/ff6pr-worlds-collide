using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PrKit;
using PrKit.Seeds;
using HarmonyLib;
using Last.Management;
using PrKit.Ff6;   // SnesAi: the kit's FF6 AI interpreter

namespace WorldsCollide.PR
{
    // The seeds made in game (BepInEx/WorldsCollide/seeds/<id>/, see tools/wc_seed.py) and which one
    // the running game has: the kit's SeedLibrary (PrKit/Seeds; WC's own code until 2026-11-20) sets
    // its master rows and text live (LiveData, full tables, so the previous seed is undone), copies its
    // chest maps / AI scripts into the WorldsCollide Magicite folder over the baseline ones
    // (tools/wc_live.py), and remembers each save's seed (loading it brings that seed back). WC's own
    // here: what a WC seed holds (its json), the dice repair, -nosaves (Ironmog) and a New Game's start.
    static class WcSeeds
    {
        public sealed class Seed : SeedInfo
        {
            public string WcVersion => Version;
            public int Gold;                                      // WC's start: gil, items (PR content id, count), espers (content ids)
            public List<int[]> Items = new List<int[]>();
            public List<int> Espers = new List<int>();
            public List<int> StartCharacters = new List<int>();     // character status ids (Terra 1 .. Umaro 14)
            public HashSet<int> Magitek = new HashSet<int>();         // characters WC dealt MagiTek as a command (WcMagitek)
            public Dictionary<string, Check> Checks = new Dictionary<string, Check>();   // by WC event module
            public List<Objective> Objectives = new List<Objective>();
            public List<(string Name, int Party)> Bosses = new List<(string, int)>();   // WC's boss objectives, PR monster parties
            public List<int> SpellTable = new List<int>();     // "Learn Spells": WC's random order, PR ability ids
            public bool NaturalLv1;                            // its learning rows include a Terra learner's level-1 spell (WcSpells)
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
            public SnesAiData SnesAi;        // every monster's WC script (the kit's SnesAiData), run by WcEnemyAi
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

        internal static readonly SeedLibrary Lib = new SeedLibrary
        {
            Tag = "[wc]", TestTag = "[wc test]", JsonName = "wc_seed.json", RefuseNote = "-nosaves",
            Root = () => WcDataDir.Seeds, BaselineDir = () => WcDataDir.Baseline,
            ModDir = () => MagiciteDir.FolderPath("WorldsCollide"),
            Parse = (id, dir) => Parse(id, dir),
            ModOn = () => Plugin.WcMode,
            TestRunning = () => WcTestState.Running,
            AllowsSave = slot => WcTest.AllowsSave(slot),
            RefuseSaves = IronMog,
            BeforeApply = s => RepairDiceAccuracy(s.Dir),
            Warn = _ => WcScreen.Toast("Couldn't load this seed's data at startup: restart the game, or pick the seed again on the Worlds Collide screen."),
            OnLoading = () => loadingSave = true,
            Describe = s => s == null ? "none" : $"{s.SeedText} ({s.Hash})",
        };

        internal static int Keep { get => Lib.Keep; set => Lib.Keep = value; }
        public static Seed Active => (Seed)Lib.Active;
        internal static int StartupFailed => Lib.StartupFailed;    // the startup apply's failed tables (-1: not done)
        internal static bool StartupDone => Lib.StartupDone;
        public static string LastApply => Lib.LastApply;

        public static void Init() => Lib.Init();

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

        public static Seed Load(string id) => (Seed)Lib.Load(id);

        // a WC seed's json (the library catches and logs what goes wrong)
        static Seed Parse(string id, string dir)
        {
            var json = Path.Combine(dir, "wc_seed.json");
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                var s = new Seed { Id = id, Dir = dir };
                SeedLibrary.ReadCommon(s, r, "wc_version");
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
                if (r.TryGetProperty("natural_lv1", out var nl1)) s.NaturalLv1 = nl1.ValueKind == JsonValueKind.True;
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
                    s.SnesAi = SnesAiData.Parse(sai);   // the kit's (PrKit/Ff6/SnesAiData.cs)
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
        }

        // newest first
        public static List<Seed> All() => Lib.All().Cast<Seed>().ToList();

        public static string NewId(string seed) => SeedLibrary.NewId(seed);

        public static string DirFor(string id) => Lib.DirFor(id);

        // --- applying ---------------------------------------------------------------------

        // Make `s` the running game's seed (null: vanilla data where a seed had changed it).
        public static string Apply(Seed s) => Lib.Apply(s);

        // Seeds made by 0.6.3-0.6.5 copied the SNES hit-rate byte onto every weapon, but Dice and
        // Fixed Dice keep their number of dice there (2, 3), and the PR rolls a physical hit
        // against it (CalcExecuteFF6.PhysicalExecution): they hit about 2% of the time. Their PR
        // accuracy is 100, as the generator writes them since 0.6.6. The seed's own weapon table
        // is fixed in place, once; accuracy isn't saved, so saves are unaffected.
        static readonly int[] DiceWeapons = { 83, 84 };   // PR ids of Dice, Fixed Dice (SNES 0x51, 0x52)

        static void RepairDiceAccuracy(string seedDir)
        {
            var path = Path.Combine(seedDir, LiveData.MasterDir, "weapon.csv");
            if (!File.Exists(path)) return;
            try
            {
                var text = File.ReadAllText(path);
                var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var lines = text.Split(new[] { newline }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                int col = Array.IndexOf(lines[0].Split(','), "accuracy_rate");
                if (col < 0) return;
                var fixedIds = new List<int>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var cells = lines[i].Split(',');
                    if (cells.Length <= col || !int.TryParse(cells[0], out int id) || !DiceWeapons.Contains(id)) continue;
                    if (!int.TryParse(cells[col], out int acc) || acc >= 10) continue;
                    cells[col] = "100";
                    lines[i] = string.Join(",", cells);
                    fixedIds.Add(id);
                }
                if (fixedIds.Count == 0) return;
                File.WriteAllText(path, string.Join(newline, lines));
                Plugin.L.LogInfo($"[wc] seed weapon table: dice accuracy back to 100 ({string.Join(", ", fixedIds)})");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] dice accuracy repair skipped: {e.Message}"); }
        }

        public static void Prune() => Lib.Prune();

        // The player's Delete in the seed list (WcScreen): not the current seed, nor one a save uses
        public static string Delete(Seed s) => Lib.Delete(s);

        // the headless test (WcTest): a save recorded for a seed, in memory only
        internal static void TestSlot(int slot, string id) => Lib.TestSlot(slot, id);

        public static string SlotSeed(int slot) => Lib.SlotSeed(slot);

        // the active seed's tables/text into the game once its data has loaded
        public static void Update() => Lib.Update();

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
                // Reset once when returning to the title, not on every title frame. Continue's
                // LoadAsync begins while the title is still visible; the old loop cleared its
                // loadingSave marker on the following frame and then treated the loaded autosave
                // as New Game (regranting the seed's starting inventory).
                if (!wasOnTitle)
                {
                    WcEvents.StartRan = false;
                    loadingSave = false;
                    pendingStart = false;
                    startSince = -1;
                }
                wasOnTitle = true;
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
        // the kit's save hooks (SeedLibrary.Patch): each save records its seed, loading brings it back;
        // a test run writes nothing but the slots WcTest allows; WC's -nosaves refuses saving

        public static void Patch(Harmony h) => Lib.Patch(h);

        // a headless self-test never writes the player's saves (the airship deck autosaves)
        // WC's -nosaves (Ironmog mode) in a WC game: no saving, autosaves included (save points still
        // heal with tents/sleeping bags; the field menu hides Save/Quick Save: WcTrack)
        internal static bool IronMog() =>
            Plugin.WcMode && Active != null && Active.Flags.Split(' ').Contains("-nosaves") &&
            WcEvents.Flag(Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

    }
}

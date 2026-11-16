using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BraveNewWorld.Rules
{
    // Stats one EL adds (or the running total of spent ELs). Stat names follow the PR:
    // Power = Vigor, Agility = Speed, Vitality = Stamina ("Spirit" in BNW), Magic = Magic.
    public sealed class StatBonus
    {
        public int Hp, Mp, Power, Agility, Vitality, Magic;

        public bool IsEmpty => Hp == 0 && Mp == 0 && Power == 0 && Agility == 0 && Vitality == 0 && Magic == 0;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Hp != 0) parts.Add($"+{Hp} HP");
            if (Mp != 0) parts.Add($"+{Mp} MP");
            if (Power != 0) parts.Add($"+{Power} Vigor");
            if (Agility != 0) parts.Add($"+{Agility} Speed");
            if (Vitality != 0) parts.Add($"+{Vitality} Spirit");
            if (Magic != 0) parts.Add($"+{Magic} Magic");
            return parts.Count == 0 ? "none" : string.Join("/", parts);
        }
    }

    // What an equipped esper gives while equipped (BNW's EsperBonuses, C2/0EF3).
    public sealed class EquipBonus
    {
        public int Power, Agility, Vitality, Magic, Defense, AbilityDefense, EvasionRate, AbilityEvasionRate;
        public int PhysicalPct, MagicPct, MaxHpPct, MaxMpPct;
        public int[] HalfAttributes = Array.Empty<int>();     // PR attribute ids taking half damage
        public int[] ProtectConditions = Array.Empty<int>();  // PR condition ids blocked
        public int[] AutoConditions = Array.Empty<int>();     // PR condition ids always on
    }

    // A spell an esper teaches: SP to learn it from battles, or to buy it outright from
    // the SP bank on the esper screen (BNW Pressed_A).
    public sealed class EsperSpell
    {
        public int AbilityId, Sp;
        public string Name = "";
    }

    public sealed class Esper
    {
        public int SnesId, ContentId, ElBonus;
        public string Name = "", ElBonusText = "", EquipBonusText = "";
        public int[] UserStatusIds = Array.Empty<int>();       // PR character_status ids allowed to equip it
        public EquipBonus Equip = new EquipBonus();
        public EsperSpell[] Spells = Array.Empty<EsperSpell>();

        public EsperSpell SpellByAbility(int abilityId) => Spells.FirstOrDefault(s => s.AbilityId == abilityId);
    }

    public sealed class RuleSet
    {
        public int MaxEl = 25, MaxEp = 50000, MaxSpBank = 30, StatCap = 128, MaxElCharacterStatusId = 12;
        public int[] EpChart = Array.Empty<int>();             // total EP needed for EL 1..MaxEl
    }

    // Weapon spell procs (c2.asm RandomCast): which spells a weapon casts is game data
    // (battle_extra_data add_ability_weapon); the plugin sets the chance per attacker.
    public sealed class ProcRules
    {
        public int BaseRate = 25, HighRate = 50;                  // percent: 1 in 4, 1 in 2
        public int[] HighRateContentIds = Array.Empty<int>();     // relics with "High Proc Rate"
        // per weapon: its chance, and its chance when a relic adds High Proc Rate
        public Dictionary<int, (int Rate, int High)> Weapons = new Dictionary<int, (int, int)>();

        public int RateFor(int weaponId, bool highRelic) =>
            Weapons.TryGetValue(weaponId, out var r) ? (highRelic ? r.High : r.Rate) : -1;
    }

    // Command wait times (c2.asm CommandWaitTimes): BNW's and vanilla's SNES values, so the
    // plugin can scale the PR's own chant time per command by BNW / vanilla.
    public sealed class CommandWaits
    {
        public Dictionary<int, (int Vanilla, int Bnw)> Commands = new Dictionary<int, (int, int)>();
        public int SummonAbilityType = 9, SummonVanilla, SummonBnw, FightCommandId = 1;
    }

    // An item the PR keys an effect on, as both its weapon/armor id and its content id.
    public sealed class HardItem
    {
        public int ItemId, ContentId;
        public string Name = "";
    }

    // One of the PR's built-in item lists (a static member) and what BNW puts there.
    public sealed class HardList
    {
        public string Type = "", Member = "", Kind = "";  // kind: id, array, dict
        public HardItem[] Vanilla = Array.Empty<HardItem>(), Bnw = Array.Empty<HardItem>();
    }

    // A boss fight that runs BNW's AI, for the F9 boss test.
    public sealed class BossTest
    {
        public int SnesId;
        public string Name = "";
        public int[] Parties = Array.Empty<int>();
        public bool Hybrid;  // BNW's AI, but the PR's battle routine handles who is on screen
    }

    public sealed class BnwData
    {
        public RuleSet Rules = new RuleSet();
        public ProcRules Procs = new ProcRules();
        public CommandWaits Waits = new CommandWaits();
        public HashSet<int> SpiritMagicAbilities = new HashSet<int>();   // use Spirit instead of Magic
        public HashSet<int> SpiritEvadeAbilities = new HashSet<int>();   // Spirit can evade their status
        public HashSet<int> PhysicalSpells = new HashSet<int>();         // spell byte 2 bit 0: Defense, not MDef
        public HashSet<int> WeaponFlagSpells = new HashSet<int>();       // byte 7 bit 3: x1.5 two-handed, else x0.75
        public Dictionary<int, int> BaseBp = new Dictionary<int, int>();  // character_status id -> base Battle Power
        public Dictionary<int, double> SpecialMultipliers = new Dictionary<int, double>();  // monster specials
        public List<HardList> HardItems = new List<HardList>();
        public Dictionary<string, HardItem[]> Passives = new Dictionary<string, HardItem[]>();
        public List<BossTest> BossTests = new List<BossTest>();
        public int[][] SlotReels = Array.Empty<int[]>();      // PR SlotDesignPattern per reel, top to bottom
        public int[][] SlotVanillaReels = Array.Empty<int[]>();
        public bool SlotDerigged;                              // reels stop where the player stops them
        public int[] Brushes = Array.Empty<int>();            // weapon content ids: Sketch needs one
        public int SketchCommandId;
        public int[] UmaroColumns = Array.Empty<int>();       // ability ids: Fight, Tackle, Snowstorm, toss
        public int[][] UmaroRows = Array.Empty<int[]>();       // per relic set: weights of 255, 255 = not available
        // "runic" / "bushido" -> weapon content ids that allow the command (vanilla's, BNW's)
        public Dictionary<string, (int[] Vanilla, int[] Bnw)> CommandWeapons = new Dictionary<string, (int[], int[])>();
        public List<StatBonus> ElBonuses = new List<StatBonus>();
        public List<Esper> Espers = new List<Esper>();
        readonly Dictionary<int, Esper> byContent = new Dictionary<int, Esper>();

        public Esper EsperByContent(int contentId) => byContent.TryGetValue(contentId, out var e) ? e : null;

        public StatBonus ElBonusOf(Esper e) =>
            e != null && e.ElBonus >= 0 && e.ElBonus < ElBonuses.Count ? ElBonuses[e.ElBonus] : new StatBonus();

        public static BnwData Load(string path) => Parse(File.ReadAllText(path));

        public static BnwData Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var d = new BnwData();
            var r = root.GetProperty("rules");
            d.Rules = new RuleSet
            {
                MaxEl = r.GetProperty("max_el").GetInt32(),
                MaxEp = r.GetProperty("max_ep").GetInt32(),
                MaxSpBank = r.GetProperty("max_sp_bank").GetInt32(),
                StatCap = r.GetProperty("stat_cap").GetInt32(),
                MaxElCharacterStatusId = r.GetProperty("max_el_character_status_id").GetInt32(),
                EpChart = Ints(r.GetProperty("ep_chart")),
            };
            foreach (var b in root.GetProperty("el_bonuses").EnumerateArray())
                d.ElBonuses.Add(new StatBonus
                {
                    Hp = b.GetProperty("hp").GetInt32(), Mp = b.GetProperty("mp").GetInt32(),
                    Power = b.GetProperty("power").GetInt32(), Agility = b.GetProperty("agility").GetInt32(),
                    Vitality = b.GetProperty("vitality").GetInt32(), Magic = b.GetProperty("magic").GetInt32(),
                });
            foreach (var e in root.GetProperty("espers").EnumerateArray())
            {
                if (e.GetProperty("content_id").ValueKind != JsonValueKind.Number) continue;
                var q = e.GetProperty("equip_bonus");
                var esper = new Esper
                {
                    SnesId = e.GetProperty("snes_id").GetInt32(),
                    ContentId = e.GetProperty("content_id").GetInt32(),
                    ElBonus = e.GetProperty("el_bonus").GetInt32(),
                    Name = e.GetProperty("name").GetString() ?? "",
                    ElBonusText = Str(e, "el_bonus_text"),
                    EquipBonusText = Str(e, "equip_bonus_text"),
                    UserStatusIds = Ints(e.GetProperty("user_status_ids")),
                    Equip = new EquipBonus
                    {
                        Power = q.GetProperty("power").GetInt32(), Agility = q.GetProperty("agility").GetInt32(),
                        Vitality = q.GetProperty("vitality").GetInt32(), Magic = q.GetProperty("magic").GetInt32(),
                        Defense = q.GetProperty("defense").GetInt32(), AbilityDefense = q.GetProperty("ability_defense").GetInt32(),
                        EvasionRate = q.GetProperty("evasion_rate").GetInt32(),
                        AbilityEvasionRate = q.GetProperty("ability_evasion_rate").GetInt32(),
                        PhysicalPct = q.GetProperty("physical_pct").GetInt32(), MagicPct = q.GetProperty("magic_pct").GetInt32(),
                        MaxHpPct = q.GetProperty("max_hp_pct").GetInt32(), MaxMpPct = q.GetProperty("max_mp_pct").GetInt32(),
                        HalfAttributes = Ints(q.GetProperty("half_attributes")),
                        ProtectConditions = Ints(q.GetProperty("protect_conditions")),
                        AutoConditions = Ints(q.GetProperty("auto_conditions")),
                    },
                };
                if (e.TryGetProperty("spells", out var spells))
                    esper.Spells = spells.EnumerateArray()
                        .Where(s => s.TryGetProperty("ability_id", out var a) && a.ValueKind == JsonValueKind.Number)
                        .Select(s => new EsperSpell
                        {
                            AbilityId = s.GetProperty("ability_id").GetInt32(),
                            Sp = s.GetProperty("sp").GetInt32(),
                            Name = Str(s, "name").Trim(),
                        }).ToArray();
                d.Espers.Add(esper);
                d.byContent[esper.ContentId] = esper;
            }
            if (root.TryGetProperty("procs", out var procs))
            {
                d.Procs = new ProcRules
                {
                    BaseRate = procs.GetProperty("base_rate").GetInt32(),
                    HighRate = procs.GetProperty("high_rate").GetInt32(),
                    HighRateContentIds = Ints(procs.GetProperty("high_rate_content_ids")),
                };
                foreach (var w in procs.GetProperty("weapons").EnumerateArray())
                    d.Procs.Weapons[w.GetProperty("weapon_id").GetInt32()] = (w.GetProperty("rate").GetInt32(), w.GetProperty("high_rate").GetInt32());
            }
            if (root.TryGetProperty("command_waits", out var waits))
            {
                foreach (var c in waits.GetProperty("commands").EnumerateArray())
                    d.Waits.Commands[c.GetProperty("command_id").GetInt32()] = (c.GetProperty("vanilla").GetInt32(), c.GetProperty("bnw").GetInt32());
                var sm = waits.GetProperty("summon");
                d.Waits.SummonAbilityType = sm.GetProperty("ability_type").GetInt32();
                d.Waits.SummonVanilla = sm.GetProperty("vanilla").GetInt32();
                d.Waits.SummonBnw = sm.GetProperty("bnw").GetInt32();
                d.Waits.FightCommandId = waits.GetProperty("fight_command_id").GetInt32();
            }
            if (root.TryGetProperty("spirit", out var spirit))
            {
                d.SpiritMagicAbilities = new HashSet<int>(Ints(spirit.GetProperty("magic_abilities")));
                d.SpiritEvadeAbilities = new HashSet<int>(Ints(spirit.GetProperty("evade_abilities")));
                if (root.TryGetProperty("base_bp", out var bb))
                    foreach (var kv in bb.EnumerateObject()) d.BaseBp[int.Parse(kv.Name)] = kv.Value.GetInt32();
                if (spirit.TryGetProperty("physical_abilities", out var ph))
                    d.PhysicalSpells = new HashSet<int>(Ints(ph));
                if (spirit.TryGetProperty("weapon_flag_abilities", out var wf))
                    d.WeaponFlagSpells = new HashSet<int>(Ints(wf));
                if (spirit.TryGetProperty("special_evade_abilities", out var se))
                    foreach (var id in Ints(se)) d.SpiritEvadeAbilities.Add(id);
                foreach (var kv in spirit.GetProperty("special_multipliers").EnumerateObject())
                    d.SpecialMultipliers[int.Parse(kv.Name)] = kv.Value.GetDouble();
            }
            if (root.TryGetProperty("hard_items", out var hard))
                foreach (var h in hard.EnumerateArray())
                    d.HardItems.Add(new HardList
                    {
                        Type = Str(h, "type"), Member = Str(h, "member"), Kind = Str(h, "kind"),
                        Vanilla = Items(h.GetProperty("vanilla")), Bnw = Items(h.GetProperty("bnw")),
                    });
            if (root.TryGetProperty("passives", out var passives))
                foreach (var kv in passives.EnumerateObject())
                    d.Passives[kv.Name] = Items(kv.Value);
            if (root.TryGetProperty("slot", out var slot))
            {
                d.SlotReels = slot.GetProperty("reels").EnumerateArray().Select(Ints).ToArray();
                d.SlotVanillaReels = slot.GetProperty("vanilla_reels").EnumerateArray().Select(Ints).ToArray();
                d.SlotDerigged = slot.GetProperty("derigged").ValueKind == JsonValueKind.True;
            }
            if (root.TryGetProperty("sketch", out var sketch))
            {
                d.SketchCommandId = sketch.GetProperty("command_id").GetInt32();
                d.Brushes = Ints(sketch.GetProperty("brushes"));
            }
            if (root.TryGetProperty("umaro", out var umaro))
            {
                d.UmaroColumns = Ints(umaro.GetProperty("columns"));
                d.UmaroRows = umaro.GetProperty("rows").EnumerateArray().Select(Ints).ToArray();
            }
            if (root.TryGetProperty("command_weapons", out var cw))
                foreach (var kv in cw.EnumerateObject())
                    d.CommandWeapons[kv.Name] = (Ints(kv.Value.GetProperty("vanilla")), Ints(kv.Value.GetProperty("bnw")));
            if (root.TryGetProperty("boss_tests", out var tests))
                foreach (var t in tests.EnumerateArray())
                    d.BossTests.Add(new BossTest
                    {
                        SnesId = t.GetProperty("snes_id").GetInt32(),
                        Name = Str(t, "name"),
                        Parties = Ints(t.GetProperty("parties")),
                        Hybrid = t.TryGetProperty("hybrid", out var h) && h.ValueKind == JsonValueKind.True,
                    });
            return d;
        }

        static HardItem[] Items(JsonElement e) => e.EnumerateArray().Select(i => new HardItem
        {
            ItemId = i.GetProperty("item_id").GetInt32(),
            ContentId = i.GetProperty("content_id").GetInt32(),
            Name = Str(i, "name"),
        }).ToArray();

        static int[] Ints(JsonElement a) => a.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}

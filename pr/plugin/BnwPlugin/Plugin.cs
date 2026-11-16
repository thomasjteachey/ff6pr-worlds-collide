using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Data.Master;
using Last.Management;
using PrKit;
using UnityEngine;

namespace BraveNewWorld.PR
{
    [BepInPlugin(Guid, "Brave New World (Pixel Remaster)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "ff6mod.bravenewworld";
        public const string Version = "0.22.1";
        internal static ManualLogSource L;

        public override void Load()
        {
            L = Log;
            Kit.Init(Log, "Brave New World");   // the modding kit (plugin/PrKit): hooks, harness, test runs
            var restrict = Config.Bind("Espers", "RestrictEquip", true, "Each esper can only be equipped by its BNW users.");
            var levels = Config.Bind("Espers", "EsperLevels", true, "EP from battles raises Esper Levels; each EL adds the equipped esper's bonus.");
            var equip = Config.Bind("Espers", "EquipBonuses", true, "Equipped espers give BNW's equip bonus (stats, elements, statuses, HP/MP %).");
            var noVanilla = Config.Bind("Espers", "DisableLevelUpBonus", true, "Turn off the vanilla esper level-up stat bonus (BNW replaces it with ELs).");
            var autoSpend = Config.Bind("Espers", "AutoSpendEL", false,
                "Spend each new EL at once on the equipped esper's bonus instead of banking it for the esper menu.");
            var spendKey = Config.Bind("Espers", "SpendKey", "None",
                "ELs are spent by moving the cursor down onto the esper's bonus row and pressing Confirm. " +
                "Optionally also a game key that spends from anywhere on that screen: None, Start, Select, PageUp or PageDown.");
            var bossExp = Config.Bind("Battle", "BossExp", true,
                "Every defeated monster pays its EXP, bosses included, as in BNW (the Pixel Remaster " +
                "leaves boss EXP out of the battle total; vanilla bosses give none, many BNW bosses do).");
            var procs = Config.Bind("Battle", "ProcRules", true,
                "Weapon spell procs follow BNW: 1 in 4 per hit, 1 in 2 with a High Proc Rate relic; brush-odds " +
                "chances (Soul Sabre, brushes) 1 in 2 or 3 in 4. Which spell each weapon casts is game data either way.");
            var hardItems = Config.Bind("Battle", "ItemEffects", true,
                "Move effects the game ties to specific items (encounter relics, physical/magic boost relics, MP-crit, " +
                "healing and Defense-ignoring weapons, Dragon Horn, weapons that allow Runic/Bushido, Sketch only with a brush...) to the items BNW gives them, " +
                "and drop ones BNW removed.");
            var relicRules = Config.Bind("Battle", "RelicRules", true,
                "BNW's numbers for relic effects: two-handed grip x1.5, Black Belt counter (Stamina+32)/129, Dragon Horn " +
                "25% for one extra bounce, MP criticals cost Level/2, +25% magic relics don't stack, esper +25% boosts count.");
            var waitTimes = Config.Bind("Battle", "WaitTimes", true,
                "BNW's command wait times: Bushido, Tools, Blitz, Control, Slot, GP Rain x2, Summon x4, Morph waits, " +
                "Jump/Pray/Shock halved, Steal, Mug, Runic, Leap, Mimic, Row, Defend, Possess, Magitek act at once.");
            var waitGauge = Config.Bind("Battle", "WaitGauge", true,
                "BNW's Wait Gauge: after a command is chosen the ATB bar shows the wait left; the bar turns red under " +
                "Stop, yellow under Slow, green under Haste.");
            var spiritAbilities = Config.Bind("Battle", "SpiritAbilities", true,
                "BNW's Spirit-based abilities (Restore, Regen, Rerise, Aurabolt, many Lores, Rages and Dances) use the " +
                "caster's Spirit (Stamina) instead of Magic.");
            var damageRules = Config.Bind("Battle", "DamageRules", true,
                "BNW's damage changes on top of the game's formulas: Fight formula, defensive variance by Vigor/Spirit, " +
                "back row x0.75, Defend halves magic, BNW's monster special attack multipliers, Imp (equipment still counts, " +
                "an imped attacker does half damage), Blind (a blinded attacker hits 50% of the time, even with can't-miss attacks).");
            var statusTicks = Config.Bind("Battle", "StatusTicks", true,
                "BNW's Regen/Poison/Sap: amounts from MaxHP and Stamina, Poison growing up to x16.5, ticks twice as often.");
            var stealRules = Config.Bind("Battle", "StealRules", true,
                "BNW's Steal: success (Speed*2+112)/256, the rare item with chance Speed/256.");
            var turnRules = Config.Bind("Battle", "TurnRules", true,
                "BNW's turns: half a turn after Defend or Row, a free turn after a successful Steal/Mug or a Scan; " +
                "Morph has no gauge (always available, lasts until Revert or the battle's end); Umaro's attack odds.");
            var reflectRules = Config.Bind("Battle", "ReflectRules", true,
                "BNW's Reflect: no timer, but 1 in 3 to break each time it reflects (not from equipment).");
            var commandLimits = Config.Bind("Battle", "CommandLimits", true,
                "BNW's command limits: Leap only in Veldt formations and while another party member stands; " +
                "Gogo can't Mimic a Mimic (he fights instead); one counterattack per monster per X-Magic.");
            var danceRules = Config.Bind("Battle", "DanceRules", true,
                "BNW's Dance: one dance per command, the dancer stays under control (step odds and no stumbling are game data).");
            var spiritEvade = Config.Bind("Battle", "SpiritEvasion", true,
                "BNW's Spirit evasion: Spirit-evadable spells and monster specials miss a status with chance (Spirit+1)/128.");
            var atbFormula = Config.Bind("Battle", "AtbFormula", true,
                "BNW's ATB speed: characters fill at 75*(Speed+51), monsters at 112.5*(Speed+20), vanilla 96*(Speed+20).");
            var slotRules = Config.Bind("Battle", "SlotRules", true,
                "Setzer's Slot as in BNW: BNW's reels (no Bahamut) and no rigging (reels stop where you stop them).");
            var natb = Config.Bind("Battle", "nATB", false,
                "Extra nATB stop (off: the Pixel Remaster already stops ATB, charging and status timers while an " +
                "action is staged, BattleProgressATB.Update; this also stops them in the action's prepare/after phases).");
            var damageLog = Config.Bind("Testing", "DamageLog", true,
                "Write every damage/heal result with its inputs to BepInEx/bnw_damage.csv (for porting BNW's formulas).");
            var testKit = Config.Bind("Testing", "TestKit", false,
                "F8 on a loaded game: every BNW esper, EL 10 with 10 ELs to spend and the next EL one EP away, " +
                "20 SP banked, 500,000 gil (save to a spare slot to keep it). F9 on the field: start the next " +
                "boss fight that runs BNW's AI. F10 in battle: defeat every enemy (counts as a win). " +
                "Controller: click both sticks (L3+R3): tap in battle = F10, tap on the field = F9, hold 1.5 s = F8.");

            // BNW's rules need BNW's data: its Magicite folder in Magicite's import directory (an
            // install without it would put BNW's rules on the game's own data)
            if (!MagiciteDir.Has("BraveNewWorld"))
            {
                L.LogWarning($"Brave New World's data isn't installed (no BraveNewWorld folder in {MagiciteDir.ImportDir}); BNW rules are off.");
                return;
            }
            var dataPath = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "bnw_data.json");
            try
            {
                EsperHooks.D = BnwData.Load(dataPath);
            }
            catch (Exception e)
            {
                L.LogError($"can't read {dataPath}: {e.Message}. Esper rules are off.");
                AddComponent<BnwRunner>();
                return;
            }
            EsperHooks.Restrict = restrict.Value;
            EsperHooks.Levels = levels.Value;
            EsperHooks.EquipBonuses = equip.Value;
            EsperHooks.NoVanillaBonus = noVanilla.Value;
            EsperHooks.AutoSpend = autoSpend.Value;
            EsperMenu.SpendKeyName = spendKey.Value.Trim();
            TestKitHotkey.Enabled = testKit.Value;
            BattleRewards.Enabled = bossExp.Value;
            WeaponProcs.Enabled = procs.Value;
            HardItems.Enabled = hardItems.Value;
            RelicRules.Enabled = relicRules.Value;
            WaitGauge.WaitTimes = waitTimes.Value;
            WaitGauge.Gauge = waitGauge.Value;
            BattleFormulas.Spirit = spiritAbilities.Value;
            BattleFormulas.Atb = atbFormula.Value;
            BattleFormulas.Damage = damageRules.Value;
            StatusTicks.Enabled = statusTicks.Value;
            CommandRules.Steal = stealRules.Value;
            CommandRules.Turns = turnRules.Value;
            CommandRules.Reflect = reflectRules.Value;
            CommandRules.SpiritEvade = spiritEvade.Value;
            CommandRules.Dance = danceRules.Value;
            DamageLog.Enabled = damageLog.Value;
            var harmony = new Harmony(Guid);
            EsperHooks.Apply(harmony);
            BattleRewards.Apply(harmony);
            WeaponProcs.Apply(harmony);
            HardItems.Apply(harmony);
            RelicRules.Apply(harmony);
            CoverRules.Enabled = relicRules.Value;
            CoverRules.Apply(harmony);
            WaitGauge.Apply(harmony);
            BattleFormulas.Apply(harmony);
            StatusTicks.Apply(harmony);
            ImpRules.Enabled = damageRules.Value;
            ImpRules.Apply(harmony);
            BlindRules.Enabled = damageRules.Value;
            BlindRules.Apply(harmony);
            MorphRules.Enabled = turnRules.Value;
            MorphRules.Apply(harmony);
            CommandWeapons.Enabled = hardItems.Value;
            CommandWeapons.Patch(harmony);
            SlotRules.Enabled = slotRules.Value;
            SlotRules.Apply(harmony);
            UmaroRules.Enabled = turnRules.Value;
            UmaroRules.Apply(harmony);
            NatbRules.Enabled = natb.Value;
            NatbRules.Apply(harmony);
            CommandRules.Apply(harmony);
            SpecialCommands.Enabled = commandLimits.Value;
            SpecialCommands.Apply(harmony);
            ShopCalls.Apply(harmony);   // the mod's added merchants (SysCall "BNW:shop <group>")
            DamageLog.Apply(harmony);
            EsperMenu.Apply(harmony);
            EsperList.Apply(harmony);
            EsperLevelDisplay.Apply(harmony);
            FormulaProbe.Init(harmony);  // only when BepInEx/bnw_probe.request exists
            AddComponent<BnwRunner>();
            L.LogInfo($"Brave New World PR plugin loaded: {EsperHooks.D.Espers.Count} espers, " +
                      $"restrict={restrict.Value} levels={levels.Value} equip={equip.Value} noVanillaBonus={noVanilla.Value} " +
                      $"autoSpend={autoSpend.Value} spendKey={EsperMenu.SpendKeyName} bossExp={bossExp.Value} testKit={testKit.Value}");
        }
    }

    // Per-frame driver: refreshes the cached esper progress (so loading a save is
    // picked up) and logs a one-time check that the Magicite data port applied.
    public class BnwRunner : MonoBehaviour
    {
        public BnwRunner(IntPtr ptr) : base(ptr) { }
        bool verified, textVerified, slotChecked, runicChecked, sketchChecked, veldtChecked, gripChecked, limitsChecked, leapChecked, coverChecked;
        int tries, frame;

        // The Veldt's table as the game loaded it (null until the misc asset is in). The mod's
        // copy (tools/port_veldt.py) has 27 groups, 74 formations, first party 2001; vanilla's
        // 55 groups, 371 formations, first party 6.
        static string VeldtCheck()
        {
            var table = global::Misc.MiscAssetDescAccessor.Desc?.beast_field_encount_table;
            if (table == null) return null;
            int entries = 0, first = -1;
            foreach (var g in table)
            {
                if (g?.party_table == null) continue;
                entries += g.party_table.Length;
                if (first < 0 && g.party_table.Length > 0) first = g.party_table[0].monster_party_id;
            }
            return $"[veldt] {table.Length} groups, {entries} formations, first party {first}";
        }

        void Update()
        {
            if (++frame % 30 == 0) EsperStore.Invalidate();
            if (frame % 10 == 0) { BattleRewards.Track(); WaitGauge.Track(); StatusTicks.Track(); }
            BattleFormulas.Track();  // Shock's cost to its user, after its hits
            if (verified && frame % 300 == 0) { HardItems.Fix(); RelicRules.SetConstants(); }
            TestKitHotkey.Update();
            FormulaProbe.Update();
            if (verified && !veldtChecked && frame % 60 == 0)
            {
                try
                {
                    var line = VeldtCheck();
                    if (line != null) { veldtChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { veldtChecked = true; Plugin.L.LogWarning($"[veldt] check: {e}"); }
            }
            if (verified && !gripChecked && frame % 60 == 0)
            {
                try
                {
                    var line = RelicRules.GripCheck();
                    if (line != null) { gripChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { gripChecked = true; Plugin.L.LogWarning($"[grip] check: {e}"); }
            }
            if (verified && veldtChecked && !limitsChecked && frame % 60 == 0)
            {
                try
                {
                    var line = SpecialCommands.Check();
                    if (line != null) { limitsChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { limitsChecked = true; Plugin.L.LogWarning($"[commands] limits check: {e}"); }
            }
            if (limitsChecked && !leapChecked && frame % 60 == 0)
            {
                try
                {
                    var line = SpecialCommands.BattleCheck();
                    if (line != null) { leapChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { leapChecked = true; Plugin.L.LogWarning($"[commands] Leap check: {e}"); }
            }
            if (leapChecked && !coverChecked && frame % 60 == 0)
            {
                try
                {
                    var line = CoverRules.SelfTest();   // probe battles only
                    if (line != null) { coverChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { coverChecked = true; Plugin.L.LogWarning($"[cover] self-test: {e}"); }
            }
            if (verified && !runicChecked && frame % 30 == 0)
            {
                try
                {
                    var line = CommandWeapons.Apply();
                    if (line != null) { runicChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { runicChecked = true; Plugin.L.LogWarning($"[weapons] {e}"); }
            }
            if (verified && !sketchChecked && frame % 60 == 0)
            {
                try
                {
                    var line = CommandWeapons.SketchCheck();
                    if (line != null) { sketchChecked = true; Plugin.L.LogInfo(line); }
                }
                catch (Exception e) { sketchChecked = true; Plugin.L.LogWarning($"[sketch] check: {e}"); }
            }
            if (verified && !slotChecked && frame % 30 == 0)
            {
                try
                {
                    var line = SlotRules.Check();
                    if (line != null) { slotChecked = true; Plugin.L.LogInfo(line); Plugin.L.LogInfo(UmaroRules.Check()); }
                }
                catch (Exception e) { slotChecked = true; Plugin.L.LogWarning($"[slot] check: {e}"); }
            }
            if (textVerified) return;
            if (verified)
            {
                var msg = MessageManager.Instance;
                var name = msg?.GetMessage("MSG_MAGIC_NAME_13", false);
                if (string.IsNullOrEmpty(name)) return;
                textVerified = true;
                Plugin.L.LogInfo($"[verify] text: ability43='{name}' bonus00='{msg.GetMessage("MSG_BNW_ESPER_BONUS_00", false)}' " +
                                 $"battle line BNW_BTL_018_01='{msg.GetMessage("BNW_BTL_018_01", false)}' " +
                                 $"game story E0055_02_343_b_13='{msg.GetMessage("E0055_02_343_b_13", false)}'");
                return;
            }
            var mm = MasterManager.Instance;
            if (mm == null || !mm.isInitialized) return;
            try
            {
                var guard = mm.GetData<Monster>(1);
                var terra = mm.GetData<CharacterStatus>(1);
                if (guard == null || terra == null) { if (++tries % 300 == 0) Plugin.L.LogInfo("[verify] waiting for master data"); return; }
                verified = true;
                Plugin.L.LogInfo($"[verify] Guard HP={guard.Hp} | Terra Vigor={terra.Strength} Magic={terra.Magic}");
                Plugin.L.LogInfo(EsperHooks.GuardSelfTest(mm));
            }
            catch (Exception e)
            {
                verified = textVerified = true;
                Plugin.L.LogWarning($"[verify] {e}");
            }
        }
    }
}

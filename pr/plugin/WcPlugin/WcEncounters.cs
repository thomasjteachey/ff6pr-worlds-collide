using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Data.Master;
using Last.Data.User;
using Last.Entity.Field;
using Last.Management;
using Last.Map;

namespace WorldsCollide.PR
{
    // WC's (the SNES's) random battles in place of the PR's step counter (the user, 2026-11-11: "can
    // you ensure the encounter rate is on parity with the WC snes"). WC has no encounter-rate options,
    // so WC's are vanilla FF6's.
    // SNES (field/battle.asm CheckBattleWorld / CheckBattleSub): one danger counter ($1F6E, kept in
    // the save) gains a rate on every step where battles can happen (at most $FF00); a battle comes
    // when a random 0-255 is below its high byte, and the counter starts again from 0. Rates: other
    // maps (2 bits per map) normal $70, low $40, high $160, very high $200; world maps (2 bits per
    // 32x32-tile zone and terrain group) normal $C0, low $60, high $180, none 0. Charm Bangle halves
    // them, Moogle Charm stops them. A world map's zone is the one where the world map was last
    // loaded (entered, or back from a battle): the SNES doesn't re-read it as the party walks.
    // Mean steps per battle: world 23 (low 34, high 16), other maps 31 (low 41, high 17).
    // PR (decompiled EncounterLot.CheckEncount, tools/decomp/encounter, preencount): a move counter
    // gains the ground's subtract_steps (100; 160 on two grounds; Charm Bangle / 1.5) per step; past a
    // threshold of 2625-4375 (drawn after each battle) each step draws 1-1000 against the set's
    // cumulative weights 80/80/80/16 (256), so a party comes 25.6% of the time: ~38 steps everywhere.
    // Here, around the step's one call of CheckEncount (FootMonitoring.ImmediatelyBeforeMoveingAction:
    // CheckEncount itself can't be patched or called, its "in List<OwnedCharacterData>" parameter makes
    // Harmony's wrapper an invalid program and the type doesn't load for a call): the PR's threshold is
    // kept out of reach and its counter holds the SNES's danger (so a save keeps it, as the SNES's
    // does); each step is rolled the SNES's way before the game's check; a battle puts the game's
    // counter just under its threshold and draws the party 1-256 (EncounterLot.RandomFraction: the
    // SNES's 5/16, 5/16, 5/16, 1/16), so the game's own check (a cell with encounters, not the Moogle
    // Charm, no encounter pending) lets it happen and the game picks the Veldt's fights, the background
    // and the start (back attacks...) and preloads the battle as it does. Only steps the game counts
    // change the danger; its re-roll after a battle (Shuffle: also drops the preloaded battle) keeps it
    // but after a random battle. Tested headless (WcTest encounters): a battle the game sets up from a
    // step is called off there, as the encounter's start can't run without graphics (the PR's own
    // random battles stop a -nographics game at that point too).
    // Rate codes (vanilla ROM, tests/test_wc.py WcEncounterRates): other maps by the PR monster set,
    // whose vanilla SNES pack belongs to one SNES map; world maps by zone; zone 10's forest (set 418) is
    // the one terrain that differs from its zone. The World of Ruin is all normal.
    static class WcEncounters
    {
        static readonly int[] OtherRate = { 0x70, 0x40, 0x160, 0x200 };   // normal, low, high, very high
        static readonly int[] WorldRate = { 0xC0, 0x60, 0x180, 0 };       // normal, low, high, none
        // PR monster set -> SNES rate code (not listed: normal): SNES maps 41, 262, 263, 264, 313, 315 low, 405 high
        internal static readonly Dictionary<int, int> SetCode = new Dictionary<int, int>
        {
            [136] = 1, [209] = 1, [210] = 1, [211] = 1, [240] = 1, [242] = 1, [295] = 2,
        };
        // World of Balance zone (y / 32 x 8 + x / 32) -> SNES rate code (not listed: normal)
        internal static readonly Dictionary<int, int> WobZoneCode = new Dictionary<int, int>
        {
            [5] = 1, [10] = 1, [12] = 1, [17] = 1, [20] = 1, [22] = 2, [29] = 2,
        };
        const int Zone10Forest = 418;   // zone 10's forest (SNES pack 2): normal
        const int Unreachable = 70000;  // the PR's threshold, past the danger's $FF00 + a step
        static readonly Random Rng = new Random();
        static int zone = -1, zoneMap = -1;
        static bool zoneStale = true;
        internal static int Steps, Battles;
        internal static string Last = "";
        internal static bool Paused;    // tests: no battles (the PR's threshold still out of reach)
        internal static bool Off;       // tests: the PR's own encounters, untouched
        // tests: a battle the game set up is called off again (the encounter's start can't run headless:
        // the PR's own random battles stop a -nographics game there too); counted in SetUp
        internal static bool CancelForTest;
        internal static int SetUp;

        public static void Patch(Harmony h)
        {
            // CheckEncount itself can't be patched or called from managed code: its "in
            // List<OwnedCharacterData>" parameter makes Harmony's wrapper an invalid program (the
            // party froze after 4 steps in the 2026-11-11 test) and the type doesn't load for a call.
            // Its one caller, FootMonitoring.ImmediatelyBeforeMoveingAction(entity), runs it once a step.
            var m = AccessTools.Method(typeof(FootMonitoring), "ImmediatelyBeforeMoveingAction");
            if (m == null) throw new MissingMethodException("FootMonitoring.ImmediatelyBeforeMoveingAction");
            encountMonitor = AccessTools.Property(typeof(FootMonitoring), "encountMonitor");
            mapAccessor = AccessTools.Property(typeof(FootMonitoring), "mapAccessor");
            if (encountMonitor == null || mapAccessor == null)
                throw new MissingMemberException($"FootMonitoring.encountMonitor {encountMonitor != null}, mapAccessor {mapAccessor != null}");
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcEncounters), nameof(Step_Prefix)),
                       postfix: new HarmonyMethod(typeof(WcEncounters), nameof(Step_Postfix)));
            // the party: the PR draws 1-1000 against its sets' cumulative weights (80/80/80/16 = 256),
            // so past its threshold a step finds a party 25.6% of the time and tries again on the next
            // (EncountTable.GetFractionNumber, decompiled tools/decomp/preencount). On a step the SNES's
            // counter chose, the draw is 1-256: the SNES's 5/16, 5/16, 5/16, 1/16 (UpdateBattleGrpRng).
            var fraction = AccessTools.Method(typeof(EncounterLot), "RandomFraction");
            if (fraction == null) throw new MissingMethodException("EncounterLot.RandomFraction");
            h.Patch(fraction, postfix: new HarmonyMethod(typeof(WcEncounters), nameof(RandomFraction_Postfix)));
            var shuffle = AccessTools.Method(typeof(EncounterLot), "Shuffle");
            if (shuffle == null) throw new MissingMethodException("EncounterLot.Shuffle");
            h.Patch(shuffle, prefix: new HarmonyMethod(typeof(WcEncounters), nameof(Shuffle_Prefix)),
                             postfix: new HarmonyMethod(typeof(WcEncounters), nameof(Shuffle_Postfix)));
            Plugin.L.LogInfo("[wc] encounters: the SNES's danger counter hooked");
        }

        static System.Reflection.PropertyInfo encountMonitor, mapAccessor;

        static EncounterLot LotOf(FootMonitoring foot) =>
            (encountMonitor?.GetValue(foot) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<EncounterLot>();

        // the field (FieldController: the step caller's map accessor), kept while the step caller and
        // the map stay the same. A step used to look it up with FindObjectOfType<FieldMap>(), a search
        // of every loaded object: walking stuttered on the Deck (the user, 2026-11-11: "only when walking
        // on the field. battles seem fine").
        static IntPtr fieldFoot;
        static int fieldMap = -1, gridMap = -1, grid;
        static Last.Map.FieldController field;

        static Last.Map.FieldController FieldOf(FootMonitoring foot, int mapId)
        {
            if (field == null || fieldFoot != foot.Pointer || fieldMap != mapId)
            {
                field = (mapAccessor?.GetValue(foot) as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Last.Map.FieldController>();
                fieldFoot = foot.Pointer;
                fieldMap = mapId;
            }
            return field;
        }

        static int GridOf(int mapId)
        {
            if (gridMap != mapId)
            {
                grid = MasterManager.Instance?.GetData<Map>(mapId)?.EncountAreaGridId ?? 0;
                gridMap = mapId;
            }
            return grid;
        }

        static bool Active => Plugin.WcMode && WcSeeds.Active != null;

        // this step's plan (CheckEncount runs on the main thread, one step at a time)
        static int before, next, planRate, planRoll, planGround;
        static bool planned, battle, lastBangle, randomBattle;
        static string planWhere = "";

        // the SNES's danger counter for one step: the new value, and whether a battle comes
        internal static bool Advance(int danger, int rate, int roll, out int after)
        {
            after = Math.Min(Math.Clamp(danger, 0, 0xFF00) + Math.Max(0, rate), 0xFF00);
            return rate > 0 && roll < after >> 8;
        }

        // before the game's own check: the SNES's roll for this step, as if the game counts it. A
        // battle: the game's counter 0 under a threshold of 1, so this step's value passes it and the
        // game itself picks the party and starts the fight; else the threshold out of reach. A step the
        // game doesn't count (no battles on the cell, a pending encounter) changes nothing.
        static EncounterLot planLot;

        public static void Step_Prefix(FootMonitoring __instance, FieldEntity __0)
        {
            planned = false;
            if (!Active || Off) return;
            long t0 = WcPerf.Now();
            try
            {
                var lot = LotOf(__instance);
                if (lot == null || !lot.Enable || lot.IsReady()) return;   // no encounters here / one pending
                int mapId = UserDataManager.Instance()?.CurrentMapId ?? 0;
                var fc = FieldOf(__instance, mapId);
                if (fc == null) return;
                planLot = lot;
                before = Math.Clamp(lot.GetMoveCount(), 0, 0xFF00);
                planRate = RateHere(fc, mapId, __0, lastBangle, out planWhere, out planGround);
                planRoll = Rng.Next(256);
                battle = Advance(before, planRate, planRoll, out next) && !Paused;
                if (battle) lot.SetRestoreData(0, 1);
                else lot.SetRestoreData(before, Unreachable);
                planned = true;
            }
            catch (Exception e) { WcRunner.WarnOnce("encounters-pre", e); }
            finally { WcPerf.Lap(9, "encounters", t0); }
        }

        public static void RandomFraction_Postfix(EncountTable __0, ref int __result)
        {
            if (!planned || !battle || __0 == null || __result >= 0) return;
            try { __result = __0.GetFractionNumber(1 + Rng.Next(256)); }
            catch (Exception e) { WcRunner.WarnOnce("encounters-party", e); }
        }

        // the game's re-roll after every battle (and its own resets; it also drops the battle it
        // preloaded, so it runs): the SNES's counter only starts again from 0 when a random battle
        // comes (event battles, map changes and the rest keep it)
        static int shuffleDanger = -1;

        public static void Shuffle_Prefix(EncounterLot __instance)
        {
            shuffleDanger = -1;
            if (!Active || Off) return;
            try { shuffleDanger = randomBattle ? 0 : Math.Clamp(__instance.GetMoveCount(), 0, 0xFF00); }
            catch (Exception e) { WcRunner.WarnOnce("encounters-shuffle", e); }
        }

        public static void Shuffle_Postfix(EncounterLot __instance)
        {
            if (shuffleDanger < 0) return;
            try { __instance.SetRestoreData(shuffleDanger, Unreachable); }
            catch (Exception e) { WcRunner.WarnOnce("encounters-shuffle", e); }
            randomBattle = false;
            shuffleDanger = -1;
        }

        public static void Step_Postfix()
        {
            if (!planned) return;
            planned = false;
            var lot = planLot;
            planLot = null;
            long t0 = WcPerf.Now();
            try
            {
                int counted = lot.GetMoveCount() - (battle ? 0 : before);
                if (counted <= 0) { lot.SetRestoreData(before, Unreachable); return; }   // not a step here
                bool encounter = lot.IsReady();   // CheckEncount's yes: the encounter it set up
                lastBangle = planGround > 1 && counted < planGround;   // the PR's Charm Bangle: its value / 1.5
                Steps++;
                Last = $"{planWhere}: +{planRate:X} -> {next:X4}, roll {planRoll}";
                if (!battle)
                {
                    lot.SetRestoreData(Paused ? before : next, Unreachable);
                    return;
                }
                // the SNES's counter starts again from 0 (and from 0 again at the game's re-roll after it)
                if (!encounter) lot.SetRestoreData(0, Unreachable);
                randomBattle = encounter;
                zoneStale = true;   // the SNES reloads the world map after a battle
                Battles++;
                Last += encounter ? " -> battle" : " -> no party here";
                Plugin.L.LogInfo($"[wc] encounters: {Last} (step {Steps})");
                if (encounter && CancelForTest)
                {
                    SetUp++;
                    lot.ClearEncountData();
                    lot.SetRestoreData(0, Unreachable);
                    randomBattle = false;
                    Plugin.L.LogInfo("[wc] encounters: the test called that battle off");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("encounters", e); }
            finally { WcPerf.Lap(9, "encounters", t0); }
        }

        // the SNES's rate for a step onto this entity's destination (bangle: the Charm Bangle's half,
        // as the game counted the last step; ground: the game's own value for this cell)
        internal static int RateHere(Last.Map.FieldController fc, int mapId, FieldEntity entity, bool bangle, out string where, out int ground)
        {
            var info = fc?.Last_Map_IMapEncounter_GetEncountInfo(entity, true);
            int set = info?.Table?.MonsterSetId ?? 0;
            ground = info != null && info.FootInfo.HasValue ? info.FootInfo.Value.SubtrackStep : 1;
            int grid = GridOf(mapId);
            int code;
            if (grid == 1 || grid == 2)
            {
                if (zoneStale || zoneMap != mapId || zone < 0)
                {
                    var c = fc.ConvertWorldPositionToCellPosition(entity.transform.position);
                    int x = Math.Clamp(UnityEngine.Mathf.RoundToInt(c.x), 0, 255), y = Math.Clamp(UnityEngine.Mathf.RoundToInt(c.y), 0, 255);
                    zone = y / 32 * 8 + x / 32;
                    zoneMap = mapId;
                    zoneStale = false;
                }
                code = grid == 1 ? WobZoneCode.GetValueOrDefault(zone, 0) : 0;
                if (grid == 1 && zone == 10 && set == Zone10Forest) code = 0;
                where = $"map {mapId} zone {zone} set {set}{(bangle ? " bangle" : "")}";
                return WorldRate[code] >> (bangle ? 1 : 0);
            }
            code = SetCode.GetValueOrDefault(set, 0);
            where = $"map {mapId} set {set}{(bangle ? " bangle" : "")}";
            return OtherRate[code] >> (bangle ? 1 : 0);
        }
    }
}

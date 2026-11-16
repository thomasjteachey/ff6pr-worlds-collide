using System;
using System.Linq;
using HarmonyLib;
using Last.Defaine;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Setzer's Slot as BNW has it (c2.asm 1126, synchysi slots.asm; read from the 3.0 ROM by
    // tools/bnw_plugin_data.py "slot"). The PR (decompiled tools/decomp/slot):
    //  * reels: Serial.FF6.Management.SlotInfomationData keeps three List<SlotDesignPattern>
    //    (the SNES reels in the same order); BattleSlotController.Initialize hands them to the
    //    reel controllers, which keep the list. BNW's reels have no Bahamut and group each
    //    symbol in threes: the lists are refilled in place before the slot UI is built.
    //  * rigging: when reel 2/3 stops, IsHit{Second,Third}DesignLottery decides by a 1-in-N
    //    roll per symbol whether the reel may slide up to 5 symbols onto a match (reel 3 slips
    //    one past a match when the roll fails; 7-7-7 gets through 1 time in 32). BNW removed the
    //    rigging (C1/806D, C1/80A6 branch over it): reels stop where the player stops them, so
    //    StopReel runs with isHit = false and isEyeCatching = true (no slide, no slip).
    //  * results (GetHitAbility): 3 of a kind -> its ability, 7-7-Bar -> 215, else 210. BNW's
    //    7-7-7 Jackpot and "no 7-7-Bar" are data (tools/port_slots.py: 216 = Jackpot,
    //    215 = the losing spin); Check() logs what the game resolves them to.
    // Bar-Bar-Bar's espers are data too (port_slots: random group 9 = BNW's 27, from 25).
    static class SlotRules
    {
        internal static bool Enabled = true;

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            var d = EsperHooks.D;
            if (d.SlotDerigged)
            {
                Hooks.Patch(h, typeof(Last.UI.KeyInput.BattleSlotReelController), "StopReel", typeof(SlotRules), prefix: nameof(StopReel_Prefix));
                Hooks.Patch(h, typeof(Last.UI.Touch.BattleSlotReelController), "StopReel", typeof(SlotRules), prefix: nameof(StopReel_Prefix));
            }
            if (d.SlotReels.Length == 3)
            {
                Hooks.Patch(h, typeof(Last.UI.KeyInput.BattleSlotController), "Initialize", typeof(SlotRules), prefix: nameof(Reels_Prefix));
                Hooks.Patch(h, typeof(Last.UI.Touch.BattleSlotController), "Initialize", typeof(SlotRules), prefix: nameof(Reels_Prefix));
            }
        }

        public static void StopReel_Prefix(ref bool isHit, ref bool isEyeCatching)
        {
            if (!Enabled) return;
            isHit = false;
            isEyeCatching = true;
            Hooks.NoteOnce("slot-derig", () => "[slot] reel stopped where the player stopped it (BNW: no rigging)");
        }

        public static void Reels_Prefix()
        {
            if (!Enabled) return;
            try { SetReels(); }
            catch (Exception e) { Hooks.WarnOnce("SlotRules.Reels", e); }
        }

        static Serial.FF6.Management.SlotInfomationData Data() =>
            Serial.ProviderManager.Instance?.SlotInfomationProvider?.serialData?.TryCast<Serial.FF6.Management.SlotInfomationData>();

        // Refills the game's reel lists with BNW's; a list that is neither vanilla's nor BNW's
        // (a game update) is left alone. Returns a one-line summary.
        internal static string SetReels()
        {
            var d = EsperHooks.D;
            var data = Data();
            if (data == null) return "slot data not loaded yet";
            var lists = new[] { data.firstReelDesignPatternList, data.secondReelDesignPatternList, data.thirdReelDesignPatternList };
            var notes = new string[3];
            for (int r = 0; r < 3; r++)
            {
                var list = lists[r];
                if (list == null) { notes[r] = "missing"; continue; }
                var now = Enumerable.Range(0, list.Count).Select(i => (int)list[i]).ToArray();
                if (now.SequenceEqual(d.SlotReels[r])) { notes[r] = "BNW"; continue; }
                if (r < d.SlotVanillaReels.Length && !now.SequenceEqual(d.SlotVanillaReels[r]))
                {
                    notes[r] = "unknown layout, left alone: " + string.Join(",", now);
                    continue;
                }
                list.Clear();
                foreach (var s in d.SlotReels[r]) list.Add((SlotDesignPattern)s);
                notes[r] = "BNW (was vanilla)";
            }
            var line = $"[slot] reels: {string.Join("; ", notes)}";
            Hooks.NoteOnce("slot-reels", () => line);
            return line;
        }

        // espers Bar-Bar-Bar (214, function 46) can summon: random group 9 rows with a weight
        static string BarEspers()
        {
            var list = Last.Data.Master.MasterManager.Instance?.GetList<Last.Data.Master.AbilityRandomGroup>();
            if (list == null) return "?";
            int n = 0, total = 0;
            foreach (var kv in list)
            {
                var r = kv.Value;
                if (r == null || r.GroupId != 9 || r.InvocationRate <= 0) continue;
                n++;
                total += r.InvocationRate;
            }
            return $"{n} (total weight {total})";
        }

        // Startup check: the reels, and the abilities 7-7-7 / 7-7-Bar / a loss resolve to.
        internal static string Check()
        {
            var data = Data();
            if (data == null) return null;
            var reels = SetReels();
            string Hit(SlotDesignPattern a, SlotDesignPattern b, SlotDesignPattern c)
            {
                var ab = data.GetHitAbility(a, b, c);
                return ab == null ? "none" : $"{ab.Id} (functions {ab.BattleFunctionGroupId}, power {ab.StandardValue}, range {ab.BattleRengeId})";
            }
            return $"{reels} | 7-7-7 -> {Hit(SlotDesignPattern.Seven, SlotDesignPattern.Seven, SlotDesignPattern.Seven)}; " +
                   $"7-7-Bar -> {Hit(SlotDesignPattern.Seven, SlotDesignPattern.Seven, SlotDesignPattern.BAR)}; " +
                   $"loss -> {Hit(SlotDesignPattern.Seven, SlotDesignPattern.BAR, SlotDesignPattern.Diamond)}; " +
                   $"rigging {(EsperHooks.D.SlotDerigged ? "off" : "the game's")}; Bar-Bar-Bar espers {BarEspers()}";
        }
    }
}

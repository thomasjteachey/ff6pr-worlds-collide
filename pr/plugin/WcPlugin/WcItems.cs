using System;
using System.Collections.Generic;
using System.Linq;

namespace WorldsCollide.PR
{
    // The weapons that allow Runic and Bushido (WC's -stra: all of them; WC's item changes move
    // the SNES bits too). The PR keeps both lists in code (Serial.FF6.Management.BlockMagicSwordData
    // / KillingSwordData .ValidWeaponContentId, exactly vanilla's SNES bits): set to the seed's
    // (wc_seed.py command_weapons) while it's active, vanilla's back without a seed.
    static class WcItems
    {
        // WC's -ari: the auction's four normal sales (only they GetItem these contents) give the
        // seed's item, and their announcements name it
        static readonly Dictionary<int, string> AuctionAnnounce = new Dictionary<int, string>
        {
            [287] = "E0424_00_111_a_01", [288] = "E1055_00_111_a_01", [291] = "E0751_00_111_a_01", [300] = "E0752_00_111_a_01",
        };

        // WC's -scan: every enemy can be scanned (the PR's monster flag 1 = the SNES "can't scan",
        // enemy byte 19 bit 4; MonsterFlagGroupUtility answers it)
        const int CantScan = 1;

        public static void Flag_Postfix(int flagId, ref bool __result)
        {
            if (__result && flagId == CantScan && Plugin.WcMode && WcSeeds.Active?.Flags.Split(' ').Contains("-scan") == true) __result = false;
        }

        // WC's -frw / -frm (flash removal): the scripts' screen flashes (External.Fader.Flash:
        // iValues RGBA, rValues[0] seconds) keep their timing with no light: -frm all, -frw the
        // bright white ones
        public static void Flash_Prefix(global::Last.Interpreter.MainCore mc, out int __state)
        {
            __state = -1;
            try
            {
                var s = WcSeeds.Active;
                if (!Plugin.WcMode || s == null) return;
                var f = s.Flags.Split(' ');
                bool most = f.Contains("-frm"), worst = f.Contains("-frw");
                var iv = mc?.op?.iValues;
                if ((!most && !worst) || iv == null || iv.Length < 4 || iv[3] == 0) return;
                if (!most && !(iv[0] >= 200 && iv[1] >= 200 && iv[2] >= 200 && iv[3] >= 150)) return;
                __state = iv[3];
                iv[3] = 0;
            }
            catch (Exception e) { WcRunner.WarnOnce("flash", e); }
        }

        public static void Flash_Postfix(global::Last.Interpreter.MainCore mc, int __state)
        {
            if (__state < 0) return;
            try { mc.op.iValues[3] = __state; } catch (Exception e) { WcRunner.WarnOnce("flash-restore", e); }
        }

        // WC's -ccrs (chest contents scaled by chests opened): a plain chest about to open
        // (EventAction.OpenTreasureBox: the coroutine reads its property later) holds the seed's
        // n-th item (or gil), n = the chests of its kind already open
        internal static string LastChest = "";

        public static void OpenChest_Prefix(global::Last.Entity.Field.FieldTresureBox entity)
        {
            try
            {
                var s = WcSeeds.Active;
                if (s == null || entity == null || entity.isOpen || !WcGame()) return;
                var p = entity.tresureBoxProperty;
                if (p == null || global::Last.Map.MapUIManager.GetTreasureFlag(p.FlagId)) return;
                // WC's -chrm: a chest WC made a monster-in-a-box is empty, and its battle starts
                // once the chest has opened (WcBattles.Queue)
                if (s.MonsterChests.TryGetValue(p.FlagId, out int boxParty))
                {
                    p.ContentId = 1;
                    p.ContentNum = 0;
                    p.MessageKey = "T0001_04_01";   // "Monster-in-a-box!" (the chest's own key is its old contents')
                    WcBattles.Queue(boxParty);
                    LastChest = $"chest {p.FlagId}: monster-in-a-box, party {boxParty}";
                    Plugin.L.LogInfo("[wc] -chrm " + LastChest);
                    return;
                }
                if (s.ScaledItems.Length == 0) return;
                int Opened(int[] flags) => flags.Count(f => f != p.FlagId && global::Last.Map.MapUIManager.GetTreasureFlag(f));
                if (p.ContentId == 1 && p.ContentNum > 0 && s.ScaledGold.Length > 0)
                {
                    int n = Opened(s.ScaledGoldFlags);
                    p.ContentNum = s.ScaledGold[Math.Min(n, s.ScaledGold.Length - 1)];
                    p.MessageKey = "T0001_03_01";   // Obtained (NUM) gil!
                    LastChest = $"chest {p.FlagId}: gil chest {n + 1} -> {p.ContentNum} gil";
                }
                else if (p.ContentId > 1)
                {
                    int n = Opened(s.ScaledItemFlags);
                    p.ContentId = s.ScaledItems[Math.Min(n, s.ScaledItems.Length - 1)];
                    p.ContentNum = 1;
                    p.MessageKey = "T0001_01_01";   // Obtained (ITEM_N)!
                    LastChest = $"chest {p.FlagId}: item chest {n + 1} -> content {p.ContentId}";
                }
                else return;
                Plugin.L.LogInfo("[wc] -ccrs " + LastChest);
            }
            catch (Exception e) { WcRunner.WarnOnce("scaled-chest", e); }
        }

        public static void Patch(HarmonyLib.Harmony h)
        {
            var open = HarmonyLib.AccessTools.Method(typeof(global::Last.Map.EventAction), "OpenTreasureBox");
            if (open != null) h.Patch(open, prefix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(OpenChest_Prefix)));
            var flag = HarmonyLib.AccessTools.Method(typeof(global::Last.Systems.MonsterFlagGroupUtility), "IsMatcthingFlag");
            if (flag != null) h.Patch(flag, postfix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(Flag_Postfix)));
            var flash = HarmonyLib.AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Fader), "Flash");
            if (flash != null) h.Patch(flash, prefix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(Flash_Prefix)),
                                       postfix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(Flash_Postfix)));
            var m = HarmonyLib.AccessTools.Method(typeof(global::Last.Interpreter.Instructions.External.Item), "GetItem");
            if (m != null) h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(GetItem_Prefix)),
                                   postfix: new HarmonyLib.HarmonyMethod(typeof(WcItems), nameof(GetItem_Postfix)));
        }

        static bool WcGame() => Plugin.WcMode && WcSeeds.Active != null &&
                                WcEvents.Flag(global::Last.Interpreter.DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        public static void GetItem_Prefix(global::Last.Interpreter.MainCore mc, out int __state)
        {
            __state = 0;
            try
            {
                var iv = mc?.op?.iValues;
                if (iv == null || iv.Length == 0 || !WcGame()) return;
                if (AuctionAnnounce.ContainsKey(iv[0])) WcTrack.SetQuest("Win An Auction");   // WC counts these sales too
                if (!WcSeeds.Active.AuctionItems.TryGetValue(iv[0], out int to)) return;
                __state = iv[0];
                iv[0] = to;
                Plugin.L.LogInfo($"[wc] -ari: auction item {__state} -> {to}");
            }
            catch (Exception e) { WcRunner.WarnOnce("auction-item", e); }
        }

        public static void GetItem_Postfix(global::Last.Interpreter.MainCore mc, int __state)
        {
            if (__state == 0) return;
            try { mc.op.iValues[0] = __state; } catch (Exception e) { WcRunner.WarnOnce("auction-item-restore", e); }
        }

        static void AuctionTexts(WcSeeds.Seed s)
        {
            if (s == null) return;
            foreach (var kv in s.AuctionItems)
                if (AuctionAnnounce.TryGetValue(kv.Key, out var key))
                    WcEvents.SetText(key, $"...a fine {WcEvents.ContentName(kv.Value)}!");
        }
        internal static string LastWeapons = "";
        static string appliedFor;
        static readonly Dictionary<string, int[]> original = new Dictionary<string, int[]>();   // the game's own

        static Il2CppSystem.Collections.Generic.List<int> GameList(string command)
        {
            var pm = Serial.ProviderManager.Instance;
            if (pm == null) return null;
            return command switch
            {
                "runic" => pm.BlockMagicSwordProvider?.serialData?.TryCast<Serial.FF6.Management.BlockMagicSwordData>()?.ValidWeaponContentId,
                "bushido" => pm.KillingSwordProvider?.serialData?.TryCast<Serial.FF6.Management.KillingSwordData>()?.ValidWeaponContentId,
                _ => null,
            };
        }

        // every few seconds (WcObjectives.Poll): once per seed change
        internal static void Apply()
        {
            var s = WcSeeds.Active;
            string key = Plugin.WcMode && s != null ? s.Id : "";
            if (key == appliedFor) return;
            var notes = new List<string>();
            foreach (var command in new[] { "runic", "bushido" })
            {
                var list = GameList(command);
                if (list == null) return;   // providers not up yet: next poll
                var now = Enumerable.Range(0, list.Count).Select(i => list[i]).OrderBy(x => x).ToArray();
                if (!original.ContainsKey(command)) original[command] = now;
                int[] want = key != "" && s.CommandWeapons.TryGetValue(command, out var seedList) ? seedList : original[command];
                if (now.SequenceEqual(want.OrderBy(x => x))) { notes.Add($"{command}: {now.Length}"); continue; }
                list.Clear();
                foreach (var id in want) list.Add(id);
                notes.Add($"{command}: {now.Length} -> {want.Length}");
            }
            appliedFor = key;
            if (key != "") AuctionTexts(s);
            LastWeapons = string.Join("; ", notes);
            Plugin.L.LogInfo("[wc] Runic/Bushido weapons: " + LastWeapons);
        }
    }
}

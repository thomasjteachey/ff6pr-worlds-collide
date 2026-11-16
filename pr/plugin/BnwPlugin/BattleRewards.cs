using System;
using System.Collections.Generic;
using HarmonyLib;
using Last.Battle;
using Last.Management;
using Last.Systems;
using UnityEngine;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Battle EXP the BNW way: every defeated monster pays its EXP, bosses included.
    // Vanilla FF6 bosses give no EXP (111 of 113 have 0); BNW gives it for 36 of them.
    // Seen in game (v0.8.6, Holy Dragon 5000 EXP, fight started with F9): the dragon was
    // on the defeated list (BattleAddExpPlug.expeditionMonsters) and the game's own
    // total GetExp() said 5000, but the result was built with expValue 0. So when the
    // result is built, a smaller expValue is raised to the game's own total.
    static class BattleRewards
    {
        internal static bool Enabled = true;

        static int battleParty = -1;     // formation of the battle in progress
        static int winExp;               // what F10's kills are worth (fallback)
        static float winAt = -1f;
        static int logged;

        // Called every few frames: the formation is gone by the time the result is built,
        // and GetExp needs it for the 5 formations that cap called-in monsters' EXP.
        internal static void Track()
        {
            try
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) return;
                battleParty = battle.MonsterParty?.Id ?? -1;
            }
            catch (Exception e) { Hooks.WarnOnce("BattleRewards.Track", e); }
        }

        internal static void NoteWin(int exp)
        {
            float now = Time.unscaledTime;
            if (winAt < 0f || now - winAt > 300f) winExp = 0;
            winExp += exp;
            winAt = now;
        }

        public static void Apply(Harmony h) =>
            Hooks.Patch(h, typeof(BattleResultProvider), "Genelate", typeof(BattleRewards), prefix: nameof(Result_Prefix));

        public static void Result_Prefix(BattleResultProvider __instance, ref int expValue, int gilValue)
        {
            if (!Hooks.IsA<BattleResultProvider>(__instance)) return;
            bool f10 = winAt >= 0f && Time.unscaledTime - winAt < 300f;
            int f10Exp = f10 ? winExp : 0;
            winAt = -1f;
            if (!Enabled && !f10) return;
            try
            {
                var battle = BattlePlugManager.Instance();
                var plug = battle?.BattleAddExpPlug;
                int live = battle?.MonsterParty?.Id ?? -1;
                int party = live >= 0 ? live : battleParty;
                var seen = new List<string>();
                var list = plug?.expeditionMonsters;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var m = list[i];
                        if (m != null) seen.Add($"{m.Id}{(m.Boss != 0 ? " boss" : "")} {m.Exp}");
                    }
                int gameExp = plug != null ? plug.GetExp(party) : -1;
                int gameGil = plug != null ? plug.GetGil() : -1;

                int target = Enabled ? Math.Max(gameExp, 0) : 0;
                if (gameExp <= 0) target = Math.Max(target, f10Exp);  // list gone: F10's own count
                int before = expValue;
                if (expValue < target)
                {
                    // the game's figure is after Config > Boost; scale the total the same way
                    var cheat = CheatSettingsClient.Instance();
                    expValue = Math.Max(expValue, cheat != null ? cheat.GetResultExp(target) : target);
                }
                if (logged++ < 3 || expValue != before)
                {
                    int boost = CheatSettingsClient.Instance()?.GetResultExp(100) ?? -1;
                    Plugin.L.LogInfo($"[rewards] formation {party}{(live < 0 ? " (tracked)" : "")}: game passed EXP {before}, " +
                                     $"its total {gameExp} (boost {boost}%), gil {gilValue} of {gameGil}; " +
                                     $"defeated [{string.Join(", ", seen)}]" + (f10 ? $"; F10 kills {f10Exp}" : "") +
                                     (expValue != before ? $" -> paying {expValue} EXP" : ""));
                }
            }
            catch (Exception e) { Hooks.WarnOnce("BattleRewards", e); }
        }
    }
}

using System;
using HarmonyLib;
using Last.Battle;
using UnityEngine;
using ActState = Last.Battle.BattleActExection.ActState;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's nATB (c2.asm 873, c3.asm 2542 "freezing time during animations"): while an action
    // plays out, time stops: no ATB filling and no spell charging (the PR's Wait mode only
    // stops for menus). The PR fills gauges in BattleProgressATB.UpdateGaugeIncrease and
    // charges in UpdateChantTime, every frame; both are skipped while the battle's action
    // executor is preparing, playing or finishing an action or a counter.
    // Found later (decompiled BattleProgressATB.Update, tools/decomp/natb): the PR itself
    // returns from the whole ATB update (gauges, charging, status timers) while
    // BattleActExection.GetStatingAct() or !BattleUtility.IsStagingEnd(), i.e. nATB is native;
    // this hook only adds the prepare/after phases, so config [Battle] nATB defaults to off.
    // Safety: never frozen for more than 15 s in a row (a state the game never leaves would
    // otherwise stop the battle).
    static class NatbRules
    {
        internal static bool Enabled = true;
        static float frozenSince = -1f;
        static bool gaveUp;
        static int logged;

        public static void Apply(Harmony h)
        {
            if (!Enabled) return;
            Hooks.Patch(h, typeof(BattleProgressATB), "UpdateGaugeIncrease", typeof(NatbRules), prefix: nameof(Tick_Prefix));
            Hooks.Patch(h, typeof(BattleProgressATB), "UpdateChantTime", typeof(NatbRules), prefix: nameof(Tick_Prefix));
            int n = 0;
            foreach (var name in new[] { "UpdateGaugeIncrease", "UpdateChantTime" })
            {
                var m = AccessTools.Method(typeof(BattleProgressATB), name);
                if (m != null && Harmony.GetPatchInfo(m)?.Prefixes.Count > 0) n++;
            }
            Plugin.L.LogInfo($"[natb] {n} of 2 ATB updates hooked");
        }

        static int ticks;
        static readonly System.Collections.Generic.HashSet<ActState> seen = new System.Collections.Generic.HashSet<ActState>();

        static bool Animating()
        {
            var ex = BattlePlugManager.Instance()?.BattleActExection;
            if (ex == null) return false;
            var s = ex.nowState;
            seen.Add(s);
            if (++ticks == 600 || ticks == 6000 || ticks == 60000)
                Plugin.L.LogInfo($"[natb] {ticks} gauge updates; action states seen: {string.Join(", ", seen)}");
            return s == ActState.InPrepare || s == ActState.InAction || s == ActState.InAfterAction ||
                   s == ActState.CounterAct || s == ActState.CounterAI;
        }

        // false = skip the game's update this frame (time stopped)
        public static bool Tick_Prefix()
        {
            if (!Enabled || gaveUp) return true;
            try
            {
                float now = Time.unscaledTime;
                if (!Animating())
                {
                    if (frozenSince >= 0f && logged < 3)
                    {
                        logged++;
                        Plugin.L.LogInfo($"[natb] time stopped {now - frozenSince:0.00} s for an action");
                    }
                    frozenSince = -1f;
                    return true;
                }
                if (frozenSince < 0f)
                {
                    frozenSince = now;
                    Hooks.NoteOnce("natb", () => "[natb] time stops while an action plays (BNW nATB)");
                }
                else if (now - frozenSince > 15f)
                {
                    gaveUp = true;
                    Plugin.L.LogWarning($"[natb] an action state lasted {now - frozenSince:0} s: time no longer stops for actions this session");
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                gaveUp = true;
                Hooks.WarnOnce("NatbRules", e);
                return true;
            }
        }
    }
}

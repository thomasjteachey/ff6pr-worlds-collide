using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Battle;
using Last.Defaine;
using UnityEngine;
using InfoController = Last.UI.KeyInput.BattlePlayerInfomationController;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's Wait Gauge and ATB colours (c1.asm WaitRemaining/DrawGauge, ee.asm StatusATB):
    //  * command wait times: the PR already waits ("chants") between a command and its
    //    action, per ability. BNW changed SNES's per-command waits (c2.asm CommandWaitTimes):
    //    Bushido/Tools/Blitz/Control/Slot/GP Rain x2, Summon x4, Morph from none to Magic's
    //    double, Jump/Health/Shock halved, Steal/Mug/Runic/Leap/Mimic/Row/Defend/Possess/
    //    Magitek none. Each chant time is scaled by BNW / vanilla for its command;
    //  * while a character waits, their ATB bar shows the wait left, draining (the cast bar);
    //  * the bar is red under Stop, yellow under Slow, green under Haste.
    static class WaitGauge
    {
        internal static bool WaitTimes = true, Gauge = true;
        static BnwData D => EsperHooks.D;

        static readonly Color Stop = new Color(213 / 255f, 41 / 255f, 41 / 255f);
        static readonly Color Slow = new Color(1f, 222 / 255f, 0f);
        static readonly Color Haste = new Color(41 / 255f, 213 / 255f, 41 / 255f);

        static readonly Dictionary<IntPtr, float> totals = new Dictionary<IntPtr, float>();  // action -> chant time
        static readonly Dictionary<IntPtr, (float Last, int Dir)> clocks = new Dictionary<IntPtr, (float, int)>();
        static readonly Dictionary<IntPtr, Color> baseColors = new Dictionary<IntPtr, Color>();
        static float fightUnit = -1f;  // the PR's chant time per SNES wait unit, seen on Fight
        static float fullValue = 1f;   // the gauge value of a full ATB bar

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(BattleProgressATBFF6), "GetChantingTime", typeof(WaitGauge), postfix: nameof(Chant_Postfix));
            Hooks.Patch(h, typeof(InfoController), "UpdateATB", typeof(WaitGauge), prefix: nameof(Atb_Prefix), postfix: nameof(Atb_Postfix));
        }

        // --- wait times ---------------------------------------------------------------------

        public static void Chant_Postfix(BattleProgressATBFF6 __instance, BattleActData battleActData, ref float __result)
        {
            if (D == null || battleActData == null || !Hooks.IsA<BattleProgressATBFF6>(__instance)) return;
            try
            {
                if (WaitTimes && battleActData.AttackUnitData?.TryCast<BattlePlayerData>() != null)
                {
                    var (vanilla, bnw, what) = WaitsFor(battleActData);
                    float before = __result;
                    if (what == "Fight" && before > 0) fightUnit = before / Math.Max(1, vanilla);
                    if (vanilla > 0 && vanilla != bnw) __result = before * bnw / vanilla;
                    else if (vanilla == 0 && bnw > 0 && fightUnit > 0) __result = Math.Max(before, fightUnit * bnw);
                    float after = __result;
                    if (what != null)
                        Hooks.NoteOnce("chant-" + what, () => $"[wait] {what}: chant time {before:0.##} -> {after:0.##} (SNES wait {vanilla:X2} -> {bnw:X2})");
                }
                totals[battleActData.Pointer] = __result;
            }
            catch (Exception e) { Hooks.WarnOnce("WaitGauge.Chant", e); }
        }

        static (int Vanilla, int Bnw, string What) WaitsFor(BattleActData act)
        {
            var w = D.Waits;
            var abilities = act.abilityList;
            var first = abilities != null && abilities.Count > 0 ? abilities[0] : null;
            if (first != null && first.TypeId == w.SummonAbilityType) return (w.SummonVanilla, w.SummonBnw, "Summon");
            int command = act.Command?.Id ?? -1;
            if (w.Commands.TryGetValue(command, out var c))
                return (c.Vanilla, c.Bnw, command == w.FightCommandId ? "Fight" : "command " + command);
            return (0, 0, null);
        }

        // --- the bar ------------------------------------------------------------------------

        static BattleProgressATB Progress() => BattlePlugManager.Instance()?.BattleProgress?.TryCast<BattleProgressATB>();

        // The wait left for a character's chosen action as a fraction (1 = just chosen), or -1.
        static float WaitLeft(BattlePlayerData player)
        {
            var list = Progress()?.chantingInfo;
            if (list == null) return -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                var act = info.actData;
                if (act?.AttackUnitData == null || act.AttackUnitData.Pointer != player.Pointer) continue;
                if (!totals.TryGetValue(act.Pointer, out var total) || total <= 0f) return -1f;
                float time = info.time;
                // the chant clock counts one way or the other: learn it from two readings
                int dir = 0;
                if (clocks.TryGetValue(act.Pointer, out var c))
                    dir = time < c.Last ? -1 : time > c.Last ? 1 : c.Dir;
                clocks[act.Pointer] = (time, dir);
                if (dir == 0) return 1f;
                Hooks.NoteOnce("chant-clock", () => $"[wait] chant clock counts {(dir < 0 ? "down" : "up")}: {time:0.##} of {total:0.##}");
                float left = dir < 0 ? time / total : 1f - time / total;
                return Mathf.Clamp01(left);
            }
            return -1f;
        }

        public static void Atb_Prefix(InfoController __instance, BattlePlayerData player, ref float value)
        {
            if (!Gauge || player == null || !Hooks.IsA<InfoController>(__instance)) return;
            try
            {
                if (value > fullValue) fullValue = value;  // 1 or 100, whichever this gauge uses
                float left = WaitLeft(player);
                if (left < 0f) return;
                value = left * fullValue;
                Hooks.NoteOnce("wait-shown", () => $"[wait] Wait Gauge drawn for {player.GetOwnedCharacterData()?.Name}: {left:P0} left (full bar = {fullValue})");
            }
            catch (Exception e) { Hooks.WarnOnce("WaitGauge.Bar", e); }
        }

        public static void Atb_Postfix(InfoController __instance, BattlePlayerData player)
        {
            if (!Gauge || player == null || !Hooks.IsA<InfoController>(__instance)) return;
            try
            {
                var contents = __instance.contentList;
                if (contents == null) return;
                foreach (var content in contents)
                {
                    if (content?.TargetData == null || content.TargetData.Pointer != player.Pointer) continue;
                    var view = content.view;
                    var color = StatusColor(player);
                    Tint(view?.atbGauge?.gaugeImage, color);
                    Tint(view?.maxGaugeImage, color);
                    break;
                }
            }
            catch (Exception e) { Hooks.WarnOnce("WaitGauge.Color", e); }
        }

        static Color? StatusColor(BattlePlayerData player)
        {
            var conditions = player.BattleUnitDataInfo?.GetConfirmedConditionType();
            if (conditions == null) return null;
            if (conditions.Contains(ConditionType.Stop)) return Stop;
            if (conditions.Contains(ConditionType.Slow)) return Slow;
            if (conditions.Contains(ConditionType.Heist)) return Haste;
            return null;
        }

        static void Tint(UnityEngine.UI.Image image, Color? color)
        {
            if (image == null) return;
            var key = image.Pointer;
            if (!baseColors.TryGetValue(key, out var original))
                baseColors[key] = original = image.color;
            var want = color ?? original;
            if (image.color != want) image.color = want;
        }

        // A new battle: forget the last one's actions.
        static bool inBattle;

        internal static void Track()
        {
            try
            {
                var battle = BattlePlugManager.Instance();
                bool now = battle != null && battle.IsBattle();
                if (now && !inBattle)
                {
                    totals.Clear();
                    clocks.Clear();
                }
                inBattle = now;
            }
            catch (Exception e) { Hooks.WarnOnce("WaitGauge.Track", e); }
        }
    }
}

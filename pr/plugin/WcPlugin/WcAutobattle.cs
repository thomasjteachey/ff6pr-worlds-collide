using System;
using System.Collections.Generic;
using HarmonyLib;

namespace WorldsCollide.PR
{
    // Pixel Remaster treats these as accessory substitutions:
    //   Steal -> Mug, Attack -> Jump, Sketch -> Control, Slot -> Gil Toss,
    //   Magic -> Dualcast.
    // Its autobattle replay therefore accepts the right-hand command only when an
    // equipped item currently produces it. WC can assign every one of those commands
    // directly, so a perfectly valid previous action otherwise becomes Fight.
    static class WcAutobattle
    {
        static readonly int[] SubstitutionCommands = { 10, 11, 1, 30, 18, 19, 20, 21, 7, 31 };
        // the right-hand side: commands PR knows only as an equipment's replacement (Mug, Jump,
        // Control, Gil Toss, Dualcast), which WC can deal a character outright
        static readonly int[] ReplacementCommands = { 11, 30, 19, 21, 31 };
        // Commands that stand in for Fight when WC deals one into the first slot (the user, 2026-11-12:
        // "fix this for all things that might replace Fight"): like Fight, one ability at the enemy and
        // no submenu (command.csv list_type 0, the ability from its ability_id): Steal, Mug, Sketch, Gil
        // Toss, Shock, Possess, Jump. Left to the game's default (Defend): Control (its next turns need
        // the controlled monster's menu), Leap (leaves the fight), Slot, Mimic, Runic, Pray, Trance,
        // and the menu commands.
        internal static readonly int[] FightLikeCommands = { 10, 11, 18, 21, 28, 29, 30 };
        static readonly int[] AllEnemiesCommands = { 21, 28 };     // Gil Toss, Shock: every monster
        static readonly HashSet<string> Logged = new HashSet<string>();
        // test only (WcTest mug_auto, request mug=old): the earlier replay, the remembered act as it was
        internal static bool ReplayAsBefore;

        public static void Patch(Harmony h)
        {
            var method = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "IsUseCommand");
            if (method == null) throw new MissingMethodException("ActSelectAuto.IsUseCommand");
            h.Patch(method, prefix: new HarmonyMethod(typeof(WcAutobattle), nameof(IsUseCommand_Prefix)));
            var ability = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "IsUseAbility");
            if (ability == null) throw new MissingMethodException("ActSelectAuto.IsUseAbility");
            h.Patch(ability, postfix: new HarmonyMethod(typeof(WcAutobattle), nameof(IsUseAbility_Postfix)));
            var system = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "GetAbilitySystemType");
            if (system == null) throw new MissingMethodException("ActSelectAuto.GetAbilitySystemType");
            h.Patch(system, postfix: new HarmonyMethod(typeof(WcAutobattle), nameof(GetAbilitySystemType_Postfix)));
            var force = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "CheckAndForceChangeActData");
            if (force == null) throw new MissingMethodException("ActSelectAuto.CheckAndForceChangeActData");
            h.Patch(force, prefix: new HarmonyMethod(typeof(WcAutobattle), nameof(CheckAndForceChangeActData_Prefix)));
            var create = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "CreateActDataAuto",
                                            new[] { typeof(global::Last.Battle.BattleActData) });
            if (create == null) throw new MissingMethodException("ActSelectAuto.CreateActDataAuto(BattleActData)");
            h.Patch(create, prefix: new HarmonyMethod(typeof(WcAutobattle), nameof(CreateActDataAuto_Prefix)));
            var fallback = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "CheckAndReplaceFightActData");
            if (fallback == null) throw new MissingMethodException("ActSelectAuto.CheckAndReplaceFightActData");
            h.Patch(fallback, postfix: new HarmonyMethod(typeof(WcAutobattle), nameof(CheckAndReplaceFightActData_Postfix)));
            var noTarget = AccessTools.Method(typeof(global::Last.Battle.ActSelectAuto), "IsNotTargetALL");
            if (noTarget == null) throw new MissingMethodException("ActSelectAuto.IsNotTargetALL");
            h.Patch(noTarget, postfix: new HarmonyMethod(typeof(WcAutobattle), nameof(IsNotTargetAll_Postfix)));
            Plugin.L.LogInfo("[wc] autobattle: randomized substitution commands hooked");
        }

        static bool In(int[] set, int id) => Array.IndexOf(set, id) >= 0;
        internal static bool IsFightLike(int id) => In(FightLikeCommands, id);

        internal static bool IsSubstitutionCommand(int id)
        {
            foreach (int candidate in SubstitutionCommands)
                if (candidate == id) return true;
            return false;
        }

        static global::Last.Data.User.OwnedCharacterData Owner(global::Last.Battle.BattleActData act) =>
            act?.AttackUnitData?.GetOwnedCharacterData();

        // Read the live character master row, which is exactly where the active seed installs
        // randomized commands. OwnedCharacterData.CommandList may be a transformed battle clone
        // (and was the reason the first fix did not recognize Gau's directly assigned Mug).
        internal static bool DirectlyAssigned(global::Last.Data.User.OwnedCharacterData owned, int command)
        {
            if (owned == null) return false;
            var row = global::Last.Data.Master.MasterManager.Instance?
                .GetData<global::Last.Data.Master.CharacterStatus>(owned.characterStatusId);
            return row != null && (row.CommandId1 == command || row.CommandId2 == command ||
                                   row.CommandId3 == command || row.CommandId4 == command ||
                                   row.CommandId5 == command || row.CommandId6 == command);
        }

        // Command 26 is PR's hidden Magitek placeholder. It sits between Attack and the
        // character command in ordinary rows and becomes raw slot 1 when WC removes Attack;
        // it is not the first command the player can actually select.
        static int FirstVisibleCommand(global::Last.Data.Master.CharacterStatus row)
        {
            if (row == null) return 0;
            foreach (int id in new[] { row.CommandId1, row.CommandId2, row.CommandId3,
                                       row.CommandId4, row.CommandId5, row.CommandId6 })
                if (id != 0 && id != 26) return id;
            return 0;
        }

        public static bool IsUseCommand_Prefix(global::Last.Battle.BattleActData battleActData, ref bool __result)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || battleActData?.Command == null ||
                    !IsSubstitutionCommand(battleActData.Command.Id)) return true;

                var owned = Owner(battleActData);
                if (!DirectlyAssigned(owned, battleActData.Command.Id)) return true;

                // The character really owns this seed-assigned command. Skip only PR's
                // vanilla equipment-pair check; CreateActDataAuto still runs IsUseAbility,
                // target validation, MP/item checks, and the rest of normal replay.
                __result = true;
                string key = $"command:{owned.characterStatusId}:{battleActData.Command.Id}";
                if (Logged.Add(key))
                    Plugin.L.LogInfo($"[wc] autobattle: accepted seed command {battleActData.Command.Id} for character {owned.characterStatusId}");
                return false;
            }
            catch (Exception e)
            {
                WcRunner.WarnOnce("autobattle-command", e);
                return true;
            }
        }

        public static void IsUseAbility_Postfix(global::Last.Battle.ActSelectAuto __instance,
                                                global::Last.Battle.BattleActData battleActData, bool __result)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || battleActData?.Command == null ||
                    !IsSubstitutionCommand(battleActData.Command.Id)) return;
                var owned = Owner(battleActData);
                int ability = battleActData.abilityList != null && battleActData.abilityList.Count > 0
                    ? battleActData.abilityList[0]?.Id ?? 0 : 0;
                Note(owned?.characterStatusId ?? 0, $"replay of command {battleActData.Command.Id} {(__result ? "accepted" : "turned down: the default act instead")}");
                string key = $"ability:{owned?.characterStatusId ?? 0}:{battleActData.Command.Id}:{ability}:{__result}";
                if (Logged.Add(key))
                {
                    // what turned it down (ActSelectAuto.IsUseAbility: the ability owned or on a
                    // command, then the command usable, then the act playable)
                    string parts = "";
                    if (!__result)
                    {
                        bool magic = __instance != null && __instance.IsHaveMagic(battleActData);
                        bool play = global::Last.Battle.BattleUtility.IsPlayAct(battleActData, true);
                        var cmds = owned?.CommandList;
                        var ids = new List<string>();
                        for (int i = 0; cmds != null && i < cmds.Count; i++) ids.Add((cmds[i]?.Id ?? 0).ToString());
                        parts = $" (has the ability {magic}, playable {play}, use type {(int)battleActData.CurrentUseType}, " +
                                $"commands [{string.Join(" ", ids)}])";
                    }
                    Plugin.L.LogInfo($"[wc] autobattle: ability validation command {battleActData.Command.Id}, ability {ability}, " +
                                     $"character {owned?.characterStatusId ?? 0}: {__result}{parts}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("autobattle-ability", e); }
        }

        // Before it reaches IsUseCommand/IsUseAbility, PR drops a replay whose ability SystemId
        // is 3 or 5 (Mug is 3) when IsNotTargetALL holds, and the unit waits. The earlier patch
        // let WC's Mug past that every time. Decompiled (2026-11-12), IsNotTargetALL is "every
        // monster of the fight has a condition that makes it unattackable" (Wound, hidden: the
        // Condition table's attack_target 1 or 3), so the bypass now applies only while some
        // monster can still be mugged; otherwise Gau waits as the game's own replays do.
        public static void GetAbilitySystemType_Postfix(global::Last.Battle.BattleActData __0,
                                                         ref global::Last.Defaine.Master.AbilitySystemType __result)
        {
            try
            {
                int system = (int)__result;
                int cmd = __0?.Command?.Id ?? 0;
                if (!Plugin.WcMode || WcSeeds.Active == null || (system != 3 && system != 5) || !IsFightLike(cmd)) return;
                var owned = Owner(__0);
                if (!DirectlyAssigned(owned, cmd) || !AnyMuggable()) return;
                int original = system;
                __result = (global::Last.Defaine.Master.AbilitySystemType)0;
                string key = $"system:{owned.characterStatusId}:{cmd}:{original}";
                if (Logged.Add(key))
                    Plugin.L.LogInfo($"[wc] autobattle: bypassed command {cmd}'s target shortcut for character {owned.characterStatusId} (system {original})");
            }
            catch (Exception e) { WcRunner.WarnOnce("autobattle-system", e); }
        }

        // PR applies battle-state forced substitutions before its autobattle validators. That
        // path knows Mug only as Steal's equipment replacement and can discard/replace a Mug
        // that WC assigned as the character's real command. Preserve that recorded act; the
        // normal command, ability, ownership and target validation immediately afterward still
        // decides whether it can execute.
        public static bool CheckAndForceChangeActData_Prefix(global::Last.Battle.BattleActData __0,
                                                              ref global::Last.Battle.BattleActData __result)
        {
            try
            {
                int cmd = __0?.Command?.Id ?? 0;
                if (!Plugin.WcMode || WcSeeds.Active == null || !In(ReplacementCommands, cmd)) return true;
                var owned = Owner(__0);
                if (!DirectlyAssigned(owned, cmd)) return true;
                __result = __0;
                string key = $"force:{owned.characterStatusId}:{cmd}";
                if (Logged.Add(key))
                    Plugin.L.LogInfo($"[wc] autobattle: preserved seed-assigned command {cmd}'s act for character {owned.characterStatusId}");
                return false;
            }
            catch (Exception e)
            {
                WcRunner.WarnOnce("autobattle-force", e);
                return true;
            }
        }

        // The private overload is the final step after IsUseCommand and IsUseAbility have both
        // accepted the replay; it is where the game picks the replay's targets (ActUtility
        // .GetDefaultTarget) and builds a fresh act. The patch builds WC's Mug here itself, as the
        // game does, but never at a stale target.
        public static bool CreateActDataAuto_Prefix(global::Last.Battle.ActSelectAuto __instance,
                                                     global::Last.Battle.BattleActData __0,
                                                     ref global::Last.Battle.BattleActData __result)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || __0?.Command?.Id != 11) return true;
                var owned = Owner(__0);
                if (!DirectlyAssigned(owned, 11)) return true;
                if (ReplayAsBefore && WcTest.Running) { __result = __0; return false; }
                var made = ReplayMug(__instance, __0, owned.characterStatusId);
                if (made == null) return true;   // no enemy can be mugged now: the game's own replay
                __result = made;
                return false;
            }
            catch (Exception e)
            {
                WcRunner.WarnOnce("autobattle-create", e);
                return true;
            }
        }

        // A replayed Mug, rebuilt as the game rebuilds a replay (ActSelectAuto.CreateActDataAuto:
        // a new act from this battle's unit, IsAutoBattleAct set). Its target is the remembered one
        // only while that enemy is still in this fight and can be targeted; otherwise a random enemy
        // that can (the game's own pick, BattleUtility.RandomEnemyTarget).
        // The user's seed gaadl68ix7j4 (2026-11-12, Gau, "sits there with a full ATB bar, sometimes
        // it works"): the earlier patch reused the remembered act as it was. Each new battle starts
        // its replay from the last battle's act (CommandHistoryData), aimed at that battle's
        // monster; the game re-aims it (IsPrevBattle -> PrevTargetChange -> GetDefaultTarget) only
        // in the step this patch replaces, so Gau's Mug went at a monster no longer there and never
        // ran. The same for a Mug at a monster that died. A self-aimed Mug (the first-slot default
        // once copied the game's Defend on itself) is re-aimed the same way.
        internal static global::Last.Battle.BattleActData ReplayMug(global::Last.Battle.ActSelectAuto auto,
                                                                     global::Last.Battle.BattleActData act, int who)
        {
            var foes = global::Last.Battle.BattlePlugManager.Instance()?.GetEnemyUnits();
            var kept = new List<global::Last.Battle.BattleUnitData>();
            var old = act.TargetUnitDatas;
            for (int i = 0; old != null && i < old.Length; i++)
                if (Muggable(old[i], foes)) kept.Add(old[i]);
            var type = act.CurrentTargetType;
            string why = "kept its target";
            if (kept.Count == 0)
            {
                var pick = foes == null || foes.Length == 0 ? null : global::Last.Battle.BattleUtility.RandomEnemyTarget(foes);
                if (!Muggable(pick, foes)) return null;
                kept.Add(pick);
                type = (global::Last.Battle.BattleActData.TargetType)3;
                why = old == null || old.Length == 0 ? "had no target: a random enemy"
                    : old[0]?.TryCast<global::Last.Battle.BattleEnemyData>() == null ? "was aimed at its own side: a random enemy"
                    : "was aimed at a monster no longer in the fight: a random enemy";
            }
            var attacker = auto?.BattleUnitData ?? act.AttackUnitData;
            var items = act.itemList ?? new Il2CppSystem.Collections.Generic.List<global::Last.Data.User.OwnedItemData>();
            var made = new global::Last.Battle.BattleActData(
                attacker, kept.ToArray(), act.Command, act.abilityList, items, act.CurrentActType, type,
                act.CurrentUseType, (global::Last.Battle.BattleActData.ArmType)0,
                (global::Last.Battle.BattleActData.ActAbilityType)0, Math.Max(1, act.continuousCommandCount));
            made.IsSlotAct = act.IsSlotAct;
            made.IsAutoBattleAct = true;
            if (auto != null) auto.isDefaultTarget = false;   // the game clears it once it has picked
            Note(who, $"Mug replayed ({why})");
            if (Logged.Add($"replay:{who}:{why}"))
                Plugin.L.LogInfo($"[wc] autobattle: character {who}'s replayed Mug {why}");
            return made;
        }

        static bool AnyMuggable()
        {
            var foes = global::Last.Battle.BattlePlugManager.Instance()?.GetEnemyUnits();
            for (int i = 0; foes != null && i < foes.Length; i++)
                if (Muggable(foes[i], foes)) return true;
            return false;
        }

        // a monster of this fight that can be targeted (not dead, petrified, gone or hidden)
        internal static bool Muggable(global::Last.Battle.BattleUnitData unit,
                                      Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<global::Last.Battle.BattleEnemyData> foes)
        {
            if (unit == null || foes == null || unit.TryCast<global::Last.Battle.BattleEnemyData>() == null) return false;
            bool here = false;
            for (int i = 0; !here && i < foes.Length; i++)
                here = foes[i] != null && foes[i].Pointer == unit.Pointer;
            if (!here || global::Last.Battle.BattleUtility.IsNotTargeting(unit)) return false;
            var p = unit.BattleUnitDataInfo?.Parameter;
            if (p == null || p.CurrentHP <= 0) return false;
            var conditions = p.CurrentConditionList;
            for (int i = 0; conditions != null && i < conditions.Count; i++)
            {
                int type = (int)(conditions[i]?.ConditionType ?? 0);
                if (type == 5 || type == 11) return false;   // Wound / Petrify
            }
            return true;
        }

        // With no remembered action, PR always creates Fight and only checks its built-in
        // equipment substitutions. WC replaces the character's first/Fight slot outright, so
        // Gau with Mug there must default to Mug even before a Mug has ever been selected.
        // The Mug is built from the game's own default Fight (__0: Attack at a random enemy,
        // ActSelectAuto.CreateFightActData), never from what the game made of it: a character
        // without the Fight command gets Defend on itself there (CheckAndReplaceFightActData:
        // ability 2, its own unit as the target), and a Mug copied from that act hit its own
        // user (the user's seed gaadl68ix7j4, 2026-11-11: Gau mugged himself). Magitek armor's
        // default (Fire Beam) and the Cultists' Tower's (Defend: magic only) aren't Attack and stay.
        public static void CheckAndReplaceFightActData_Postfix(global::Last.Battle.BattleActData __0,
                                                                ref global::Last.Battle.BattleActData __result)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || __result == null || __0 == null) return;
                var owned = Owner(__0) ?? Owner(__result);
                var row = owned == null ? null : global::Last.Data.Master.MasterManager.Instance?
                    .GetData<global::Last.Data.Master.CharacterStatus>(owned.characterStatusId);
                int cmdId = FirstVisibleCommand(row);
                if (!IsFightLike(cmdId)) return;
                int first = __0.abilityList != null && __0.abilityList.Count > 0 ? __0.abilityList[0]?.Id ?? 0 : 0;
                if (first != 1) return;
                var targets = __0.TargetUnitDatas;
                bool enemies = targets != null && targets.Length > 0;
                for (int i = 0; enemies && i < targets.Length; i++)
                    enemies = targets[i]?.TryCast<global::Last.Battle.BattleEnemyData>() != null;
                if (!enemies)
                {
                    string none = $"none:{owned.characterStatusId}";
                    if (Logged.Add(none))
                        Plugin.L.LogInfo($"[wc] autobattle: no enemy for character {owned.characterStatusId}'s command {cmdId}: the game's default stays");
                    return;
                }
                if (In(AllEnemiesCommands, cmdId))
                {
                    var foes = global::Last.Battle.BattlePlugManager.Instance()?.GetEnemyUnits();
                    var all = new List<global::Last.Battle.BattleUnitData>();
                    for (int i = 0; foes != null && i < foes.Length; i++)
                        if (Muggable(foes[i], foes)) all.Add(foes[i]);
                    if (all.Count > 0) targets = all.ToArray();
                }

                var masters = global::Last.Data.Master.MasterManager.Instance;
                var command = masters?.GetData<global::Last.Data.Master.Command>(cmdId);
                var ability = command == null ? null : masters.GetData<global::Last.Data.Master.Ability>(command.AbilityId);
                if (command == null || ability == null) return;
                var abilities = new Il2CppSystem.Collections.Generic.List<global::Last.Data.Master.Ability>();
                abilities.Add(ability);
                __result = new global::Last.Battle.BattleActData(
                    __0.AttackUnitData, targets, command, abilities,
                    __0.CurrentActType, __0.CurrentTargetType, __0.UseArmType,
                    __0.UseActAbilityType, 1, false);
                Note(owned.characterStatusId, $"default command {cmdId} (empty history or a replay turned down)");
                string key = $"fallback:{owned.characterStatusId}:{cmdId}";
                if (Logged.Add(key))
                    Plugin.L.LogInfo($"[wc] autobattle: defaulted character {owned.characterStatusId}'s empty history to its first-slot command {cmdId} (ability {ability.Id})");
            }
            catch (Exception e) { WcRunner.WarnOnce("autobattle-fallback", e); }
        }

        // Empty history is checked for "no target" before PR creates its default Fight act, and
        // the earlier patch saw it turn WC's first-slot Mug away. It reads the status info's
        // monster list (BattleStatusControlInfo.BattleEnemyDatas); the override stays, but only
        // while a monster of the fight (BattlePlugManager's list) can be mugged: with every
        // monster down or hidden the character waits, as the game's own replays do.
        public static void IsNotTargetAll_Postfix(global::Last.Battle.ActSelectAuto __instance, ref bool __result)
        {
            try
            {
                if (!__result || !Plugin.WcMode || WcSeeds.Active == null) return;
                var owned = __instance?.BattleUnitData?.GetOwnedCharacterData();
                var row = owned == null ? null : global::Last.Data.Master.MasterManager.Instance?
                    .GetData<global::Last.Data.Master.CharacterStatus>(owned.characterStatusId);
                int cmdId = FirstVisibleCommand(row);
                if (!IsFightLike(cmdId) || !AnyMuggable()) return;
                __result = false;
                string key = $"target:{owned.characterStatusId}:{cmdId}";
                if (Logged.Add(key))
                    Plugin.L.LogInfo($"[wc] autobattle: allowed first-slot command {cmdId} through empty-history target selection for character {owned.characterStatusId}");
            }
            catch (Exception e) { WcRunner.WarnOnce("autobattle-empty-target", e); }
        }

        // --- the stall watch ---------------------------------------------------------------
        // The user (2026-11-12, Gau with Mug on auto): "he just sits there with a full ATB bar
        // (sometimes it works)". Not reproduced headless (WcTest mug_auto: the old replay kept
        // mugging across battles and after its targets died), so the plugin watches for it: a party
        // member on auto whose gauge has stayed full for 4 s with no act queued or playing gets one
        // log line per battle with where it waits and what the auto-battle last did for it.

        static readonly Dictionary<int, string> lastPath = new Dictionary<int, string>();
        static readonly Dictionary<IntPtr, float> fullSince = new Dictionary<IntPtr, float>();
        static readonly HashSet<IntPtr> reported = new HashSet<IntPtr>();
        static IntPtr watchedBattle;
        static float nextWatch;

        static void Note(int who, string what)
        {
            if (who > 0) lastPath[who] = $"{what} at {UnityEngine.Time.realtimeSinceStartup:0.0} s";
        }

        internal static void Watch()
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < nextWatch) return;
                nextWatch = now + 0.5f;
                var bm = global::Last.Battle.BattlePlugManager.Instance();
                var ui = global::Last.UI.BattleUIManager.Instance;
                if (bm == null || !bm.IsBattle() || ui == null || !ui.IsAuto()) { fullSince.Clear(); return; }
                var atb = bm.BattleProgress?.TryCast<global::Last.Battle.BattleProgressATB>();
                var exe = bm.BattleActExection;
                var players = bm.GetPlayerUnits();
                if (atb == null || players == null) return;
                // a new battle: report again (the manager stays; its monster list changes)
                var foes = bm.GetEnemyUnits();
                var battleKey = foes != null && foes.Length > 0 && foes[0] != null ? foes[0].Pointer : IntPtr.Zero;
                if (battleKey != watchedBattle) { watchedBattle = battleKey; reported.Clear(); fullSince.Clear(); }
                foreach (var p in players)
                {
                    if (p == null) continue;
                    var pr = p.BattleUnitDataInfo?.Parameter;
                    bool busy = exe != null && (exe.IsStackAct(p) || exe.IsStagingByUnit(p));
                    bool chanting = atb.IsChantTimeActDataByUnit(p);
                    float gauge = atb.GetAtbGaugeByUnitData(p);
                    bool waiting = gauge >= 100 && !busy && !chanting && pr != null && pr.CurrentHP > 0 && p.IsActable();
                    if (!waiting) { fullSince.Remove(p.Pointer); continue; }
                    if (!fullSince.TryGetValue(p.Pointer, out var since)) { fullSince[p.Pointer] = now; continue; }
                    if (now - since < 4 || !reported.Add(p.Pointer)) continue;
                    var owned = p.GetOwnedCharacterData();
                    int who = owned?.characterStatusId ?? 0;
                    string history = "none";
                    var acts = owned?.CommandHistoryData?.ActDataList;
                    if (acts != null && acts.Count > 0 && acts[0] != null)
                    {
                        var a = acts[0];
                        var t = a.TargetUnitDatas;
                        var ts = new List<string>();
                        for (int i = 0; t != null && i < t.Length; i++)
                            ts.Add(t[i] == null ? "null" : t[i].TryCast<global::Last.Battle.BattleEnemyData>() == null ? "ally"
                                   : Muggable(t[i], foes) ? "monster" : "monster gone/down");
                        int ab = a.abilityList != null && a.abilityList.Count > 0 ? a.abilityList[0]?.Id ?? 0 : 0;
                        history = $"command {a.Command?.Id ?? 0} ability {ab} at [{string.Join(", ", ts)}], target type {(int)a.CurrentTargetType}";
                    }
                    var conds = new List<string>();
                    var list = pr.CurrentConditionList;
                    for (int i = 0; list != null && i < list.Count; i++) conds.Add(((int)(list[i]?.ConditionType ?? 0)).ToString());
                    Plugin.L.LogInfo($"[wc] autobattle: character {who} has sat {now - since:0} s with a full gauge ({gauge:0}) and nothing queued: " +
                                     $"the game asks it for an act: {atb.IsActSelectCheck(p)}; selected count {p.SelectedCount}; conditions [{string.Join(" ", conds)}]; " +
                                     $"last auto act: {history}; last auto-battle path: {(lastPath.TryGetValue(who, out var path) ? path : "none this session")}; " +
                                     $"muggable monsters {AnyMuggable()}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("autobattle-watch", e); }
        }
    }
}

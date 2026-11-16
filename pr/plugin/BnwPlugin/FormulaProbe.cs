using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Last.Battle;
using Last.Battle.Function;
using Last.Data;
using Last.Data.Master;
using Last.Data.User;
using Last.Defaine;
using Last.Defaine.Master;
using Last.Defaine.User;
using Last.Management;
using Last.Systems;
using UnityEngine;
using Object = UnityEngine.Object;
using TargetType = Last.Battle.BattleActData.TargetType;
using Il2CppIntMap = Il2CppSystem.Collections.Generic.Dictionary<int, int>;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Automated test of the game's damage calculations, no playing needed.
    //
    // Requested by the file BepInEx/bnw_probe.request (optional key=value lines: party,
    // weapon, samples, quit). On the next launch the plugin deletes the request, loads the
    // latest save, starts a battle against one weak monster and, for every ability whose
    // function group has a damage or heal function, builds the game's own calculation object
    // (ParameterActFunctionManagment.GetFunctionBaseCalc) and runs FunctionBase.MainCalc
    // between a character and the monster, both ways, over a grid of attacker/target stats.
    // The stats are set through the Confirmed* getters (postfixes that only answer during a
    // probe call); the values actually seen are recorded too. BNW's own damage hooks stay on,
    // so the results are what a player would get. Output: BepInEx/bnw_probe.csv, then the
    // game quits. tools/probe_check.py compares the file with BNW's formulas.
    static class FormulaProbe
    {
        internal static bool Active;   // stat overrides answer (inside a probe call)
        internal static bool Running;  // a probe run is in progress

        enum Step { Off, Title, Field, Battle, Probing, Shop, Learn, Win, NewGame, Done }
        static Step step = Step.Off;
        static float stepSince, startedAt, titleSince = -1, lastTry;
        static bool loadCalled;
        static int encounterTries;

        static string requestPath, outPath;
        static readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
        static int partyId = 7, weaponId = 9, samples = 8;
        static bool quit = true;

        public static void Init(Harmony h)
        {
            var root = BepInEx.Paths.BepInExRootPath;
            requestPath = Path.Combine(root, "bnw_probe.request");
            outPath = Path.Combine(root, "bnw_probe.csv");
            if (!File.Exists(requestPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(requestPath))
                {
                    var kv = line.Split('=');
                    if (kv.Length == 2) Settings[kv[0].Trim()] = kv[1].Trim();
                }
                File.Delete(requestPath);  // one run only, even if this one fails
            }
            catch (Exception e) { Plugin.L.LogWarning($"[probe] request: {e.Message}"); }
            partyId = Int("party", partyId);
            weaponId = Int("weapon", weaponId);
            samples = Math.Max(1, Int("samples", samples));
            quit = Int("quit", 1) != 0;
            if (Settings.TryGetValue("boxcheck", out var bx) && bx.Split(':').Length == 3)
            {
                Settings["shopcheck"] = bx;
                boxCheck = true;
            }
            if (Settings.TryGetValue("shopcheck", out var sc) && sc.Split(':').Length == 3)
            {
                shopCheck = sc;
                var msg = AccessTools.Method(typeof(Last.Interpreter.Instructions.Message), "Msg");
                if (msg != null) h.Patch(msg, prefix: new HarmonyMethod(typeof(FormulaProbe), nameof(Msg_Prefix)));
                Plugin.L.LogInfo($"[probe] shop check requested: {sc}");
            }
            PatchOverrides(h);
            PatchNoSaves(h);
            bool harness = Settings.TryGetValue("harness", out var hsv) && hsv == "1";
            bool newGameCheck = (Settings.TryGetValue("newgamecheck", out var ngv) && ngv == "1") || harness;
            if (newGameCheck)
            {
                newGame = !harness;
                var msgOp = AccessTools.Method(typeof(Last.Interpreter.Instructions.Message), "Msg");
                if (msgOp != null) h.Patch(msgOp, prefix: new HarmonyMethod(typeof(FormulaProbe), nameof(Msg_Prefix)));
                Plugin.L.LogInfo("[probe] new game check requested: a new game, what each character gets");
            }
            if ((Settings.TryGetValue("battlecheck", out var bcv) && bcv == "1") || newGameCheck)
            {
                battleCheck = !newGameCheck;
                foreach (var (type, name) in new[] { (typeof(OwnedCharacterClient), "AddAbility"), (typeof(OwnedCharacterClient), "LearningAbility"),
                                                     (typeof(OwnedCharacterClient), "UpdateOwnedMagicFromHisotry"), (typeof(UserDataClient), "AddOwnedMagic") })
                {
                    var m = AccessTools.Method(type, name);
                    if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(FormulaProbe), nameof(Taught_Prefix)));
                    else Plugin.L.LogInfo($"[probe] battle check: {type.Name}.{name} not found");
                }
                Plugin.L.LogInfo("[probe] battle check requested: a battle won, the abilities it teaches");
            }
            if (Settings.TryGetValue("learncheck", out var lc))
            {
                learnCheck = lc;
                Plugin.L.LogInfo($"[probe] learn check requested: {lc}");
            }
            Mute();
            Running = true;
            step = Step.Title;
            if (harness)
            {
                // the harness plays: no automatic steps (its commands load or start a game)
                step = Step.Off;
                GameHarness.Start(h);
            }
            Plugin.L.LogInfo($"[probe] requested: formation {partyId}, weapon {weaponId}, {samples} samples per case, " +
                             (quit ? "quits when done" : "stays open"));
        }

        static int Int(string key, int fallback) =>
            Settings.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

        // --- run steps (called every frame) ---------------------------------------------

        // silent run: the game's sound engine (SEAD) and Unity's own audio at volume 0, every
        // frame (the game sets its volume from the config while loading); the config is untouched
        // (the modding kit's TestRun.Mute since 2026-11-11)
        static void Mute() => PrKit.TestRun.Mute();

        public static void Update()
        {
            if (GameHarness.Active)
            {
                Mute();
                try { GameHarness.Update(); } catch (Exception e) { Plugin.L.LogWarning($"[harness] update: {e.Message}"); }
            }
            if (step == Step.Off) return;
            Mute();
            if (step == Step.Done) return;
            float now = Time.realtimeSinceStartup;
            if (startedAt == 0) startedAt = now;
            if (now - startedAt > 900) { Finish($"timed out at step {step}"); return; }
            try
            {
                switch (step)
                {
                    case Step.Title: StepTitle(now); break;
                    case Step.Field: StepField(now); break;
                    case Step.Battle: StepBattle(now); break;
                    case Step.Probing: StepProbe(); break;
                    case Step.Shop: StepShop(now); break;
                    case Step.Learn: StepLearn(now); break;
                    case Step.Win: StepWin(now); break;
                    case Step.NewGame: StepNewGame(now); break;
                }
            }
            catch (Exception e) { Finish($"step {step} failed: {e}"); }
        }

        static void Go(Step s)
        {
            step = s;
            stepSince = Time.realtimeSinceStartup;
            Plugin.L.LogInfo($"[probe] step: {s}");
        }

        static bool OnField() => Object.FindObjectOfType<FieldMap>()?.fieldController != null;

        static void StepTitle(float now)
        {
            if (OnField()) { Go(Step.Field); return; }
            var mm = MasterManager.Instance;
            if (mm == null || !mm.isInitialized || loadCalled) return;
            bool title = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>() != null ||
                         Object.FindObjectOfType<Last.UI.Touch.TitleWindowController>() != null;
            if (title && titleSince < 0) titleSince = now;
            // load 3 s after the title shows, or after 45 s anyway (a title we don't recognise)
            if (!(title && now - titleSince >= 3) && now - startedAt < 45) return;
            if (newGame)
            {
                // the title's own New Game command (WcTest's way: TitleWindowController.SetCommand)
                var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
                var set = typeof(Last.UI.KeyInput.TitleWindowController).GetMethod("SetCommand",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tw == null || set == null) { Finish("new game: no title window / SetCommand"); return; }
                var ps = set.GetParameters();
                loadCalled = true;
                Plugin.L.LogInfo("[probe] new game chosen");
                set.Invoke(tw, new object[] { Last.Defaine.TitleCommandId.NewGame, Enum.ToObject(ps[1].ParameterType, 2), false, true });
                return;
            }
            var mgr = SaveSlotManager.Instance();
            var slot = mgr?.GetLatestSaveSlotData(false, false);
            if (slot == null) { Finish("no save to load"); return; }
            loadCalled = true;
            Plugin.L.LogInfo("[probe] loading the latest save");
            mgr.GotoLoadSaveData(slot);
        }

        // --- no saves in a test run -------------------------------------------------------
        // A run loads the player's latest save and must never write one: a warp's map change
        // autosaved (the shop/box checks of 2026-11-07 wrote the PC's slot 21), and the harness's
        // first battle wrote the bestiary. Since 2026-11-11 the modding kit's TestRun.BlockSaves does
        // it: every SaveSlotManager.Save overload (the callback one hears "done"), a script's
        // AutoSave, the bestiary's and the config's own Save ("[test] ... skipped (test run)" lines).
        // Without the slot-save hooks the run quits rather than risk writing one.
        static void PatchNoSaves(Harmony h)
        {
            if (PrKit.TestRun.BlockSaves(h) > 0) return;
            Plugin.L.LogError("[probe] saves couldn't be blocked: quitting rather than risk writing one");
            Application.Quit();
        }

        // --- learn check (request "learncheck=<exp>:<ap>:<magic points>", instead of the probe) ------------------
        // What the party learns after a battle: the game's own battle result (BattleResultProvider
        // .Genelate: level-ups, magicite AP) for the loaded save's party, with the given EXP and
        // AP and an empty battle log; logs each member's abilities before and after. The result
        // is computed, not applied (and nothing is saved).
        static string learnCheck;

        static string Known(OwnedCharacterData c, out string progress)
        {
            var known = new List<int>();
            var partial = new List<string>();
            if (c?.OwnedAbilityList != null)
                foreach (var a in c.OwnedAbilityList)
                {
                    if (a?.Ability == null) continue;
                    if (a.SkillLevel >= 100) known.Add(a.Ability.Id);
                    else if (a.SkillLevel > 0) partial.Add($"{a.Ability.Id}:{a.SkillLevel}");
                }
            known.Sort();
            progress = string.Join(" ", partial);
            return $"{known.Count} known [{string.Join(" ", known)}]";
        }

        static void StepLearn(float now)
        {
            if (now - stepSince < 8 || !OnField()) return;
            var udm = UserDataManager.Instance();
            var party = udm?.GetPartyMemberData();
            if (party == null || party.Count == 0) { Finish("learn check: no party"); return; }
            var parts = learnCheck.Split(':');
            int exp = parts.Length > 0 && int.TryParse(parts[0], out var e1) ? e1 : 0;
            int ap = parts.Length > 1 && int.TryParse(parts[1], out var a1) ? a1 : 0;
            int mpt = parts.Length > 2 && int.TryParse(parts[2], out var m1) ? m1 : 0;
            foreach (var c in party)
                Plugin.L.LogInfo($"[probe] learn check: before: {c.Name} (status {c.characterStatusId}, job {c.JobId}, lv {c.Parameter.ConfirmedLevel()}, " +
                                 $"magicite {c.MagicStoneId}, learning value {c.MagicLearningValue}, commands [{string.Join(" ", Commands(c))}]): {Known(c, out var pr)}; in progress [{pr}]");
            // the party's magic list (UserDataManager.OwnedMagicList: content ids the game gives every
            // character after each won battle, OwnedCharacterClient.UpdateOwnedMagicFromHisotry)
            var magic = new List<int>();
            if (udm.OwnedMagicList != null) foreach (var id in udm.OwnedMagicList) magic.Add(id);
            magic.Sort();
            Plugin.L.LogInfo($"[probe] learn check: the party's magic list: {magic.Count} [{string.Join(" ", magic)}]");
            try
            {
                new OwnedCharacterClient().UpdateOwnedMagicFromHisotry();
                foreach (var c in party)
                    Plugin.L.LogInfo($"[probe] learn check: after the won-battle magic update: {c.Name}: {Known(c, out var pr2)}; in progress [{pr2}]");
            }
            catch (Exception ex) { Plugin.L.LogInfo($"[probe] learn check: magic update failed: {ex.Message}"); }
            foreach (var (label, e, a, mg) in new[] { ("EXP", exp, 0, 0), ("AP", 0, ap, 0), ("magic points", 0, 0, mpt), ("all", exp, ap, mpt) })
            {
                if (e == 0 && a == 0 && mg == 0) continue;
                var targets = new Il2CppSystem.Collections.Generic.List<OwnedCharacterData>();
                foreach (var c in party) targets.Add(c);
                var result = new Last.Systems.BattleResultProvider().Genelate(
                    targets.Cast<Il2CppSystem.Collections.Generic.IEnumerable<OwnedCharacterData>>(), e, 0,
                    new Il2CppSystem.Collections.Generic.List<DropItemData>(),
                    new BattleSkillUpInformation(new Il2CppSystem.Collections.Generic.List<BattleActLogData>(), 1, a, mg));
                foreach (var r in result.CharacterList)
                {
                    string before = Known(r.BeforData, out _), after = Known(r.AfterData, out var prog);
                    var learned = new List<int>();
                    if (r.LearningList != null) foreach (var id in r.LearningList) learned.Add(id);
                    var gained = new List<int>();
                    if (r.AfterData?.OwnedAbilityList != null)
                        foreach (var x in r.AfterData.OwnedAbilityList)
                        {
                            if (x?.Ability == null || x.SkillLevel < 100) continue;
                            bool had = false;
                            if (r.BeforData?.OwnedAbilityList != null)
                                foreach (var y in r.BeforData.OwnedAbilityList)
                                    if (y?.Ability != null && y.Ability.Id == x.Ability.Id && y.SkillLevel >= 100) had = true;
                            if (!had) gained.Add(x.Ability.Id);
                        }
                    Plugin.L.LogInfo($"[probe] learn check: {label} {e}/{a}/{mg}: {r.AfterData?.Name ?? r.BeforData?.Name} level up {r.IsLevelUp} " +
                                     $"(lv {r.BeforData?.Parameter?.ConfirmedLevel()} -> {r.AfterData?.Parameter?.ConfirmedLevel()}); learning list [{string.Join(" ", learned)}]; " +
                                     $"gained {gained.Count} [{string.Join(" ", gained)}]; after: {after}; in progress [{prog}]");
                }
            }
            Finish("learn check done");
        }

        static IEnumerable<int> Commands(OwnedCharacterData c)
        {
            if (c?.CommandList == null) yield break;
            foreach (var x in c.CommandList) if (x != null) yield return x.Id;
        }

        // --- new game check (request "newgamecheck=1", instead of the probe) ----------------------
        // A new game from the title (its scenes run with their lines passed), the owned
        // characters' abilities logged whenever they change for 3 minutes (the opening's battles
        // stall headless: no input), and every ability the game adds (the battle check's hooks).
        static bool newGame;
        static string lastOwned = "";
        static float ngLastDump;

        static string OwnedAbilities()
        {
            var list = UserDataManager.Instance()?.OwnedCharacterList;
            if (list == null) return "no characters";
            var parts = new List<string>();
            foreach (var c in list)
                if (c != null) parts.Add($"{c.Name} ({c.characterStatusId}, lv {c.Parameter.ConfirmedLevel()}): {Known(c, out var pr)}{(pr.Length > 0 ? $"; in progress [{pr}]" : "")}");
            var magic = new List<int>();
            var om = UserDataManager.Instance()?.OwnedMagicList;
            if (om != null) foreach (var id in om) magic.Add(id);
            magic.Sort();
            return string.Join(" | ", parts) + $" || party magic list {magic.Count} [{string.Join(" ", magic)}]";
        }

        static void StepNewGame(float now)
        {
            SkipMessages = true;
            if (now - ngLastDump >= 3)
            {
                ngLastDump = now;
                var cur = OwnedAbilities();
                if (cur != lastOwned)
                {
                    var b = BattlePlugManager.Instance();
                    Plugin.L.LogInfo($"[probe] new game check: {now - stepSince:0} s, map {CurrentMap()}{(b != null && b.IsBattle() ? ", in battle" : "")}: {cur}");
                    lastOwned = cur;
                }
            }
            if (now - stepSince > 180) Finish("new game check done");
        }

        // --- battle check (request "battlecheck=1", instead of the probe) ---------------------
        // A battle from the loaded save's field, won (every enemy knocked out as the game does it:
        // HP 0 + UnableFight), then the game's own end of battle and result: logs every ability
        // the game adds to anyone meanwhile (OwnedCharacterClient.AddAbility / LearningAbility,
        // UserDataClient.AddOwnedMagic) and each party member's abilities before and after.
        static bool battleCheck;
        static int winPhase, taughtLogged;
        static bool judged;
        static float winAt, lastDump;
        static string lastParty = "";

        public static void Taught_Prefix(MethodBase __originalMethod, object[] __args)
        {
            if (++taughtLogged > 200) return;
            Plugin.L.LogInfo($"[probe] battle check: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({string.Join(", ", __args ?? new object[0])})");
        }

        static string PartyAbilities()
        {
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            if (party == null) return "no party";
            var parts = new List<string>();
            foreach (var c in party) parts.Add($"{c.Name} (lv {c.Parameter.ConfirmedLevel()}, magicite {c.MagicStoneId}): {Known(c, out var pr)}{(pr.Length > 0 ? $"; in progress [{pr}]" : "")}");
            return string.Join(" | ", parts);
        }

        static void StepWin(float now)
        {
            var battle = BattlePlugManager.Instance();
            bool inBattle = battle != null && battle.IsBattle();
            switch (winPhase)
            {
                case 0:
                    lastParty = PartyAbilities();
                    Plugin.L.LogInfo($"[probe] battle check: before: {lastParty}");
                    var cc = battle?.BattleConditionController;
                    int n = 0;
                    foreach (var u in battle.GetEnemyUnits())
                    {
                        var p = u?.BattleUnitDataInfo?.Parameter;
                        if (p == null) continue;
                        p.CurrentHP = 0;
                        cc?.InterruptAddCondition(u, ConditionType.UnableFight);
                        n++;
                    }
                    Plugin.L.LogInfo($"[probe] battle check: {n} enemies knocked out");
                    winPhase = 1;
                    winAt = now;
                    return;
                case 1 when now - winAt >= 4 && !judged:
                    // the game judges the end after an act; none comes (no input): its own judgment,
                    // once the knockouts (queued conditions) are in; Force() if it still says no
                    judged = true;
                    try
                    {
                        var judge = battle?.BattleEndJugment;
                        if (judge == null) { Plugin.L.LogInfo("[probe] battle check: no end judgment"); return; }
                        var dead = new List<string>();
                        foreach (var u in battle.GetEnemyUnits())
                        {
                            bool down = false;
                            var cl = u?.BattleUnitDataInfo?.GetConfirmedConditionType();
                            if (cl != null) foreach (var t in cl) if (t == ConditionType.UnableFight) down = true;
                            dead.Add($"{u?.BattleUnitDataInfo?.Parameter?.CurrentHP} HP, knocked out {down}");
                        }
                        judge.Execute();
                        var r = judge.GetEndJugment();
                        Plugin.L.LogInfo($"[probe] battle check: enemies [{string.Join("; ", dead)}]; end judgment {r} ({judge.CurrentEndJugmentType})");
                        // the loop checks the end when an act finishes: tell it one did
                        var players = battle.GetPlayerUnits();
                        if (players != null && players.Length > 0)
                        {
                            battle.BattleProgress?.ActExectionEndDelegate(players[0], false);
                            Plugin.L.LogInfo("[probe] battle check: an act's end reported to the battle loop");
                        }
                    }
                    catch (Exception e) { Plugin.L.LogInfo($"[probe] battle check: end judgment failed: {e.Message}"); }
                    return;
                case 1:
                    // the battle ends, its result runs (the result screen waits for input: its own
                    // steps log through the hooks); the party is dumped whenever it changes
                    if (now - lastDump >= 2)
                    {
                        lastDump = now;
                        var cur = PartyAbilities();
                        if (cur != lastParty)
                        {
                            Plugin.L.LogInfo($"[probe] battle check: changed ({(inBattle ? "in battle" : "after battle")}, {now - winAt:0} s): {cur}");
                            lastParty = cur;
                        }
                    }
                    if (now - winAt < 45) return;
                    Finish($"battle check done ({(inBattle ? "still in battle" : "battle over")})");
                    return;
            }
        }

        // --- shop check (request "shopcheck=<map id>:<point in>:<script>", instead of the probe) ---
        // Warps to the map, finds the script the way the field does, runs it with its message
        // lines passed (no input headless) and sees whether the shop opens: the mod's added
        // merchants (port_merchants, ShopCalls) end to end.
        static string shopCheck;
        static bool boxCheck;   // request "boxcheck=<map>:<point>:<script>": a box's script, its battle
        static int shopPhase;
        static float shopAt;
        internal static bool SkipMessages;   // the Msg instruction completes at once (shop check only)

        public static bool Msg_Prefix(ref int __result)
        {
            if (!SkipMessages) return true;
            __result = 0;
            return false;
        }

        static int CurrentMap() => Last.Management.UserDataManager.Instance()?.CurrentMapId ?? -1;
        static bool ScriptRunning() => Last.Interpreter.Integrator.Instance?.IsMainCoreAlive() ?? false;
        static int warps;

        static void StepShop(float now)
        {
            var parts = shopCheck.Split(':');
            int map = int.Parse(parts[0]), point = int.Parse(parts[1]);
            string script = parts[2];
            switch (shopPhase)
            {
                case 0:
                    // the loaded save's field first settles (fade-in, its arrival script, an event
                    // scene's own map change: messages pass at once meanwhile), up to 60 s
                    SkipMessages = true;
                    if (now - stepSince < 8 || !OnField() || (ScriptRunning() && now - shopAt < 60)) return;
                    var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
                    if (ep == null) { Finish("shop check: no event procedure"); return; }
                    var ld = new Last.Map.LoadData();
                    ld.NextMapData(map, point, (Last.Map.ViewType)0);
                    ep.EventGotoMap(ld, false, false);
                    Plugin.L.LogInfo($"[probe] shop check: warp from map {CurrentMap()} to map {map} point {point}");
                    shopPhase = 1;
                    shopAt = now;
                    return;
                case 1:
                    // the new map is up (its id set, its field built) and has settled; an event
                    // that moved the party elsewhere meanwhile: warp again (3 tries)
                    if (CurrentMap() != map && now - shopAt < 30) return;
                    if (now - shopAt < 10 || !OnField()) return;
                    if (CurrentMap() != map && ++warps < 3)
                    {
                        Plugin.L.LogInfo($"[probe] shop check: on map {CurrentMap()}, not {map}: warping again");
                        shopPhase = 0;
                        shopAt = now;
                        stepSince = now;
                        return;
                    }
                    var fc = Object.FindObjectOfType<FieldMap>()?.fieldController;
                    var text = fc?.GetScriptData(script);
                    Plugin.L.LogInfo($"[probe] shop check: script {script} {(text == null ? "NOT FOUND" : $"found ({text.Length} chars, {(text.Contains("BNW:shop") ? "with" : "without")} the shop call)")}");
                    Plugin.L.LogInfo($"[probe] shop check: on map {CurrentMap()}");
                    // the merchant itself: the mod's NPCs (script ids from 9901) and their cells
                    var npcs = new List<string>();
                    if (fc?.entityList != null)
                        foreach (var e in fc.entityList)
                        {
                            var pe = e?.Property?.TryCast<Last.Map.PropertyEvent>();
                            if (pe == null || pe.ScriptId <= 9900) continue;
                            var c = fc.ConvertWorldPositionToCellPosition(e.transform.position);
                            npcs.Add($"script {pe.ScriptId} at ({Mathf.Round(c.x)},{Mathf.Round(c.y)}){(e.gameObject.activeInHierarchy ? "" : " (hidden)")}");
                        }
                    Plugin.L.LogInfo($"[probe] shop check: the mod's NPCs here: {(npcs.Count == 0 ? "none" : string.Join(", ", npcs))}");
                    if (text == null) { Finish("shop check failed: the script isn't loaded"); return; }
                    var ep2 = Object.FindObjectOfType<MainGame>()?.eventProcedure;
                    Last.Interpreter.ICallbacks cb = null;
                    try { cb = new Last.Battle.EventCallBacks().Cast<Last.Interpreter.ICallbacks>(); } catch (Exception) { }
                    SkipMessages = true;
                    // a script reservation the loaded save's event scene left when the check warped
                    // away from it (a box's battle reserves its after-battle script, and a second
                    // reservation throws "Reserved script is already booked"): cancelled, logged
                    var integ = Last.Interpreter.Integrator.Instance;
                    if (integ != null && integ.ExistsReserveScript())
                    {
                        Plugin.L.LogInfo($"[probe] shop check: a reserved script was pending ({integ.reservedScript}): cancelled");
                        integ.CancelReservedScript();
                    }
                    bool ok = ep2 != null && ep2.EventRunScript(script, cb);
                    Plugin.L.LogInfo($"[probe] shop check: EventRunScript {script}: {ok}");
                    if (!ok) { Finish("shop check failed: the script didn't start"); return; }
                    shopPhase = 2;
                    shopAt = now;
                    return;
                case 2 when boxCheck:
                    // a monster-in-a-box (port_chests' new boxes): its battle starts, with its monsters
                    var battle = BattlePlugManager.Instance();
                    var foes = battle != null && battle.IsBattle() ? battle.GetEnemyUnits() : null;
                    bool up = foes != null && foes.Length > 0;
                    if (!up && now - shopAt < 30) return;
                    if (up && now - shopAt < 4) return;   // every enemy in place
                    SkipMessages = false;
                    var ids = new List<string>();
                    if (up)
                        foreach (var u in foes)
                            ids.Add(u?.MonsterMaster != null ? u.MonsterMaster.Id.ToString() : "?");
                    Finish($"box check {(up ? "ok" : "FAILED")}: {(up ? $"a battle with monsters [{string.Join(" ", ids)}]" : "no battle")}");
                    return;
                case 2:
                    var shop = Last.Management.ShopUIManager.Instance;
                    bool open = shop != null && shop.IsOpen;
                    if (!open && now - shopAt < 8) return;
                    SkipMessages = false;
                    Finish($"shop check {(open && ShopCalls.Opened > 0 ? "ok" : "FAILED")}: shop UI {(open ? "open" : "not open")}, " +
                           $"the mod's shop calls {ShopCalls.Opened}");
                    return;
            }
        }

        static void StepField(float now)
        {
            if (shopCheck != null) { shopAt = now; Go(Step.Shop); return; }
            if (learnCheck != null) { Go(Step.Learn); return; }
            if (newGame) { Go(Step.NewGame); return; }
            var battle = BattlePlugManager.Instance();
            if (battle != null && battle.IsBattle()) { Go(Step.Battle); return; }
            if (now - stepSince < 4 || now - lastTry < 6) return;
            if (encounterTries++ >= 5) { Finish("could not start a battle from the field"); return; }
            lastTry = now;
            var controller = Object.FindObjectOfType<FieldMap>()?.fieldController;
            var party = MasterManager.Instance.GetData<MonsterParty>(partyId);
            if (controller == null || party == null) { Finish($"no field controller or formation {partyId}"); return; }
            Plugin.L.LogInfo($"[probe] starting formation {partyId}");
            controller.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
        }

        static void StepBattle(float now)
        {
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) return;
            if (battle.GetPlayerUnits()?.Length > 0 && battle.GetEnemyUnits()?.Length > 0 && now - stepSince >= 5 && battleCheck)
            {
                Go(Step.Win);
                return;
            }
            if (battle.GetPlayerUnits()?.Length > 0 && battle.GetEnemyUnits()?.Length > 0 && now - stepSince >= 5)
            {
                RelicRules.LogParty(battle);
                Prepare(battle);
                Go(Step.Probing);
            }
        }

        internal static void Quit(string why) => Finish(why);

        static void Finish(string why)
        {
            Active = false;
            Running = false;
            step = Step.Done;
            try { file?.Flush(); file?.Close(); } catch { }
            file = null;
            Plugin.L.LogInfo($"[probe] done: {why}");
            if (quit)
            {
                Plugin.L.LogInfo("[probe] quitting the game");
                Application.Quit();
            }
        }

        // --- stat overrides ----------------------------------------------------------------

        enum S { Level, Power, Agility, Vitality, Magic, Attack, Defense, AbilityDefense, Accuracy, Evasion,
                 AbilityEvasion, Critical, MaxHp, Intelligence, Spirit, Count }

        static readonly string[] Getters =
        {
            "ConfirmedLevel", "ConfirmedPower", "ConfirmedAgility", "ConfirmedVitality", "ConfirmedMagic",
            "ConfirmedAttack", "ConfirmedDefense", "ConfirmedAbilityDefense", "ConfirmedAccuracyRate",
            "ConfirmedEvasionRate", "ConfirmedAbilityEvasionRate", "ConfirmedCriticalRate", "ConfirmedMaxHp",
            "ConfirmedIntelligence", "ConfirmedSpirit",
        };

        static IntPtr attPtr, tgtPtr, attPtr2, tgtPtr2;  // 2 = the character's OwnedCharacterData.Parameter
        static readonly int[] attStats = new int[(int)S.Count], tgtStats = new int[(int)S.Count];
        static readonly List<string> unpatched = new List<string>();

        static void PatchOverrides(Harmony h)
        {
            foreach (var type in new[] { typeof(PlayerCharacterParameter), typeof(EnemyCharacterParameter) })
            {
                for (int i = 0; i < (int)S.Count; i++)
                    if (!PatchFirst(h, type, Getters[i], $"Get{i}")) unpatched.Add($"{type.Name}.{Getters[i]}");
                if (!PatchFirst(h, type, "ConfirmedResistanceAttributeList", nameof(NoElements)))
                    unpatched.Add($"{type.Name}.ConfirmedResistanceAttributeList");
            }
            // a character's Fight reads its Battle Power per hand, not ConfirmedAttack (decompiled
            // PhysicalExecution): answer the probe's Attack there too
            if (!PatchFirst(h, typeof(PlayerCharacterParameter), "GetArmConfirmedAttack", "Get5"))
                unpatched.Add("PlayerCharacterParameter.GetArmConfirmedAttack");
            // every unit counts as front row while probing (the save's own rows would halve
            // physical damage both ways for a back-row character: x0.48 in the 2026-11-05 run)
            var rowMethods = typeof(BattleUtility).GetMethods().Where(m => m.Name == "GetCorpsId").ToList();
            var calcRow = AccessTools.Method(typeof(CalcControllerProvider), "GetCorpsId");  // what the damage calc reads
            if (calcRow != null) rowMethods.Add(calcRow);
            foreach (var m in rowMethods)
            {
                try
                {
                    if (Hooks.TinyNativeCode(m) != null) { unpatched.Add($"{m.DeclaringType.Name}.GetCorpsId (tiny)"); continue; }
                    h.Patch(m, postfix: new HarmonyMethod(typeof(FormulaProbe), nameof(FrontRow)) { priority = Priority.First });
                }
                catch (Exception e) { unpatched.Add($"{m.DeclaringType.Name}.GetCorpsId ({e.Message})"); }
            }
            if (unpatched.Count > 0) Plugin.L.LogWarning($"[probe] stats it can't set: {string.Join(", ", unpatched)}");
        }

        static void FrontRow(ref CorpsId __result)
        {
            if (Active) __result = CorpsId.Front;
        }

        static bool PatchFirst(Harmony h, Type type, string method, string postfix)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null || original.DeclaringType != type) return false;
                if (Hooks.TinyNativeCode(original) != null) return false;
                var hm = new HarmonyMethod(typeof(FormulaProbe), postfix) { priority = Priority.First };
                h.Patch(original, postfix: hm);
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[probe] patch {type.Name}.{method}: {e.Message}");
                return false;
            }
        }

        static void Over(CharacterParameterBase p, ref int result, int stat)
        {
            if (!Active || p == null) return;
            var ptr = p.Pointer;
            if (ptr == attPtr || ptr == attPtr2) { if (attStats[stat] >= 0) result = attStats[stat]; }
            else if (ptr == tgtPtr || ptr == tgtPtr2) { if (tgtStats[stat] >= 0) result = tgtStats[stat]; }
        }

        public static void Get0(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 0);
        public static void Get1(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 1);
        public static void Get2(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 2);
        public static void Get3(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 3);
        public static void Get4(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 4);
        public static void Get5(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 5);
        public static void Get6(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 6);
        public static void Get7(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 7);
        public static void Get8(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 8);
        public static void Get9(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 9);
        public static void Get10(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 10);
        public static void Get11(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 11);
        public static void Get12(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 12);
        public static void Get13(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 13);
        public static void Get14(CharacterParameterBase __instance, ref int __result) => Over(__instance, ref __result, 14);

        // no elemental weakness/resistance on the two probe units
        public static void NoElements(CharacterParameterBase __instance, ref Il2CppIntMap __result)
        {
            if (!Active || __instance == null) return;
            var ptr = __instance.Pointer;
            if (ptr == attPtr || ptr == tgtPtr || ptr == attPtr2 || ptr == tgtPtr2) __result = new Il2CppIntMap();
        }

        // --- cases ---------------------------------------------------------------------------

        class Case
        {
            public string Name;
            public int[] A, T;
            public int AHp, THp;
            public CorpsId ARow = CorpsId.Front, TRow = CorpsId.Front;
            // statuses put on the attacker (true) or target for this case, removed afterwards;
            // such cases run only for the Special abilities (Attack, Fire, Cure, Quake)
            public List<(bool OnAttacker, ConditionType Type)> Statuses = new List<(bool, ConditionType)>();
        }

        // status and character-on-character cases run only for these (cost)
        static readonly HashSet<int> Special = new HashSet<int> { 1, 31, 40, 52 };

        static int[] BaseAttacker() => new[] { 30, 50, 40, 40, 50, 100, 50, 50, 255, 0, 0, 0, 5000, 50, 40 };
        static int[] BaseTarget() => new[] { 30, 40, 40, 40, 30, 50, 100, 100, 100, 0, 0, 0, 9999, 30, 40 };

        static List<Case> Cases()
        {
            var list = new List<Case>();
            Case Make(string name, Action<Case> set = null)
            {
                var c = new Case { Name = name, A = BaseAttacker(), T = BaseTarget(), AHp = 5000, THp = 9999 };
                set?.Invoke(c);
                list.Add(c);
                return c;
            }
            Make("base");
            void VaryA(S s, params int[] values) { foreach (var v in values) Make($"a_{s}={v}", c => c.A[(int)s] = v); }
            void VaryT(S s, params int[] values) { foreach (var v in values) Make($"t_{s}={v}", c => c.T[(int)s] = v); }
            VaryA(S.Level, 1, 10, 50, 70, 99);
            VaryA(S.Power, 1, 20, 80, 128, 200);
            VaryA(S.Magic, 1, 20, 80, 128, 200);
            VaryA(S.Attack, 0, 50, 150, 255);
            VaryA(S.Vitality, 1, 20, 80, 128);
            VaryA(S.Agility, 1, 80, 128);
            VaryT(S.Defense, 0, 50, 150, 200, 255);
            VaryT(S.AbilityDefense, 0, 50, 150, 200, 255);
            VaryT(S.Vitality, 1, 20, 80, 128);
            VaryT(S.Power, 1, 80, 128);
            VaryT(S.Level, 1, 10, 99);
            foreach (var hp in new[] { 100, 1000 }) Make($"a_hp={hp}", c => c.AHp = hp);
            foreach (var hp in new[] { 100, 1000, 5000 }) Make($"t_hp={hp}", c => c.THp = hp);
            foreach (var lv in new[] { 10, 50, 99 })
            {
                foreach (var v in new[] { 20, 80, 128 }) Make($"a_Level={lv},a_Power={v}", c => { c.A[0] = lv; c.A[(int)S.Power] = v; });
                foreach (var v in new[] { 50, 150, 255 }) Make($"a_Level={lv},a_Attack={v}", c => { c.A[0] = lv; c.A[(int)S.Attack] = v; });
                foreach (var v in new[] { 20, 80, 128 }) Make($"a_Level={lv},a_Magic={v}", c => { c.A[0] = lv; c.A[(int)S.Magic] = v; });
            }
            // no row cases: the game takes rows from the units, not from CalcInfo (run 1)
            foreach (var (onAttacker, type) in new[]
                     {
                         (true, ConditionType.Kappa), (true, ConditionType.Trance), (false, ConditionType.Trance), (true, ConditionType.Blind),
                         (false, ConditionType.Defend), (false, ConditionType.Protect), (false, ConditionType.Shell),
                     })
                Make($"{(onAttacker ? "a" : "t")}_status={type}", c => c.Statuses.Add((onAttacker, type)));
            return list;
        }

        // --- the run -----------------------------------------------------------------------

        // damage/heal function types (Last.Defaine.Master.FunctionType, 1-based like function.csv)
        static readonly HashSet<int> DamageTypes = new HashSet<int>
        {
            1, 2, 3, 13, 23,      // Recovery, Physical, Magic, Absorption, Unique
            32, 33, 34, 36, 41,   // BaseValueFixedHP Recovery/Magic, ResultValueFixedHPRecovery, OneDigitHP, ParameterFixedCoefficientPhysical
            48, 50, 55, 56, 65,   // DifferentRange, ThrowItem, SelectHp, UserSelectHp, RecoveryEx
            68, 70, 74, 76, 77,   // MagicAbsorption, Kill, MagicPointReduction, PhysicsAbsorption, PowerDependentMagic
        };

        // Dir: PE character -> monster, EP monster -> character, PP character -> another character
        struct Job { public Ability Ability; public int Index, Type, V1, V2, V3; public string Dir; public bool PlayerAttacks => Dir == "PE"; }

        static readonly Queue<Job> jobs = new Queue<Job>();
        static List<Case> cases;
        static StreamWriter file;
        static BattlePlayerData player, player2;
        static BattleEnemyData enemy;
        static CalcControllerProvider provider;
        static Weapon weapon;
        static Command fight;
        static int jobsTotal, calls, errors;
        static readonly Stopwatch clock = new Stopwatch();

        static void Prepare(BattlePlugManager battle)
        {
            DamageLog.Enabled = false;
            provider = BattleControllerManagement.Instance()?.GetCurrentProvider()?.TryCast<CalcControllerProvider>();
            if (provider == null) { Finish("no calc provider"); return; }
            var mm = MasterManager.Instance;
            weapon = mm.GetData<Weapon>(weaponId);
            fight = mm.GetData<Command>(1);

            // the character with the fewest statuses, and the first monster
            int best = int.MaxValue;
            foreach (var p in battle.GetPlayerUnits())
            {
                int n = p?.BattleUnitDataInfo?.GetConfirmedConditionType()?.Count ?? 99;
                if (p != null && n < best) { best = n; player = p; }
            }
            enemy = battle.GetEnemyUnits()[0];
            foreach (var p in battle.GetPlayerUnits())
                if (p != null && p.Pointer != player.Pointer) { player2 = p; break; }
            cases = Cases();

            foreach (var kv in mm.GetList<Ability>())
            {
                var a = kv.Value;
                var g = a == null ? null : mm.GetData<FunctionGroup>(a.BattleFunctionGroupId);
                if (g == null) continue;
                var fns = new[]
                {
                    (g.Function1Id, g.Function1Value1, g.Function1Value2, g.Function1Value3),
                    (g.Function2Id, g.Function2Value1, g.Function2Value2, g.Function2Value3),
                    (g.Function3Id, g.Function3Value1, g.Function3Value2, g.Function3Value3),
                };
                for (int k = 0; k < 3; k++)
                {
                    if (!DamageTypes.Contains(fns[k].Item1)) continue;
                    var dirs = Special.Contains(a.Id) && player2 != null ? new[] { "PE", "EP", "PP" } : new[] { "PE", "EP" };
                    foreach (var dir in dirs)
                        jobs.Enqueue(new Job { Ability = a, Index = k + 1, Type = fns[k].Item1, V1 = fns[k].Item2,
                                               V2 = fns[k].Item3, V3 = fns[k].Item4, Dir = dir });
                }
            }
            jobsTotal = jobs.Count;

            file = new StreamWriter(outPath, false, new UTF8Encoding(false));
            file.WriteLine($"# probe {DateTime.Now:yyyy-MM-dd HH:mm} plugin {Plugin.Version} formation {partyId} weapon {weaponId} " +
                           $"samples {samples}; character {player?.ownedCharacterData?.Name} statuses {Conditions(player)}; " +
                           $"monster {enemy?.GetMonster()?.Id} statuses {Conditions(enemy)}; can't set: {string.Join(" ", unpatched)}");
            file.WriteLine("ability,ability_type,attribute,standard,fn,fn_type,v1,v2,v3,dir,case," +
                           "a_lv,a_vig,a_spd,a_sta,a_mag,a_atk,a_hp,t_lv,t_vig,t_sta,t_def,t_mdef,t_hp,a_row,t_row," +
                           "n,hit_n,hit_min,hit_max,hit_mean,crit_n,crit_min,crit_max,heal_n,heal_min,heal_max," +
                           "mp_n,mp_min,mp_max,miss_n,zero_n,other_n,err_n,factor_min,factor_max,raw_min,raw_max");
            Plugin.L.LogInfo($"[probe] {jobsTotal} ability-function directions x {cases.Count} cases x {samples} samples " +
                             $"(second character: {player2?.ownedCharacterData?.Name ?? "none"})");
        }

        static string Conditions(BattleUnitData u)
        {
            var list = u?.BattleUnitDataInfo?.GetConfirmedConditionType();
            if (list == null) return "?";
            var parts = new List<string>();
            foreach (var c in list) parts.Add(c.ToString());
            return parts.Count == 0 ? "none" : string.Join("|", parts);
        }

        static void StepProbe()
        {
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle()) { Finish($"the battle ended early ({jobsTotal - jobs.Count}/{jobsTotal} done)"); return; }
            KeepAlive(battle);
            var saved = provider.GetCalcInfo();
            clock.Restart();
            try
            {
                while (jobs.Count > 0 && clock.ElapsedMilliseconds < 150)
                {
                    var job = jobs.Dequeue();
                    foreach (var c in cases)
                        if (c.Statuses.Count == 0 || Special.Contains(job.Ability.Id)) RunCase(job, c);
                }
            }
            finally
            {
                Active = false;
                provider.SetCalcInfo(saved);
            }
            file.Flush();
            int done = jobsTotal - jobs.Count;
            if (done % 200 < 2 || jobs.Count == 0)
                Plugin.L.LogInfo($"[probe] {done}/{jobsTotal} ({calls} calls, {errors} errors)");
            if (jobs.Count == 0) Finish($"wrote {outPath}");
        }

        // nobody dies while the probe runs between frames
        static void KeepAlive(BattlePlugManager battle)
        {
            foreach (var u in battle.GetPlayerUnits()) Heal(u);
            foreach (var u in battle.GetEnemyUnits()) Heal(u);
        }

        static void Heal(BattleUnitData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p != null && p.CurrentHP > 0) p.CurrentHP = p.ConfirmedMaxHp();
        }

        static void RunCase(Job job, Case c)
        {
            var att = job.Dir == "EP" ? (BattleUnitData)enemy : player;
            var tgt = job.Dir == "PE" ? (BattleUnitData)enemy : job.Dir == "PP" ? player2 : player;
            var ap = att.BattleUnitDataInfo.Parameter;
            var tp = tgt.BattleUnitDataInfo.Parameter;
            attPtr = ap.Pointer;
            tgtPtr = tp.Pointer;
            var owned = player.ownedCharacterData?.Parameter;
            var owned2 = job.Dir == "PP" ? player2.ownedCharacterData?.Parameter : owned;
            attPtr2 = job.Dir != "EP" && owned != null ? owned.Pointer : IntPtr.Zero;
            tgtPtr2 = job.Dir != "PE" && owned2 != null ? owned2.Pointer : IntPtr.Zero;
            Array.Copy(c.A, attStats, attStats.Length);
            Array.Copy(c.T, tgtStats, tgtStats.Length);
            int aHpWas = ap.CurrentHP, tHpWas = tp.CurrentHP;

            var info = new CalcInfo();
            info.command = job.Ability.TypeId == 4 ? fight : null;
            info.isDominantArm = true;
            info.targetSum = 1;
            info.userCorpsId = c.ARow;
            info.targetCorpsId = c.TRow;
            info.isSideAttack = false;
            info.baseHitRate = 255;
            if (job.Dir != "EP")
            {
                info.userWeapon = weapon;
                info.userRightWeapon = weapon;
                info.userCharaData = player.ownedCharacterData;
            }
            if (job.Dir == "PE") info.targetMonster = enemy.GetMonster();
            else info.targetCharaData = (job.Dir == "PP" ? player2 : player).ownedCharacterData;
            var conditions = BattlePlugManager.Instance()?.BattleConditionController;
            foreach (var (onAttacker, type) in c.Statuses)
                try { conditions?.InterruptAddCondition(onAttacker ? att : tgt, type); } catch (Exception) { }

            int hitN = 0, critN = 0, healN = 0, mpN = 0, missN = 0, zeroN = 0, otherN = 0, errN = 0;
            int hitMin = int.MaxValue, hitMax = 0, critMin = int.MaxValue, critMax = 0, healMin = int.MaxValue, healMax = 0,
                mpMin = int.MaxValue, mpMax = 0;
            long hitSum = 0;
            int rawMin = int.MaxValue, rawMax = 0;  // the game's own value, BNW's battle rules off
            double fMin = double.MaxValue, fMax = 0;
            int[] seenA = new int[7], seenT = new int[7];
            Active = true;
            try
            {
                ap.CurrentHP = c.AHp;
                tp.CurrentHP = c.THp;
                seenA = Seen(ap, c.AHp);
                seenT = Seen(tp, c.THp);
                // pass 1: BNW's battle rules off, the game's own values (raw_*: the vanilla baseline)
                bool damageWas = BattleFormulas.Damage, spiritWas = BattleFormulas.Spirit;
                BattleFormulas.Damage = BattleFormulas.Spirit = false;
                try
                {
                    for (int i = 0; i < samples; i++)
                    {
                        calls++;
                        try
                        {
                            provider.SetCalcInfo(info);
                            var f0 = ParameterActFunctionManagment.GetFunctionBaseCalc((FunctionType)job.Type, job.V1, job.V2, job.V3, TargetType.SingleEnemy);
                            if (f0 == null) break;
                            f0.MainCalc(ap, tp, job.Ability, null);
                            var r0 = f0.GetCalcResult();
                            if (r0 == null) continue;
                            var h0 = r0.GetHitType();
                            if (h0 != HitType.Hit && h0 != HitType.Recovery) continue;
                            int v0 = r0.GetValue(false);
                            rawMin = Math.Min(rawMin, v0);
                            rawMax = Math.Max(rawMax, v0);
                        }
                        catch (Exception) { errN++; }
                    }
                }
                finally
                {
                    BattleFormulas.Damage = damageWas;
                    BattleFormulas.Spirit = spiritWas;
                }
                // pass 2: as a player gets it
                for (int i = 0; i < samples; i++)
                {
                    calls++;
                    try
                    {
                        provider.SetCalcInfo(info);
                        BattleFormulas.LastFactor = 1.0;
                        var f = ParameterActFunctionManagment.GetFunctionBaseCalc((FunctionType)job.Type, job.V1, job.V2, job.V3, TargetType.SingleEnemy);
                        if (f == null) { otherN++; break; }
                        f.MainCalc(ap, tp, job.Ability, null);
                        var r = f.GetCalcResult();
                        if (r == null) { otherN++; continue; }
                        int v = r.GetValue(false);
                        var hit = r.GetHitType();
                        if (BattleFormulas.LastFactor != 1.0)
                        {
                            fMin = Math.Min(fMin, BattleFormulas.LastFactor);
                            fMax = Math.Max(fMax, BattleFormulas.LastFactor);
                        }
                        BattleFormulas.LastFactor = 1.0;
                        switch (hit)
                        {
                            case HitType.Hit: hitN++; hitSum += v; hitMin = Math.Min(hitMin, v); hitMax = Math.Max(hitMax, v); break;
                            case HitType.Critical: critN++; critMin = Math.Min(critMin, v); critMax = Math.Max(critMax, v); break;
                            case HitType.Recovery: healN++; healMin = Math.Min(healMin, v); healMax = Math.Max(healMax, v); break;
                            case HitType.MPHit:
                            case HitType.MPRecovery: mpN++; mpMin = Math.Min(mpMin, v); mpMax = Math.Max(mpMax, v); break;
                            case HitType.Miss: missN++; break;
                            case HitType.Zero: zeroN++; break;
                            default: otherN++; break;
                        }
                    }
                    catch (Exception e)
                    {
                        errN++;
                        if (++errors <= 30) Plugin.L.LogWarning($"[probe] ability {job.Ability.Id} fn {job.Type} {c.Name}: {e.Message}");
                    }
                }
            }
            finally
            {
                Active = false;
                ap.CurrentHP = aHpWas;
                tp.CurrentHP = tHpWas;
                foreach (var (onAttacker, type) in c.Statuses)
                    try { conditions?.InterruptRemoveCondition(onAttacker ? att : tgt, type); } catch (Exception) { }
            }
            string M(int n, int lo, int hi) => n == 0 ? "," : $"{lo},{hi}";
            var a = job.Ability;
            file.WriteLine(string.Join(",",
                a.Id, a.TypeId, a.AttributeId, a.StandardValue, job.Index, job.Type, job.V1, job.V2, job.V3,
                job.Dir, c.Name.Replace(",", ";"),
                string.Join(",", seenA[0], seenA[1], seenA[2], seenA[3], seenA[4], seenA[5], seenA[6]),
                string.Join(",", seenT[0], seenT[1], seenT[3], seenT[4], seenT[5], seenT[6]),
                c.ARow, c.TRow, samples,
                hitN, M(hitN, hitMin, hitMax), hitN == 0 ? "" : (hitSum / (double)hitN).ToString("0.#", CultureInfo.InvariantCulture),
                critN, M(critN, critMin, critMax), healN, M(healN, healMin, healMax), mpN, M(mpN, mpMin, mpMax),
                missN, zeroN, otherN, errN,
                fMax == 0 ? "" : fMin.ToString("0.###", CultureInfo.InvariantCulture),
                fMax == 0 ? "" : fMax.ToString("0.###", CultureInfo.InvariantCulture),
                rawMax == 0 && rawMin == int.MaxValue ? "" : rawMin.ToString(),
                rawMax == 0 && rawMin == int.MaxValue ? "" : rawMax.ToString()));
        }

        // what the game's getters answer for the stats the formulas use (lv, vig, spd, sta, mag, atk, hp)
        // and for the target (lv, vig, -, sta, def, mdef, hp; spd unused)
        static int[] Seen(CharacterParameterBase p, int hp)
        {
            bool att = p.Pointer == attPtr || p.Pointer == attPtr2;
            return att
                ? new[] { p.ConfirmedLevel(), p.ConfirmedPower(), p.ConfirmedAgility(), p.ConfirmedVitality(), p.ConfirmedMagic(), p.ConfirmedAttack(), p.CurrentHP }
                : new[] { p.ConfirmedLevel(), p.ConfirmedPower(), 0, p.ConfirmedVitality(), p.ConfirmedDefense(), p.ConfirmedAbilityDefense(), p.CurrentHP };
        }
    }
}

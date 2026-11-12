using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Last.Interpreter;
using Last.Interpreter.Instructions;
using Last.Management;

namespace WorldsCollide.PR
{
    // Worlds Collide's events: the PR event scripts we change (tools/wc_events.py, shipped in the
    // WorldsCollide Magicite folder) call SysCall "WC:<name>" where WC does something per seed;
    // the game's SysCall instruction looks the name up in its table, so a prefix on it handles
    // ours and leaves every other call alone.
    //   WC:start   New Game with a seed: gil, items, espers; sets map-local variable 6 to 1 so
    //              the opening goes on to WC's start (else 0: the vanilla opening runs)
    //   WC:party   the seed's starting characters join (the game's own join calls)
    //   WC:to_wor / WC:to_wob   the airship wheels' world switch: the Falcon (WoR) / Blackjack
    //              (WoB) goes where the other airship is, released and placed
    //   WC:offer <module> <world> <message key>   put the seed's price in that line (Tzen)
    //   WC:pay <module> <world>     take the seed's price if the party has it: map-local 0 = 1
    //              (paid) or 0, as the game's RemoveGil leaves it
    //   WC:reward <module> [n]      the seed's n-th reward for that check (character joins, esper
    //              or item added); the text "WC_REWARD" becomes its "Obtained ..." line
    //   WC:announce <module> <n> <key>   message <key> names the check's n-th reward (auction)
    //   WC:hasflag <flag>  ScenarioFlag4 131 = 1 when the seed's flags have <flag> (-etr...)
    //   WC:auction_hint <key> <done flags...>   -adeh: the auction's greeting hints at unsold espers
    //   WC:gate <module>   ScenarioFlag4 131 = 1 when the check's gating character has joined
    //              (or the seed has no character gating, -cg), else 0
    // Every character joining a WC game (any join, ours or the game's) sets ScenarioFlag4
    // 100 + status id: WC's "character recruited" bits (gates, the Track menu's counts).
    static class WcEvents
    {
        internal const int StartVar = 6;   // tools/wc_events.py WC_VAR
        internal static string LastStart = "";
        internal static bool StartRan;     // this New Game ran WC:start (WcSeeds skips its fallback grant)

        // character status id (Terra 1 .. Umaro 14) -> the game's join call
        static readonly string[] JoinNames =
        {
            null, "ティナ", "ロック", "カイエン", "シャドウ", "エドガー", "マッシュ", "セリス", "ストラゴス",
            "リルム", "セッツァー", "モグ", "ガウ", "ゴゴ", "ウーマロ",
        };

        public static void Patch(Harmony h)
        {
            var timer = AccessTools.Method(typeof(External.Timer), "SetTimer");
            if (timer != null) h.Patch(timer, prefix: new HarmonyMethod(typeof(WcEvents), nameof(Timer_Prefix)));
            var m = AccessTools.Method(typeof(External.Misc), "SystemCall");
            if (m == null) { Plugin.L.LogWarning("[wc] no External.Misc.SystemCall: WC's events are off"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcEvents), nameof(SystemCall_Prefix)));
            // trace (tests): instruction handlers returning int (MainCore.Execute returns a
            // Nullable<int>, and hooking it broke the VM)
            foreach (var (t, n) in new (Type, string)[]
                     {
                         (typeof(Internal.Main), "Jump"), (typeof(Internal.Main), "JumpCommon"), (typeof(Internal.Main), "Call"), (typeof(Internal.Main), "Return"), (typeof(External.Misc), "SystemCall"),
                         (typeof(Internal.Main), "ComparisonOperation"), (typeof(Internal.Main), "SetPuppet"),
                         (typeof(Internal.Main), "ExecPuppet"), (typeof(Internal.Main), "FadeOut"),
                         (typeof(External.Misc), "ChangeMap"), (typeof(External.Message), "Select"),
                     })
            {
                var tm = AccessTools.Method(t, n, new[] { typeof(MainCore) });
                if (tm != null) h.Patch(tm, postfix: new HarmonyMethod(typeof(WcEvents), nameof(Trace_Postfix)));
            }
            var exit = AccessTools.Method(typeof(Last.Interpreter.Instructions.Internal), "Exit");
            if (exit != null) h.Patch(exit, postfix: new HarmonyMethod(typeof(WcEvents), nameof(Exit_Postfix)));
            var morph = AccessTools.Method(typeof(global::Last.Battle.TranceConditionFunction), "Start");
            if (morph != null) h.Patch(morph, postfix: new HarmonyMethod(typeof(WcEvents), nameof(Morph_Postfix)));
            var organize = AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "OrganizeOneParty");
            if (organize != null) h.Patch(organize, prefix: new HarmonyMethod(typeof(WcEvents), nameof(Organize_Prefix)));
            var join = AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "JoinCharacter");
            if (join != null) h.Patch(join, postfix: new HarmonyMethod(typeof(WcEvents), nameof(Join_Postfix)));
            else Plugin.L.LogWarning("[wc] no Current.JoinCharacter: recruited flags are off");
            var joinGauBattle = AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "JoinGauInBattle");
            if (joinGauBattle != null)
                h.Patch(joinGauBattle, prefix: new HarmonyMethod(typeof(WcEvents), nameof(JoinGauInBattle_Prefix)));
            else Plugin.L.LogWarning("[wc] no Current.JoinGauInBattle: Veldt reward still uses Gau's scene");
            // the dried meat's first-join branch (battle event 01, SysCall "ガウ初回予約"): the one a
            // WC game takes (ScenarioFlag1 126 is never set by a WC game's Gau)
            var firstJoin = AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "ReserveScriptGauFirstJoin");
            if (firstJoin != null)
                h.Patch(firstJoin, prefix: new HarmonyMethod(typeof(WcEvents), nameof(GauFirstJoin_Prefix)));
            else Plugin.L.LogWarning("[wc] no Current.ReserveScriptGauFirstJoin: the Veldt reward goes through Gau's scene map");
            var msgm = AccessTools.Method(typeof(Last.Interpreter.Instructions.Message), "Msg");
            if (msgm != null) h.Patch(msgm, prefix: new HarmonyMethod(typeof(WcEvents), nameof(Msg_Prefix)));
            var sel = AccessTools.Method(typeof(External.Message), "Select");
            if (sel != null) h.Patch(sel, prefix: new HarmonyMethod(typeof(WcEvents), nameof(Select_Prefix)));
            var interact = AccessTools.Method(typeof(Last.Map.InteractiveTargetObserver), "CanPlayerInteractToEntityPosition");
            if (interact != null)
                h.Patch(interact, prefix: new HarmonyMethod(typeof(WcEvents), nameof(TransportationOverlap_Prefix)));
            else Plugin.L.LogWarning("[wc] no InteractiveTargetObserver.CanPlayerInteractToEntityPosition: transportation overlap parity is off");
        }

        // Pixel Remaster's generic observer accepts targets on the player's tile or one step in
        // front. SNES world-map vehicles are entered from their own tile. Restrict only map-object
        // type 8 (PropertyTransportation) in an active WC world; ordinary NPC/action ranges stay
        // native. Returning false here prevents both the key-help prompt and boarding beside it.
        public static bool TransportationOverlap_Prefix(UnityEngine.Vector3 __0, UnityEngine.Vector3 __1,
                                                         int __3, ref bool __result)
        {
            if (!Plugin.WcMode || __3 != 8) return true;
            int map = UserDataManager.Instance()?.CurrentMapId ?? 0;
            if (map != WobMap && map != WorMap) return true;
            if (UnityEngine.Mathf.Approximately(__0.x, __1.x) &&
                UnityEngine.Mathf.Approximately(__0.y, __1.y)) return true;
            __result = false;
            return false;
        }

        // self-test only: log the event instructions the VM runs (address changes), at most Trace lines
        internal static int Trace;
        static int lastAddr = -1, repeats;
        static string lastName = "";

        public static void TraceEnter(MainCore mc)
        {
            if (Trace > 0) Plugin.L.LogInfo($"[wc trace] enter {mc?.programCounter?.currentAddress} {mc?.currentInstruction?.mnemonic}");
        }

        public static void TraceEnterCore(Core c)
        {
            if (Trace > 0) Plugin.L.LogInfo($"[wc trace] enter {c?.programCounter?.currentAddress} {c?.currentInstruction?.mnemonic}");
        }

        public static void Trace_Postfix(MainCore mc, int __result)
        {
            if (Trace <= 0) return;
            try
            {
                int addr = mc?.programCounter?.currentAddress ?? -1;
                var ins = mc?.currentInstruction;
                var name = ins == null ? "?" : $"{ins.mnemonic} {string.Join(",", ins.operands?.sValues ?? new string[0]).Trim(',')} -> {__result}";
                if (addr == lastAddr && name == lastName) { repeats++; return; }
                Trace--;
                Plugin.L.LogInfo($"[wc trace] {addr} {name}{(repeats > 0 ? $" (previous ran {repeats + 1}x)" : "")}");
                lastAddr = addr;
                lastName = name;
                repeats = 0;
            }
            catch (Exception e) { Trace = 0; Plugin.L.LogWarning($"[wc trace] {e.Message}"); }
        }

        // self-test only: message windows are logged and skipped (nobody presses a button)
        internal static readonly List<string> TestMessages = new List<string>();

        // WC cuts nearly all of the story's dialogue (its events are rewritten to the checks). In a
        // WC game a character's lines in event scenes (sc_e_* scripts, "E" message keys with a
        // speaker: 2,334 of the game's 4,100 lines) are skipped; what stays: townsfolk (NPC scripts,
        // "N" keys), lines without a speaker (narration, signs, a puzzle's answer), system and WC's
        // own messages, a line that asks the question of the menu after it, and a line that tells
        // what the party got. Config [Story] SkipDialogue.
        internal static bool SkipStory = true;
        internal static int StorySkipped, StoryKept;
        static readonly System.Text.RegularExpressions.Regex KeepText = new System.Text.RegularExpressions.Regex(
            @"^<CT>(Obtained|Received|Learned|Found|Got|HP and MP)|dragons? remaining|magicite!|joined the party",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);   // 72 of the 3,546 story lines

        internal static string StoryWhy = "";   // the last decision's reason (the self-test logs it)

        // The game leaves the message window open after a line when another Msg comes next, for it
        // to reuse; a skipped line never touches it. So a line shown before skipped ones stayed on
        // screen into the next scene (the Collapsing House: the leader's "Let me help you!", then
        // Sabin's skipped lines; the box stayed into the house: the user's playtest, 2026-11-06).
        // A skipped line that ends its run (the next instruction past labels isn't a Msg) closes a
        // window left open; inside a run the next line reuses the window as it would have.
        static void CloseAfterSkipped(Core c)
        {
            try
            {
                var mw = Last.Message.MessageWindowManager.Instance;
                if (mw == null || !mw.IsOpen()) return;
                var ms = c?.mnemonics;
                var cur = c?.currentInstruction;
                if (ms != null && cur != null)
                {
                    int at = -1;
                    for (int k = 0; k < ms.Length; k++) if (ms[k] != null && ms[k].Pointer == cur.Pointer) { at = k; break; }
                    int next = at + 1;
                    while (at >= 0 && next < ms.Length && (ms[next]?.mnemonic ?? "") == "Nop") next++;
                    if (at >= 0 && next < ms.Length && (ms[next]?.mnemonic ?? "") == "Msg") return;
                }
                mw.Close();
                Plugin.L.LogInfo($"[wc] story: closed the window a shown line left open ({Integrator.Instance?.scriptName})");
            }
            catch (Exception e) { WcRunner.WarnOnce("msg-close", e); }
        }

        internal static bool SkipStoryLine(Core c, string key)
        {
            StoryWhy = "not story text";
            if (string.IsNullOrEmpty(key) || key[0] != 'E') return false;
            var script = Integrator.Instance?.scriptName ?? "";
            StoryWhy = "not a scene";
            if (!script.StartsWith("sc_e_", StringComparison.OrdinalIgnoreCase)) return false;
            var ms = c.mnemonics;
            var cur = c.currentInstruction;
            if (ms != null && cur != null)
            {
                int at = -1;
                for (int k = 0; k < ms.Length; k++) if (ms[k] != null && ms[k].Pointer == cur.Pointer) { at = k; break; }
                for (int k = at + 1; at >= 0 && k < ms.Length && k <= at + 4; k++)
                {
                    var mn = ms[k]?.mnemonic ?? "";
                    if (mn == "Nop" || mn == "MsgClose" || mn == "Wait") continue;
                    if (mn.StartsWith("Select")) { StoryWhy = "a question"; return false; }   // the menu's question
                    break;
                }
            }
            // a character speaking: the story (2,334 of the 3,546 lines); lines without a speaker are
            // narration, signs and a puzzle's answer ("The clock just keeps ticking away..."),
            // except in a check's scene: WC rewrites those to little more than the reward, so
            // nobody's lines stay either (Phoenix Cave's "Is that...magicite?", Deathgaze's "A
            // piece of magicite fell...", Lone Wolf's "Snatched the treasure...")
            var mm = MessageManager.Instance;
            if (mm == null) return false;
            bool check = CheckScene(script, ms);
            StoryWhy = "no speaker";
            if (string.IsNullOrWhiteSpace(mm.GetSpeaker(key)) && !check) return false;
            var text = mm.GetMessage(key, false) ?? "";
            StoryWhy = "what the party got";
            return !KeepText.IsMatch(text);
        }

        // a script that gives a WC reward (WC:reward), but not a sale's (the auction's and Tzen's
        // talk is the sale: WC:announce, WC:offer). Per script name: WC's scripts are the same
        // for every seed (the reward is looked up when given)
        static readonly Dictionary<string, bool> checkScenes = new Dictionary<string, bool>();

        internal static bool CheckScene(string script, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<global::Last.Interpreter.Instructions.Format.Mnemonic> ms)
        {
            if (ms == null) return false;
            if (checkScenes.TryGetValue(script, out bool known)) return known;
            bool reward = false, sale = false;
            for (int k = 0; k < ms.Length; k++)
            {
                var m = ms[k];
                if (m == null || m.mnemonic != "SysCall") continue;
                var sv = m.operands?.sValues;
                string s = sv != null && sv.Length > 0 ? sv[0] ?? "" : "";
                if (s.StartsWith("WC:reward")) reward = true;
                else if (s.StartsWith("WC:announce") || s.StartsWith("WC:offer")) sale = true;
            }
            return checkScenes[script] = reward && !sale;
        }

        public static bool Msg_Prefix(Core c, ref int __result)
        {
            if (!WcTest.Running)
            {
                try
                {
                    if (!SkipStory || !WcGame()) return true;
                    string k = c?.op?.sValues?[0];
                    if (!SkipStoryLine(c, k)) { if (k != null && k.Length > 0 && k[0] == 'E') StoryKept++; return true; }
                    StorySkipped++;
                    CloseAfterSkipped(c);
                    __result = 0;
                    return false;
                }
                catch (Exception e) { WcRunner.WarnOnce("story-text", e); return true; }
            }
            if (Trace > 0) Plugin.L.LogInfo("[wc trace] enter Msg");
            string key = null;
            try { key = c?.op?.sValues?[0]; } catch { }
            var text = key == null ? "" : MessageManager.Instance?.GetMessage(key, false) ?? "";
            TestMessages.Add($"{key}: {text}");
            if (key == "WC_REWARD") RewardScreenBlack = WcTest.ScreenBlack().Item1;   // the reward line's screen (the pick's test)
            // how play would treat it (the test shows none: no input headless)
            bool story = false;
            try { story = SkipStory && WcGame() && SkipStoryLine(c, key); } catch (Exception) { }
            (story ? TestStorySkipped : TestStoryShown).Add(key ?? "");
            if (!story && key != null && key.StartsWith("E")) TestStoryShownWhy.Add($"{key} ({StoryWhy}, {Integrator.Instance?.scriptName})");
            Plugin.L.LogInfo($"[wc test] Msg {key} ({(story ? "story: skipped in play" : $"shown in play: {StoryWhy}")}; script '{Integrator.Instance?.scriptName}'): {text.Replace("\n", " ")}");
            __result = 0;
            return false;
        }

        internal static readonly List<string> TestStorySkipped = new List<string>(), TestStoryShown = new List<string>();
        internal static readonly List<string> TestSelects = new List<string>();   // the menus the test answered (option keys)
        internal static readonly List<string> TestStoryShownWhy = new List<string>();   // E-lines play would show, and why

        // self-test only: answer the next Select windows without showing them
        internal static readonly Queue<int> TestAnswers = new Queue<int>();
        internal static readonly Queue<string> TestAnswerKeys = new Queue<string>();

        public static bool Select_Prefix(MainCore mc, ref int __result)
        {
            if (!WcTest.Running) return true;
            int a = 0;                                                   // unasked: the first choice
            string why = "";
            if (TestAnswerKeys.Count > 0)
            {
                // by the option's text key: the menus' options depend on the seed (a gate's extra choice)
                var key = TestAnswerKeys.Dequeue();
                var sv = mc?.op?.sValues;
                int n = 0, found = -1;
                if (sv != null)
                    for (int i = 0; i < sv.Length; i++)
                    {
                        if (string.IsNullOrEmpty(sv[i])) continue;
                        if (sv[i] == key) found = n;
                        n++;
                    }
                a = Math.Max(0, found);
                why = found < 0 ? $" ({key} not offered)" : $" ({key})";
            }
            else if (TestAnswers.Count > 0) a = TestAnswers.Dequeue();
            DataStorage.Instance?.SetMessageSelection(a);
            try { TestSelects.Add(string.Join("/", (mc?.op?.sValues ?? new string[0]).Where(v => !string.IsNullOrEmpty(v)))); } catch (Exception) { }
            Plugin.L.LogInfo($"[wc test] Select answered {a}{why} (script '{Integrator.Instance?.scriptName}')");
            __result = 0;
            return false;
        }

        static string lastCallKey = "";
        static float lastCallAt = -10;

        public static bool SystemCall_Prefix(MainCore mc, ref int __result)
        {
            string name = null;
            try
            {
                var sv = mc?.op?.sValues;
                if (sv != null && sv.Length > 0) name = sv[0];
            }
            catch { return true; }
            // WC's Veldt reward is completed in the battle itself. Suppress the vanilla
            // BattleJoin(Gau), which queues sc_e_0239_b (the Gau scene map), moves the party to
            // a fixed coordinate, and eventually deletes the Blackjack. The battle controller
            // exits normally afterward, returning to the exact world position where it began.
            if (name == "バトル加入：ガウ" && VeldtRewardEncounter())
            {
                __result = 0;
                GrantVeldtBattleReward(mc);
                return false;
            }
            if (name == null || !name.StartsWith("WC:")) return true;
            __result = 0;
            // the VM sometimes runs an instruction twice in a row (traced "ran 2x"; a reward
            // joined a character twice and the game hung): one call per script line
            int addr = -1;
            try { addr = mc.programCounter?.currentAddress ?? -1; } catch { }
            var key = $"{mc.Pointer}:{addr}:{name}";
            float t = UnityEngine.Time.realtimeSinceStartup;
            if (key == lastCallKey && t - lastCallAt < 1f) return false;
            lastCallKey = key;
            lastCallAt = t;
            try { Run(name.Substring(3), mc); }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] event call {name}: {e}"); }
            return false;
        }

        // The dried meat has two branches in the Veldt's battle script (sc_b_0239): event 00
        // (SysCall "バトル加入：ガウ" -> Current.JoinGauInBattle: Gau rejoins in battle, as after a
        // Leap) and event 01 (SysCall "ガウ初回予約" -> Current.ReserveScriptGauFirstJoin: Gau's
        // first join, which reserves sc_e_0239 on the world map; that script parks the Blackjack
        // at (1, 1), moves the party to the scene map 574 for sc_e_0239_b and ends at the Crescent
        // Mountain cave, world map point 16). A WC game's Gau never sets ScenarioFlag1 126, so
        // the reward encounter takes event 01 (the user's seed, 2026-11-11: two fades, vanilla Gau
        // on the scene map, then the cave). WC (event/veldt.py) gives the reward in the battle
        // and leaves the party where it fought: both branches do that here when it is the
        // reward encounter (Gau in the party, the check open); a Gau back from a Leap rejoins.
        internal static bool VeldtRewardEncounter()
        {
            if (!Plugin.WcMode || WcSeeds.Active == null || Flag(DataStorage.Category.kScenarioFlag4, WcGameFlag) != 1) return false;
            if (Flag(DataStorage.Category.kScenarioFlag4, 31) == 1) return false;   // the check is done
            return GauInParty();
        }

        static bool GauInParty()
        {
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            if (party == null) return false;
            foreach (var c in party) if (c != null && c.characterStatusId == 12) return true;
            return false;
        }

        public static bool JoinGauInBattle_Prefix(MainCore __0, ref int __result)
        {
            if (!VeldtRewardEncounter()) return true;   // Gau back from a Leap: the game's own rejoin
            __result = 0;
            try { GrantVeldtBattleReward(__0); }
            catch (Exception e) { WcRunner.WarnOnce("veldt-battle-reward", e); }
            return false;
        }

        internal static int GauFirstJoins;   // tests: first-join reservations a WC game turned away

        public static bool GauFirstJoin_Prefix(MainCore __0, ref int __result)
        {
            if (!Plugin.WcMode || WcSeeds.Active == null || Flag(DataStorage.Category.kScenarioFlag4, WcGameFlag) != 1) return true;
            __result = 0;
            GauFirstJoins++;
            try
            {
                if (VeldtRewardEncounter()) GrantVeldtBattleReward(__0);
                else if (!GauInParty() && IsRecruited(12))
                {
                    // Gau back from a Leap: he rejoins where the battle is, as event 00 does (a
                    // private method of the game's: by reflection)
                    AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "JoinGauInBattle")
                        ?.Invoke(null, new object[] { __0 });
                    Plugin.L.LogInfo("[wc] veldt: Gau rejoins after his Leap (no first-join scene)");
                }
                else Plugin.L.LogInfo("[wc] veldt: Gau's first-join scene skipped (WC game)");
            }
            catch (Exception e) { WcRunner.WarnOnce("veldt-first-join", e); }
            return false;
        }

        static void GrantVeldtBattleReward(MainCore mc)
        {
            if (Flag(DataStorage.Category.kScenarioFlag4, 31) == 1) return;
            Reward("veldt", 0, mc);
            SetWcFlag(31, 1);
            // a character reward joins here with room in the party (the encounter needs fewer than
            // four): no party pick follows in battle
            SetWcFlag(PickParty, 0);
            // vanilla's "Gau met at the Veldt" (sc_e_0239_b): later Leap returns rejoin without meat
            DataStorage.Instance?.Set(DataStorage.Category.kScenarioFlag1, 126, 1);
            // the battle script's Gau mode back to 1 (it exits right after); a script's locals
            // throw outside a running script (the headless test calls this directly)
            try { DataStorage.Instance?.Set(DataStorage.Category.kScriptLocalVariable, 2, 1); } catch (Exception) { }
            // its line goes in the battle's own notice window: the battle script shows WC_REWARD
            // right after this call when RewardLine is set (tools/wc_events.py veldt_battle; the
            // field's centring tag doesn't belong in that window)
            var line = MessageManager.Instance?.GetMessage("WC_REWARD", false) ?? "";
            SetText("WC_REWARD", line.Replace("<CT>", ""));
            SetWcFlag(RewardLine, 1);
            Plugin.L.LogInfo("[wc] veldt: reward granted in battle; Gau scene and relocation suppressed");
        }

        internal const int RewardLine = 129;   // ScenarioFlag4: a battle reward's line to show (tools/wc_events.py REWARD_LINE)

        internal static int PickRecruit = -1, PartyRefreshes, PartyPicks;
        internal static bool RewardScreenBlack, PickScreenBlack;   // tests: the screen at the reward line and after the pick
        internal static int PickLeader = -1;                       // tests: the leader right after the pick

        // the party select screen (OrganizeOneParty, as the pick after a character reward opens
        // it): a headless test can't work a menu, so a test run stands in for the player, who puts
        // the new recruit in front
        public static bool Organize_Prefix(ref int __result)
        {
            if (!WcTest.Running) return true;
            PartyPicks++;
            try
            {
                var list = UserDataManager.Instance()?.corpsList;
                int cid = Last.Interpreter.Instructions.SystemCall.Current.GetCharacterIdFromStatusId(PickRecruit);
                int at = -1;
                for (int i = 0; list != null && i < list.Count; i++) if (list[i].CharacterId == cid) at = i;
                if (at < 0 && list != null && list.Count > 0)
                {
                    // in reserve (the party was full): the player swaps them in for the last member
                    at = list.Count - 1;
                    list[at].CharacterId = cid;
                }
                if (at > 0)
                {
                    var corps = list[at];
                    list.RemoveAt(at);
                    list.Insert(0, corps);
                }
                var lead = UserDataManager.Instance()?.GetPartyMemberData();
                Plugin.L.LogInfo($"[wc test] party pick: the screen stood in for, character {PickRecruit} (id {cid}, was {at}) in front: leader {(lead != null && lead.Count > 0 ? lead[0].characterStatusId : -1)}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc test] party pick stand-in: {e.Message}"); }
            __result = 0;
            return false;
        }

        // after the party pick (a character reward): the field's party figure redrawn for the
        // party's new leader where it stands (the PR's own party points reload the map instead)
        static void PartyRefresh()
        {
            var fc = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController;
            if (fc == null) { Plugin.L.LogWarning("[wc] party refresh: no field"); return; }
            // (private in the game: by name)
            AccessTools.Method(typeof(Last.Map.FieldController), "ChangePartyEntityAsset")?.Invoke(fc, null);
            PartyRefreshes++;
            if (WcTest.Running) PickScreenBlack = WcTest.ScreenBlack().Item1;
            var lead = UserDataManager.Instance()?.GetPartyMemberData();
            PickLeader = lead != null && lead.Count > 0 ? lead[0].characterStatusId : -1;
            Plugin.L.LogInfo($"[wc] party picked: leader {(lead != null && lead.Count > 0 ? lead[0].characterStatusId : -1)}");
        }

        // a chest the PR keeps empty that a seed filled with gil (tools/port_chests.py: its script
        // gives no gil of its own): the gil, and WC_REWARD says so in the game's gil chest line
        static void Gil(int amount)
        {
            new OwnedItemClient().AddGil(amount);
            var line = MessageManager.Instance?.GetMessage("T0001_03_01", false);
            SetText("WC_REWARD", !string.IsNullOrEmpty(line) && line.Contains("(NUM)") ? line.Replace("(NUM)", amount.ToString()) : $"<CT>Obtained {amount} gil!");
            Plugin.L.LogInfo($"[wc] chest: {amount} gil");
        }

        // ... and with an item: given as a check's reward gives one (the script's own GetItem gave
        // nothing in the headless test), WC_REWARD names it
        static void ChestItem(int content, int num)
        {
            new OwnedItemClient().AddOwnedItem(content, num);
            SetText("WC_REWARD", $"<CT>Obtained {ContentName(content)}!");
            Plugin.L.LogInfo($"[wc] chest: content {content} x{num}");
        }

        static void Run(string call, MainCore mc)
        {
            var args = call.Split(' ');
            switch (args[0])
            {
                case "offer": Offer(args[1], int.Parse(args[2]), args[3]); return;
                case "pay": Pay(args[1], int.Parse(args[2])); return;
                case "gil": Gil(int.Parse(args[1])); return;
                case "party_refresh": PartyRefresh(); return;
                case "item": ChestItem(int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 1); return;
                case "reward": Reward(args[1], args.Length > 2 ? int.Parse(args[2]) : 0, mc); return;
                case "gate": Gate(args[1]); return;
                // a flag the seed has (WC:has -fbs): ScenarioFlag4 Scratch = 1 if so
                case "has":
                    {
                        bool has = WcSeeds.Active?.Flags.Split(' ').Contains(args[1]) == true;
                        SetWcFlag(Scratch, has ? 1 : 0);
                        Plugin.L.LogInfo($"[wc] seed has {args[1]}: {has} (script '{Integrator.Instance?.scriptName}')");
                        return;
                    }
                case "is_asset": SetWcFlag(Scratch, IsAsset(int.Parse(args[1]), int.Parse(args[2])) ? 1 : 0); return;
                case "announce": Announce(args[1], int.Parse(args[2]), args[3]); return;
                case "hasflag": SetWcFlag(Scratch, WcSeeds.Active?.Flags.Split(' ').Contains(args[1]) == true ? 1 : 0); return;
                case "brl":
                    // -brl: Bum Rush once a Blitz holder knows the other seven (Raging Fist .. 138)
                    bool brl = WcSeeds.Active?.Flags.Split(' ').Contains("-brl") == true;
                    var blitzer = WcSkills.Holder(15);
                    SetWcFlag(Scratch, !brl || (blitzer != null && Enumerable.Range(132, 7).All(id => WcSkills.Knows(blitzer, id))) ? 1 : 0);
                    return;
                case "auction_hint": AuctionHint(args[1], args.Skip(2).Select(int.Parse).ToArray()); return;
                case "clock":
                    {
                        // WC's Zozo clock (-rc): the menu's choice for hand 0/1/2 is the seed's
                        int hand = int.Parse(args[1]);
                        var clock = WcSeeds.Active?.Clock ?? new[] { 2, 0, 4 };
                        int got = DataStorage.Instance.GetMessageSelection();
                        SetWcFlag(Scratch, hand < clock.Length && got == clock[hand] ? 1 : 0);
                        LastClock = $"clock hand {hand}: chose {got}, the seed's {(hand < clock.Length ? clock[hand] : -1)} ({WcSeeds.Active?.ClockTime})";
                        return;
                    }
            }
            switch (call)
            {
                case "start": Start(); break;
                case "final_kefka": FinalKefka(); break;
                case "kt_skip": KtSkip(); break;
                case "party": Party(mc); break;
                case "to_wor": MoveAirship(from: 4, to: 5, map: 2); break;
                case "to_wob": MoveAirship(from: 5, to: 4, map: 1); break;
                default: Plugin.L.LogWarning($"[wc] unknown event call WC:{call}"); break;
            }
        }

        // --- checks ---------------------------------------------------------------------------

        static readonly string[] NameTags =
        {
            null, "(TINA)", "(ROCK)", "(KAIEN)", "(SHADOW)", "(EDGAR)", "(MASH)", "(CELES)", "(STRAGUS)",
            "(RELM)", "(SETZER)", "(MOG)", "(GAU)", "(GOGO)", "(UMARO)",
        };
        static readonly Dictionary<string, string> originalText = new Dictionary<string, string>();
        internal static string LastReward = "";

        static WcSeeds.Check CheckOf(string module)
        {
            var s = WcSeeds.Active;
            if (s == null || !s.Checks.TryGetValue(module, out var c)) { Plugin.L.LogWarning($"[wc] no seed data for check {module}"); return null; }
            return c;
        }

        internal static void SetText(string key, string text)
        {
            var dic = MessageManager.Instance?.messageDictionary;
            if (dic == null) return;
            if (!originalText.ContainsKey(key)) originalText[key] = dic.ContainsKey(key) ? dic[key] : "";
            dic[key] = text;
        }

        static void Offer(string module, int world, string key)
        {
            var c = CheckOf(module);
            if (c == null || c.Prices.Length <= world) return;
            SetText(key, originalText.TryGetValue(key, out var o) ? o : MessageManager.Instance.GetMessage(key, false));
            var text = originalText[key];
            // the PR's line names its price once ("For 3000 gil", "how's 10 gil"): the seed's instead
            var re = new System.Text.RegularExpressions.Regex(@"\d[\d,]*(?= gil)");
            SetText(key, re.Replace(text, c.Prices[world].ToString(), 1));
        }

        static void Pay(string module, int world)
        {
            var c = CheckOf(module);
            int price = c != null && c.Prices.Length > world ? c.Prices[world] : 0;
            var udm = UserDataManager.Instance();
            bool paid = udm.OwendGil >= price;
            if (paid) new OwnedItemClient().RemoveGil(price);
            SetVar(0, paid ? 1 : 0);
            Plugin.L.LogInfo($"[wc] {module}: price {price}, {(paid ? "paid" : "not enough gil")} (now {udm.OwendGil})");
        }

        // the auctioneer's "...a piece of Golem magicite!": the seed's n-th reward of the check instead
        static void Announce(string module, int n, string key)
        {
            var c = CheckOf(module);
            if (c == null || c.Rewards.Count <= n) return;
            var (kind, id) = c.Rewards[n];
            SetText(key, kind == "esper" ? $"...a piece of {ContentName(id)} magicite!" : $"...the {ContentName(id)}!");
        }

        // WC's -adeh (auction door esper hint): the auction's greeting says whether an esper is
        // still for sale (one of the auction's rewards is an esper whose check flag isn't set)
        static void AuctionHint(string key, int[] doneFlags)
        {
            var s = WcSeeds.Active;
            var c = CheckOf("auction_house");
            if (!originalText.ContainsKey(key)) SetText(key, MessageManager.Instance.GetMessage(key, false));
            bool hint = s != null && s.Flags.Split(' ').Contains("-adeh");
            bool left = false;
            for (int n = 0; c != null && n < c.Rewards.Count && n < doneFlags.Length; n++)
                if (c.Rewards[n].Kind == "esper" && Flag(DataStorage.Category.kScenarioFlag4, doneFlags[n]) != 1) left = true;
            SetText(key, hint && left ? "See anything you want?\nGlowing stones still available!" : originalText[key]);
        }

        // WC's "Unlock Final Kefka" objective result: ScenarioFlag4 131 = 1 when the seed has no such
        // objective or one is complete (conditions the port can't track yet count as met, so a seed
        // never locks the ending for good), else 0 with the reason in WC_REWARD
        static void FinalKefka()
        {
            var objs = WcSeeds.Active?.Objectives.Where(o => o.ResultName == "Unlock Final Kefka").ToList();
            bool open = objs == null || objs.Count == 0 || objs.Any(o => o.Conditions.Count(c => WcTrack.Met(c) != false) >= o.Required);
            SetWcFlag(Scratch, open ? 1 : 0);
            if (!open)
                SetText("WC_REWARD", $"<CT>A force pushes you back...\n<CT>Objective {objs[0].Letter} ({objs[0].Result}) isn't complete yet.");
            Plugin.L.LogInfo($"[wc] final Kefka: {(open ? "open" : "sealed")} ({objs?.Count ?? 0} unlocking objectives)");
        }

        // WC's "Unlock KT Skip": ScenarioFlag4 131 = 1 when such an objective is complete and the final
        // Kefka is open too (else the party would wait at the top with no way on)
        static void KtSkip()
        {
            var s = WcSeeds.Active;
            bool skip = false;
            for (int i = 0; s != null && i < s.Objectives.Count && i < 16; i++)
                if (s.Objectives[i].ResultName == "Unlock KT Skip" && WcObjectives.IsComplete(i)) skip = true;
            if (skip)
            {
                FinalKefka();   // sets the scratch flag: 1 = open
                skip = Flag(DataStorage.Category.kScenarioFlag4, Scratch) == 1;
            }
            SetWcFlag(Scratch, skip ? 1 : 0);
            Plugin.L.LogInfo($"[wc] KT skip: {(skip ? "offered" : "not offered")}");
        }

        internal static string ContentName(int contentId)
        {
            var row = Last.Data.Master.MasterManager.Instance.GetData<Last.Data.Master.Content>(contentId);
            return row == null ? $"#{contentId}" : MessageManager.Instance.GetMessage(row.MesIdName, false);
        }

        static string lastRewardKey = "";
        static float lastRewardAt = -10;

        static void Reward(string module, int n, MainCore mc)
        {
            // the VM may run one SysCall twice in a row (traced: "ran 2x"): give once
            var key = $"{module}/{n}";
            float t = UnityEngine.Time.realtimeSinceStartup;
            if (key == lastRewardKey && t - lastRewardAt < 3) { Plugin.L.LogInfo($"[wc] reward {key} already given just now"); return; }
            lastRewardKey = key;
            lastRewardAt = t;
            var c = CheckOf(module);
            if (c == null || c.Rewards.Count <= n) { SetText("WC_REWARD", "<CT>Nothing here..."); return; }
            if (module == "auction_house") WcTrack.SetQuest("Win An Auction");   // a won bid (WC sets its quest bit there)
            var (kind, id) = c.Rewards[n];
            var client = new OwnedItemClient();
            string text;
            if (kind == "character")
            {
                Join(id);
                text = $"<CT>{NameTags[id]} joined the party!";
                SetWcFlag(PickParty, 1);   // WC's RecruitAndSelectParty: the script opens the party pick
                PickRecruit = id;
            }
            else if (kind == "esper")
            {
                var udm = UserDataManager.Instance();
                if (!udm.OwnedMagicStoneList.Contains(id))
                {
                    client.AddMagicStone(id);
                    if (!udm.OwnedMagicStoneList.Contains(id)) udm.OwnedMagicStoneList.Add(id);
                }
                text = $"<CT>Obtained {ContentName(id)} magicite!";
            }
            else
            {
                client.AddOwnedItem(id, 1);
                text = $"<CT>Obtained {ContentName(id)}!";
            }
            SetText("WC_REWARD", text);
            LastReward = $"{module}: {kind} {id} ({text})";
            Plugin.L.LogInfo($"[wc] check {LastReward}");
        }

        internal const int Recruited = 100, Scratch = 131;   // ScenarioFlag4 (tools/wc_events.py)
        internal const int PickParty = 99;                    // ScenarioFlag4: a character reward (the party pick follows)
        internal const int WcGameFlag = 0;                   // ScenarioFlag4 0: a WC game

        static bool WcGame() => Plugin.WcMode && Flag(DataStorage.Category.kScenarioFlag4, 0) == 1;

        static void SetWcFlag(int index, int value) =>
            DataStorage.Instance?.Set(DataStorage.Category.kScenarioFlag4, index, value);

        public static void Join_Postfix(int characterStatusId)
        {
            try
            {
                if (characterStatusId >= 1 && characterStatusId <= 14 && WcGame())
                {
                    bool first = !IsRecruited(characterStatusId);
                    TraceHp(characterStatusId, "the join");
                    SetWcFlag(Recruited + characterStatusId, 1);
                    if (first) FreshRecruit(characterStatusId);         // WC's recruit, not the PR's extras
                    if (first) WcRecruits.Joined(characterStatusId);   // -sal: the first time only (WC's recruit)
                    StartSpells(characterStatusId);
                    StartName(characterStatusId);
                    WcObjectives.Joined(characterStatusId);
                    TraceHp(characterStatusId, "WC's recruit");
                }
                JoinCount[characterStatusId] = JoinCount.GetValueOrDefault(characterStatusId) + 1;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] join hook: {e.Message}"); }
        }

        internal static readonly Dictionary<int, int> JoinCount = new Dictionary<int, int>();   // joins this session (tests)

        // WC's -etr (random event timers): a countdown the seed changed starts with WC's time (the
        // collapsing house, the opera, the Floating Continent; keyed by the script it runs at zero)
        public static void Timer_Prefix(MainCore mc)
        {
            try
            {
                var s = WcSeeds.Active;
                if (s == null || s.Timers.Count == 0 || !WcGame() || !s.Flags.Split(' ').Contains("-etr")) return;
                var op = mc?.op;
                var target = op?.sValues?[0];
                if (target == null || !s.Timers.TryGetValue(target, out int seconds) || seconds <= 0 || op.rValues == null) return;
                if (Math.Abs(op.rValues[0] - seconds) < 0.5f) return;
                Plugin.L.LogInfo($"[wc] -etr: {target}'s countdown {op.rValues[0]} s -> {seconds} s");
                op.rValues[0] = seconds;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] timer hook: {e.Message}"); }
        }

        // WC's -name A.B.C...: the 14 characters' names (WC's order, Terra first; empty = default)
        internal static string StartName(int statusId)
        {
            try
            {
                var s = WcSeeds.Active;
                if (s == null) return "";
                var tokens = Tokens.Split(s.Flags);
                int at = tokens.IndexOf("-name");
                if (at < 0 || at + 1 >= tokens.Count) return "";
                var names = tokens[at + 1].Split('.');
                if (statusId < 1 || statusId > names.Length || names[statusId - 1].Length == 0) return "";
                var list = UserDataManager.Instance()?.OwnedCharacterList;
                if (list == null) return "";
                foreach (var c in list)
                    if (c != null && c.characterStatusId == statusId && c.Name != names[statusId - 1])
                    {
                        c.Name = names[statusId - 1];
                        Plugin.L.LogInfo($"[wc] character {statusId} named {c.Name} (-name)");
                        return c.Name;
                    }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] -name for character {statusId}: {e.Message}"); }
            return "";
        }

        // WC's -scan / -warp: every character knows Scan / Warp (the PR's Libra, ability 64, and
        // Teleport, 82), taught at 100% when they join (WC's data side, 0 MP, is the data port's)
        internal static string StartSpells(int statusId)
        {
            var s = WcSeeds.Active;
            if (s == null) return "";
            var flags = s.Flags.Split(' ');
            var teach = new List<int>();
            if (flags.Contains("-scan")) teach.Add(64);
            if (flags.Contains("-warp")) teach.Add(82);
            if (teach.Count == 0) return "";
            var done = new List<string>();
            try
            {
                var list = UserDataManager.Instance()?.OwnedCharacterList;   // the live list (private in the game)
                if (list == null) return "";
                foreach (var c in list)
                {
                    if (c == null || c.characterStatusId != statusId) continue;
                    foreach (var id in teach)
                    {
                        Last.Data.User.OwnedAbility owned = null;
                        var have = c.OwnedAbilityList;
                        if (have != null)
                            foreach (var a in have)
                                if (a?.Ability != null && a.Ability.Id == id) owned = a;
                        owned ??= new OwnedCharacterClient().AddAbility(c.Id, id);
                        if (owned == null) continue;
                        owned.SkillLevel = 100;
                        done.Add(id.ToString());
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc] -scan/-warp for character {statusId}: {e.Message}"); }
            if (done.Count > 0) Plugin.L.LogInfo($"[wc] character {statusId} knows abilities {string.Join(", ", done)} (-scan/-warp)");
            return string.Join(",", done);
        }

        internal static bool IsRecruited(int statusId) => Flag(DataStorage.Category.kScenarioFlag4, Recruited + statusId) == 1;

        // A character's first join in a WC game makes what WC's recruit makes (the SNES game, read
        // in Mesen: the start party at full HP, its level's HP and MP). The PR's JoinCharacter adds
        // two things of its own for a first-time joiner:
        //  * the HP / MP growth up to a level it picks (AddtionalMaxHp / Mp; the level itself stays):
        //    the user's Deck seed (2026-11-06) had Sabin at 86/209 HP at level 3 (the growth of
        //    levels 4-9), Gau 70/167, Mog 70/121, where the SNES has 86/86, 70/70, 70/70. Gone
        //    here; -sal still raises a recruit to WC's average level after this (WcRecruits);
        //  * the character's own starting skills (Gau joins with vanilla's 9 starting rages,
        //    Strago with its lores). WC's -srr / -slr / -sdr replace those sets with the seed's
        //    (data/rages.py start_random_rages clears the bits first): un-learned here, and the
        //    seed's go to the command's holders (WcRages.StartSkills).
        internal static string LastRecruit = "";

        static void FreshRecruit(int statusId)
        {
            try
            {
                var list = UserDataManager.Instance()?.OwnedCharacterList;
                if (list == null) return;
                Last.Data.User.OwnedCharacterData c = null;
                foreach (var x in list) if (x != null && x.characterStatusId == statusId) c = x;
                if (c == null) return;
                var p = c.Parameter;
                int hp = p.AddtionalMaxHp, mp = p.AddtionalMaxMp;
                p.AddtionalMaxHp = 0;
                p.AddtionalMaxMp = 0;
                p.CurrentHP = p.ConfirmedMaxHp();
                p.CurrentMP = p.ConfirmedMaxMp();
                var flags = WcSeeds.Active?.Flags.Split(' ') ?? new string[0];
                var dropped = new List<string>();
                foreach (var (kind, flag, lo, hi) in new[] { ("rages", "-srr", 800, 1054), ("lores", "-slr", 140, 163), ("dances", "-sdr", 164, 171) })
                {
                    if (!flags.Contains(flag) || c.OwnedAbilityList == null) continue;
                    int n = 0;
                    foreach (var a in c.OwnedAbilityList)
                        if (a?.Ability != null && a.Ability.Id >= lo && a.Ability.Id <= hi && a.SkillLevel > 0) { a.SkillLevel = 0; n++; }
                    if (n > 0) dropped.Add($"{n} {kind}");
                }
                // WC's MagiTek menu is the 4 basic beams for everyone, Terra too, until "Magitek
                // Upgrade" (battle/magitek_upgrade.py); the PR gives Terra all 8. The upgrade, once
                // done, teaches them back (WcObjectives.MagitekUpgrade, every few seconds)
                if (statusId == 1 && c.OwnedAbilityList != null)
                {
                    int n = 0;
                    foreach (var a in c.OwnedAbilityList)
                        if (a?.Ability != null && WcObjectives.MagitekTerra.Contains(a.Ability.Id) && a.SkillLevel > 0) { a.SkillLevel = 0; n++; }
                    if (n > 0) dropped.Add($"{n} Magitek moves past WC's basic 4");
                }
                LastRecruit = $"character {statusId}: the PR's join extras HP +{hp}, MP +{mp} removed (HP {p.CurrentHP}/{p.ConfirmedMaxHp()}, MP {p.CurrentMP}/{p.ConfirmedMaxMp()})" +
                              (dropped.Count > 0 ? $"; its own starting {string.Join(", ", dropped)} un-learned (the seed's instead)" : "");
                Plugin.L.LogInfo("[wc] recruit: " + LastRecruit);
            }
            catch (Exception e) { WcRunner.WarnOnce("fresh-recruit", e); }
        }

        // test runs: a character's level, HP and MP (base + the game's additional) after a step
        internal static void TraceHp(int statusId, string step)
        {
            if (!WcTest.Running) return;
            try
            {
                var list = UserDataManager.Instance()?.OwnedCharacterList;
                if (list == null) return;
                foreach (var c in list)
                {
                    if (c == null || c.characterStatusId != statusId) continue;
                    var p = c.Parameter;
                    int rages = 0, ragesFull = 0;
                    if (c.OwnedAbilityList != null)
                        foreach (var a in c.OwnedAbilityList)
                            if (a?.Ability != null && a.Ability.Id >= 800 && a.Ability.Id <= 1054) { rages++; if (a.SkillLevel >= 100) ragesFull++; }
                    Plugin.L.LogInfo($"[wc test] hp {statusId} after {step}: Lv {p.BaseLevel} HP {p.CurrentHP}/{p.ConfirmedMaxHp()} (base {p.BaseMaxHp} +{p.AddtionalMaxHp}) " +
                                     $"MP {p.CurrentMP}/{p.ConfirmedMaxMp()} (base {p.BaseMaxMp} +{p.AddtionalMaxMp}); rages {rages} ({ragesFull} at 100)");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc test] hp trace: {e.Message}"); }
        }

        // a check's character gate met (or no gating): what WC:gate answers
        internal static bool GateOpen(string module)
        {
            var s = WcSeeds.Active;
            var c = CheckOf(module);
            bool gating = s != null && s.Flags.Split(' ').Contains("-cg");
            return !gating || c == null || c.Gate <= 0 || IsRecruited(c.Gate);
        }

        // WC:is_asset <entity> <mapobject>: ScenarioFlag4 Scratch = 1 if the map's entity shows that
        // art. A check NPC a seed re-skins (tools/wc_npcs.py) lacks the frames its map's scripts cue
        // by name (the Ancient Castle's queen statue: 'stone', frame Stone_00 of the queen's sheet);
        // the script cues them only while the entity still shows its vanilla art
        static bool IsAsset(int entity, int asset)
        {
            int have = -1;
            try
            {
                var e = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController?.GetFieldEntity(entity);
                have = e?.Property?.TryCast<Last.Map.PropertyNpc>()?.AssetId ?? -1;
            }
            catch (Exception) { }
            Plugin.L.LogInfo($"[wc] entity {entity} shows art {have} (the script's {asset}; script '{Integrator.Instance?.scriptName}')");
            return have == asset;
        }

        internal static void Gate(string module)
        {
            var s = WcSeeds.Active;
            var c = CheckOf(module);
            bool gating = s != null && s.Flags.Split(' ').Contains("-cg");
            bool open = GateOpen(module);
            SetWcFlag(Scratch, open ? 1 : 0);
            Plugin.L.LogInfo($"[wc] gate {module}: {(open ? "open" : "closed")} (character gating {(gating ? "on" : "off")}, gate {c?.Gate})");
        }

        static void SetVar(int index, int value)
        {
            var ds = DataStorage.Instance;
            if (ds != null) ds.Set(DataStorage.Category.kMapLocalVariable, index, value);
        }

        internal static float NewGameChosenAt = -1000;   // WcTitleMenu: New Game picked on the title

        static void Start()
        {
            var s = WcSeeds.Active;
            // the opening runs for a New Game and for the title's attract demo: WC's start only for
            // a New Game picked in the last few minutes (the vanilla script tells them apart by
            // map-local 7, which is 1 in the demo)
            bool newGame = UnityEngine.Time.realtimeSinceStartup - NewGameChosenAt < 300;
            NewGameChosenAt = -1000;
            if (Plugin.WcMode && s != null && !newGame)
                Plugin.L.LogInfo("[wc] the opening without a New Game (the title's demo): vanilla");
            bool wc = Plugin.WcMode && s != null && newGame;
            SetVar(StartVar, wc ? 1 : 0);
            StartRan = wc;
            if (!wc)
            {
                LastStart = "no seed: vanilla opening";
                Plugin.L.LogInfo("[wc] New Game without a seed: the vanilla opening");
                return;
            }
            var udm = UserDataManager.Instance();
            int before = udm.OwendGil;
            var grant = WcSeeds.GrantStart(s, setGil: true);
            LastStart = $"seed {WcSeeds.Describe(s)}: {grant}; gil {before} -> {udm.OwendGil}";
            Plugin.L.LogInfo($"[wc] WC start: {LastStart}");
            // WC starts with Mobliz WoR's double Morph duration (its init bits: battle bit 0x39). The
            // PR has no flag of its own for it: TranceConditionFunction.Start halves Morph's gauge
            // unless "after the collapse" (ScenarioFlag2 41; its constant 297 is that flag's index
            // in the game's one 1024-flag array, 256 + 41). An earlier version set "ScenarioFlag1 297"
            // here, which is the same bit: every WC game began after the collapse (Narshe in its WoR
            // state, its exits to the WoR; the user's playtest, 2026-11-06). Now Morph_Postfix gives
            // a WC game the full gauge, and the flag stays with the world (WorldSync).
            DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, CollapseFlag, 0);
            Plugin.L.LogInfo("[wc] WC start: double Morph duration (Morph_Postfix), the World of Balance");
        }

        // Morph's gauge (TranceConditionFunction.Start, decompiled tools/decomp/morph): the
        // character's morph points x rateValue, 1 after the collapse, else 0.5. A WC game always has
        // the full gauge.
        internal static int MorphsDoubled;

        public static void Morph_Postfix(global::Last.Battle.TranceConditionFunction __instance)
        {
            try
            {
                if (!WcGame() || __instance == null || __instance.rateValue >= 1f || __instance.isPermanent) return;
                float full = __instance.startTranceGageValue / Math.Max(0.01f, __instance.rateValue);
                __instance.rateValue = 1f;
                __instance.startTranceGageValue = full;
                __instance.tranceGageValue = full;
                __instance.SetTranceGageValue(full, true);
                MorphsDoubled++;
            }
            catch (Exception e) { WcRunner.WarnOnce("morph", e); }
        }

        static void Party(MainCore mc)
        {
            var s = WcSeeds.Active;
            if (s == null) return;
            var joined = new List<string>();
            foreach (var id in s.StartCharacters)
                if (Join(id)) joined.Add(id.ToString());
            Plugin.L.LogInfo($"[wc] WC start party: characters {string.Join(" ", joined)} joined");
        }

        static Last.Data.User.OwnedTransportationData Vehicle(int id)
        {
            var ts = UserDataManager.Instance()?.OwnedTransportationList;
            if (ts != null) foreach (var t in ts) if (t.Id == id) return t;
            return null;
        }

        internal static string LastMove = "";
        internal static string LastClock = "";

        // In a WC game the world map you're on decides the merged towns' state: "after the
        // collapse" (ScenarioFlag2 41: Narshe, Figaro... in their WoR state, and their exits to the
        // WoR) and "after the Falcon" (97) are set on the WoR map and cleared on the WoB map, as WC
        // keeps IN_WOR (the wheels set them when they switch worlds; this catches any other way).
        // And this world's airship (the Blackjack in the WoB, the Falcon in the WoR) is on this map:
        // if not, it comes to the party.
        internal static string LastSync = "";

        internal static string WorldSync()
        {
            if (!Plugin.WcMode || WcSeeds.Active == null || Flag(DataStorage.Category.kScenarioFlag4, WcGameFlag) != 1) return "";
            var udm = UserDataManager.Instance();
            if (udm == null || EventBusy()) return "";
            int map = udm.CurrentMapId;
            var ds = DataStorage.Instance;
            var done = new List<string>();
            // a save from the double-Morph bug (above): "after the collapse" without "after the
            // Falcon" (a world switch always sets both); back to the WoB's state
            if (map != WorMap && ds.Get(DataStorage.Category.kScenarioFlag2, CollapseFlag) == 1 &&
                ds.Get(DataStorage.Category.kScenarioFlag2, FalconFlag) == 0)
            {
                ds.Set(DataStorage.Category.kScenarioFlag2, CollapseFlag, 0);
                done.Add("ScenarioFlag2 41 -> 0 (a save from the double-Morph bug)");
            }
            if (map != WobMap && map != WorMap)
            {
                if (done.Count > 0) { LastSync = $"world sync on map {map}: {string.Join("; ", done)}"; Plugin.L.LogInfo("[wc] " + LastSync); }
                return done.Count > 0 ? LastSync : "";
            }
            int want = map == WorMap ? 1 : 0;
            foreach (var f in new[] { CollapseFlag, FalconFlag })
                if (ds.Get(DataStorage.Category.kScenarioFlag2, f) != want)
                {
                    ds.Set(DataStorage.Category.kScenarioFlag2, f, want);
                    done.Add($"ScenarioFlag2 {f} -> {want}");
                }
            var ship = Vehicle(map == WorMap ? FalconId : BlackjackId);
            if (ship != null && (ship.MapId != map || !ship.Enable))
            {
                var cell = PlayerCell();
                if (cell.HasValue)
                {
                    ship.MapId = map;
                    ship.Position = cell.Value;
                    ship.SetDataStorageFlag(true);
                    done.Add($"airship {ship.Id} to map {map} at {cell.Value.x:0},{cell.Value.y:0}");
                }
            }
            // SetVehicle can leave the transport visible and enabled in memory while its saved
            // release flag is clear (the Serpent Trench arrival did this to the Blackjack). The
            // world-map prompt reads the flag, so repair it independently of position/Enable.
            int releaseFlag = 131 + (map == WorMap ? FalconId : BlackjackId); // TreasureFlag4 135/136
            if (ds.Get(DataStorage.Category.kTreasureFlag4, releaseFlag) != 1)
            {
                ship?.SetDataStorageFlag(true);
                ds.Set(DataStorage.Category.kTreasureFlag4, releaseFlag, 1);
                done.Add($"airship release flag {releaseFlag} -> 1");
            }
            // An interim migration moved the Blackjack one tile north of Nikeah. Restore it to
            // Nikeah's world-map exit: PR airships are boarded while standing on their own tile.
            if (map == WobMap && ship != null && ship.MapId == WobMap && ship.Enable &&
                UnityEngine.Mathf.RoundToInt(ship.Position.x) == 116 &&
                UnityEngine.Mathf.RoundToInt(ship.Position.y) == 60)
            {
                ship.Position = new UnityEngine.Vector3(116, 61, ship.Position.z);
                ship.SetDataStorageFlag(true);
                udm.SetTransportationData(ship);
                done.Add("Blackjack restored to Nikeah exit 116,60 -> 116,61");
            }
            // Saves made by the old layer-3 build have a live, normally registered Blackjack at
            // native depth 149 while Nikeah's world-map player is at 349. Repair the saved row and
            // live transforms once per map object. Do not call TransportationEnabled here: map
            // setup already registered this enabled ship, and enabling it again duplicates its
            // native boarding behavior.
            if (map == WobMap && ship != null && ship.MapId == WobMap && ship.Enable)
            {
                int sx = UnityEngine.Mathf.RoundToInt(ship.Position.x);
                int sy = UnityEngine.Mathf.RoundToInt(ship.Position.y);
                if (sx >= 104 && sx <= 128 && sy >= 48 && sy <= 72)
                {
                    var fc = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController;
                    var live = fc?.Last_Map_IEntityAccessor_GetTransportation(BlackjackId);
                    var ptr = live?.MapObject?.Pointer ?? IntPtr.Zero;
                    Last.Map.TransportationInfo riding = null;
                    try { riding = fc?.Last_Map_IEntityAccessor_GetCurrentTransportation(); } catch { }
                    if (fc?.player != null && riding?.Id != BlackjackId && ptr != IntPtr.Zero && ptr != LastNikeahRepairedObject)
                    {
                        // Bad historical builds stored layer 3's native Z (149). Nikeah's exit
                        // player is on layer 1/native Z 349. TransportationEnabled can preserve
                        // the stale live transform after SetData, so repair both representations.
                        float depth = fc.player?.transform.localPosition.z ?? 349f;
                        if (!UnityEngine.Mathf.Approximately(ship.Position.z, depth))
                        {
                            float oldDepth = ship.Position.z;
                            ship.Position = new UnityEngine.Vector3(sx, sy, depth);
                            ship.SetDataStorageFlag(true);
                            udm.SetTransportationData(ship);
                            done.Add($"Blackjack depth {oldDepth:0} -> {depth:0}");
                        }
                        if (live?.MapObject != null)
                        {
                            var p = live.MapObject.transform.localPosition;
                            live.MapObject.transform.localPosition = new UnityEngine.Vector3(p.x, p.y, depth);
                        }
                        if (live?.ColliderObject != null)
                        {
                            var p = live.ColliderObject.transform.localPosition;
                            live.ColliderObject.transform.localPosition = new UnityEngine.Vector3(p.x, p.y, depth);
                        }
                        LastNikeahRepairedObject = ptr;
                        done.Add("Blackjack live depth repaired");
                    }
                }
            }
            LogNikeahTransportation(ship);
            if (done.Count == 0) return "";
            LastSync = $"world sync on map {map}: {string.Join("; ", done)}";
            Plugin.L.LogInfo("[wc] " + LastSync);
            return LastSync;
        }

        static string LastNikeahTransportation = "";
        static IntPtr LastNikeahRepairedObject = IntPtr.Zero;

        // Read-only diagnosis for old saves made by the bad Serpent SetVehicle call. Keep this
        // until their native transport row is migrated: it identifies whether the missing link
        // is the saved row, live TransportationInfo, map object, collider, or action targeting.
        static void LogNikeahTransportation(Last.Data.User.OwnedTransportationData ship)
        {
            try
            {
                if (ship == null || ship.Id != BlackjackId || ship.MapId != WobMap || !ship.Enable) return;
                int sx = UnityEngine.Mathf.RoundToInt(ship.Position.x);
                int sy = UnityEngine.Mathf.RoundToInt(ship.Position.y);
                if (sx < 104 || sx > 128 || sy < 48 || sy > 72) return;
                var fc = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController;
                var live = fc?.Last_Map_IEntityAccessor_GetTransportation(BlackjackId);
                var obj = live?.MapObject;
                var interactive = obj?.TryCast<Last.Entity.Field.IInteractiveEntity>();
                var player = fc?.player;
                string playerCell = "none";
                int distance = 999;
                if (fc != null && player != null)
                {
                    var p = fc.ConvertWorldPositionToCellPosition(player.transform.position);
                    int px = UnityEngine.Mathf.RoundToInt(p.x), py = UnityEngine.Mathf.RoundToInt(p.y);
                    var facing = player.GetNormalizedDirectionVector();
                    var lp = player.transform.localPosition;
                    playerCell = $"{px},{py},{p.z:0.##}/layer {player.gameObject.layer}/local {lp.x:0.##},{lp.y:0.##},{lp.z:0.##}/facing {facing.x:0},{facing.y:0}";
                    distance = Math.Abs(px - sx) + Math.Abs(py - sy);
                }
                if (distance > 5) return;
                string objectState = obj == null ? "null" :
                    $"{obj.GetType().FullName} ptr {obj.Pointer} active {obj.gameObject.activeInHierarchy} layer {obj.gameObject.layer} " +
                    $"local {obj.transform.localPosition.x:0.##},{obj.transform.localPosition.y:0.##},{obj.transform.localPosition.z:0.##} " +
                    $"world {obj.transform.position.x:0.##},{obj.transform.position.y:0.##},{obj.transform.position.z:0.##} " +
                    $"property {(obj.Property == null ? "null" : $"entity {obj.Property.EntityId} object {obj.Property.ObjectId}/{obj.Property.ObjectType} myLayer {obj.Property.MyLayer}")}";
                string interactiveState = interactive == null ? "null" :
                    $"mask {interactive.AcceptActionDirection} can {interactive.CanAction} allLayer {interactive.AllLayerTarget} eye {interactive.EyeContact}";
                string terrain = "unavailable";
                if (fc?.transportation != null)
                {
                    string Cell(int x, int y)
                    {
                        int a = fc.GetCellAttribute(new UnityEngine.Vector2(x, y));
                        return $"{x},{y}=attr{a}/land{fc.transportation.CheckLandingList(BlackjackId, a)}";
                    }
                    terrain = string.Join(" ", new[] { Cell(sx, sy - 1), Cell(sx + 1, sy), Cell(sx, sy + 1), Cell(sx - 1, sy) });
                }
                string nearest = "null";
                try
                {
                    var n = fc?.Last_Map_IEntityAccessor_GetNearestInteractiveTargetEntity();
                    if (n != null) nearest = $"{n.GetType().FullName} ptr {n.Pointer}";
                }
                catch (Exception e) { nearest = "error " + e.GetType().Name; }
                string observer = "unavailable";
                try
                {
                    var list = fc?.interactiveTargetObserver?.InteractiveEntityList;
                    int count = list?.Count ?? -1, matches = 0;
                    if (list != null && interactive != null)
                        for (int i = 0; i < list.Count; i++)
                            if (list[i]?.Pointer == interactive.Pointer) matches++;
                    observer = $"count {count} shipMatches {matches}";
                }
                catch (Exception e) { observer = "error " + e.GetType().Name; }
                string state = $"Nikeah Blackjack owned pos {ship.Position.x:0.##},{ship.Position.y:0.##},{ship.Position.z:0.##} dir {ship.Direction} map {ship.MapId} enabled {ship.Enable}; " +
                    $"live {(live == null ? "null" : $"id {live.Id} map {live.StayMapId} enabled {live.Enable} collider {(live.ColliderObject == null ? "null" : $"active {live.ColliderObject.activeInHierarchy}/layer {live.ColliderObject.layer}")}")}; " +
                    $"object {objectState}; interactive {interactiveState}; player {playerCell}; terrain [{terrain}]; observer {observer}; nearest {nearest}";
                if (state != LastNikeahTransportation)
                {
                    LastNikeahTransportation = state;
                    Plugin.L.LogInfo("[wc transport] " + state);
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("Nikeah-transport-state", e); }
        }

        internal const int WobMap = 1, WorMap = 2, BlackjackId = 4, FalconId = 5, CollapseFlag = 41, FalconFlag = 97;

        // the party's cell on the field map (the airships' positions are cells)
        internal static UnityEngine.Vector3? PlayerCell()
        {
            var fc = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController;
            var p = fc?.player;
            if (p == null) return null;
            var c = fc.ConvertWorldPositionToCellPosition(p.transform.position);
            return new UnityEngine.Vector3(UnityEngine.Mathf.Round(c.x), UnityEngine.Mathf.Round(c.y), c.z);
        }

        internal static void MoveAirship(int from, int to, int map)
        {
            var a = Vehicle(from);
            var b = Vehicle(to);
            if (a == null || b == null) { Plugin.L.LogWarning($"[wc] airship {from} or {to} missing"); return; }
            b.MapId = map;
            b.Position = a.Position;
            b.Direction = a.Direction;
            b.SetDataStorageFlag(true);    // released = enabled (TreasureFlag4 135/136); the wheel won't reset its place
            LastMove = $"airship {to} to map {map} at {b.Position.x:0},{b.Position.y:0} (from airship {from})";
            Plugin.L.LogInfo("[wc] " + LastMove);
        }

        // which text the game holds for a script: the field's copy and the asset cache's
        internal static string ScriptProbe(string name)
        {
            var parts = new List<string>();
            try
            {
                var fc = UnityEngine.Object.FindObjectOfType<FieldMap>()?.fieldController;
                var text = fc?.GetScriptData(name);
                parts.Add($"field: {(text == null ? "none" : $"{text.Length} chars, WC {(text.Contains("wc_helm") || text.Contains("wc_start") ? "yes" : "no")}")}");
            }
            catch (Exception e) { parts.Add("field: " + e.Message); }
            try
            {
                var dic = ResourceManager.Instance?.completeAssetDic;
                if (dic != null)
                    foreach (var key in dic.Keys)
                        if (key.Contains(name))
                        {
                            var ta = dic[key]?.TryCast<UnityEngine.TextAsset>();
                            var t = ta?.text;
                            parts.Add($"cache '{key}': {(t == null ? "not text" : $"{t.Length} chars, WC {(t.Contains("wc_helm") || t.Contains("wc_start") ? "yes" : "no")}")}");
                        }
            }
            catch (Exception e) { parts.Add("cache: " + e.Message); }
            return string.Join("; ", parts);
        }

        // tests: warp to a map's point-in the way the ChangeMap instruction does
        internal static bool GotoMap(int mapId, int pointIn)
        {
            var ep = UnityEngine.Object.FindObjectOfType<MainGame>()?.eventProcedure;
            if (ep == null) return false;
            var ld = new Last.Map.LoadData();
            ld.NextMapData(mapId, pointIn, (Last.Map.ViewType)0);
            ep.EventGotoMap(ld, false, false);
            Plugin.L.LogInfo($"[wc test] warp to map {mapId} point {pointIn}");
            return true;
        }

        // world-switch test: run a wheel script on its deck with a choice answered
        internal static bool RunScript(string name, params int[] answers)
        {
            foreach (var a in answers) TestAnswers.Enqueue(a);
            Trace = 300;
            var game = UnityEngine.Object.FindObjectOfType<MainGame>();
            var ep = game?.eventProcedure;
            if (ep == null) { Plugin.L.LogWarning("[wc test] no event procedure"); return false; }
            var integ = Integrator.Instance;
            // a script started here (no callbacks) leaves Integrator.working set after it ends,
            // and Execute then refuses everything ("Interpreter is busy"): clear it when no script runs
            if (integ != null && integ.working && !integ.IsMainCoreAlive())
            {
                integ.working = false;
                Plugin.L.LogInfo("[wc test] interpreter marked idle (the last test script had ended)");
            }
            // with callbacks (a no-op set, the battle events' kind): the interpreter clears its busy
            // state when the script ends, as after a talk event, so the game's own scripts that
            // follow (an arrival script after ChangeMap, a map's exit event) aren't refused
            ICallbacks cb = null;
            try { cb = new global::Last.Battle.EventCallBacks().Cast<ICallbacks>(); } catch (Exception) { }
            bool ok = ep.EventRunScript(name, cb);
            Running = ok;
            if (!ok && integ != null)
                Plugin.L.LogInfo($"[wc test] refused: main core alive {integ.IsMainCoreAlive()}, script '{integ.scriptName}', reserved {integ.ExistsReserveScript()}; {ScriptProbe(name)}");
            Plugin.L.LogInfo($"[wc test] EventRunScript {name}: {ok}");
            return ok;
        }

        internal static int Flag(DataStorage.Category c, int index) => DataStorage.Instance?.Get(c, index) ?? -1;

        // a character joins the party the way the game's join calls do (JoinTina = JoinCharacter(1,
        // -1, 1, true, false, false)); the generic call also creates a character that never joined
        internal static bool Join(int statusId)
        {
            if (statusId < 1 || statusId > 14) { Plugin.L.LogWarning($"[wc] no character {statusId}"); return false; }
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            if (party != null)
                foreach (var c in party)
                    if (c.characterStatusId == statusId)
                    {
                        // joining twice hung the game (2026-11-05: a reward call that ran twice)
                        Plugin.L.LogInfo($"[wc] character {statusId} is already in the party");
                        return false;
                    }
            bool changeAsset = !(WcTest.Running && WcTest.NoAssetChange);   // experiment (tests)
            TraceHp(statusId, "before JoinCharacter");
            Last.Interpreter.Instructions.SystemCall.Current.JoinCharacter(statusId, -1, (Last.Defaine.User.CorpsId)1, changeAsset, false, false);
            TraceHp(statusId, "JoinCharacter");
            return true;
        }

        // a script's main core ran Exit (puppet routines end with Exit too: not counted); tests
        // wait for this after RunScript
        internal static int MainExits;
        internal static bool Running;

        public static void Exit_Postfix(Core c)
        {
            try
            {
                if (c != null && c.TryCast<MainCore>() != null) { MainExits++; Running = false; }
            }
            catch { }
        }

        internal static bool EventBusy() => Running || (Integrator.Instance?.IsMainCoreAlive() ?? false);

        // tests: a main core that stays alive with nothing to do (after a script started from a
        // test) is reset so the next script can start
        internal static void ResetMainCore()
        {
            var integ = Integrator.Instance;
            if (integ == null) return;
            Plugin.L.LogInfo($"[wc test] resetting the main core (script '{integ.scriptName}')");
            integ.ResetMainCore();
        }

        // what the New Game test checks: party (status ids), gil, the Blackjack's place
        internal static string Report()
        {
            var udm = UserDataManager.Instance();
            if (udm == null) return "no user data";
            var party = udm.GetPartyMemberData();
            var ids = new List<int>();
            if (party != null) foreach (var c in party) ids.Add(c.characterStatusId);
            string V(int id)
            {
                var t = Vehicle(id);
                return t == null ? "none" : $"map {t.MapId} at {t.Position.x:0},{t.Position.y:0} enable {t.Enable}";
            }
            return $"map {udm.CurrentMapId}; party [{string.Join(" ", ids)}]; gil {udm.OwendGil}; Blackjack {V(4)}; Falcon {V(5)}; " +
                   $"WC game {Flag(DataStorage.Category.kScenarioFlag4, 0)}; Falcon flag {Flag(DataStorage.Category.kScenarioFlag2, 97)}";
        }
    }
}

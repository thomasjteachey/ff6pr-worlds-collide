using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Il2CppSystem.Input;
using Last.Battle;
using Last.Data.User;
using Last.Management;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PrKit
{
    // Play the game headless by commands: one line in, one line back, over 127.0.0.1:47811 (the
    // client is tools/game_harness.py). Started by a test run's request (harness=1; TestRun.cs),
    // so saves are blocked and the sound is off. Generalised from BNW's GameHarness (2026-11-07).
    //
    // Built in (any PR):
    //   ping | help | state | press <key> [hold frames] | wait <frames> | skipmsg on|off | newgame
    //   | slots | load [slot] | warp <map> [point] | script <name> | battle <party> | encount <party>
    //   | log | quit
    // Keys: action cancel menu up down left right pageup pagedown switchleft switchright start select
    // shortcut. A press answers the game's own input checks (InputListener.GetKeyDown/GetKey/GetKeyUp,
    // AnyKey*) for that key: down on its first frame, held `hold` frames (3), up on the next; the
    // reply comes once it's released and 3 quiet frames passed.
    //
    // A mod adds its own:  Harness.Register("party", args => "...", "party: the party's levels");
    // a handler that returns null is called again every frame (with tick = 1, 2, ...) until it
    // answers or two minutes pass:  Harness.Register("untilbattle", (args, tick) => InBattle ? "in battle" : null, ...)
    // and adds to "state":  Harness.StateParts.Add(() => $"espers {n}");
    public static class Harness
    {
        public const int DefaultPort = 47811;
        public static bool Active { get; private set; }
        public static int Port { get; private set; } = DefaultPort;
        public static bool SkipMessages;   // a script's message box completes at once (skipmsg on)
        public static readonly List<Func<string>> StateParts = new List<Func<string>>();

        public delegate string Handler(string[] args, int tick);

        sealed class Call
        {
            public string Line;
            public string[] Args;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Reply;
            public int Tick;
            public Handler Handler;
            public float Since;
        }

        static readonly ConcurrentQueue<Call> calls = new ConcurrentQueue<Call>();
        static readonly Dictionary<string, (Handler Run, string Help)> commands =
            new Dictionary<string, (Handler, string)>(StringComparer.OrdinalIgnoreCase);
        static Call pending;
        static Thread thread;

        // the press under way: its key (by pointer), first frame, release frame, quiet-until frame
        static IntPtr pressKey = IntPtr.Zero;
        static int pressFrom, pressUntil, quietUntil, waitUntil;

        static readonly Dictionary<string, Func<Key>> Keys = new Dictionary<string, Func<Key>>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = () => Key.Action, ["cancel"] = () => Key.Cancel, ["menu"] = () => Key.Menu,
            ["up"] = () => Key.Up, ["down"] = () => Key.Down, ["left"] = () => Key.Left, ["right"] = () => Key.Right,
            ["pageup"] = () => Key.PageUp, ["pagedown"] = () => Key.PageDown, ["switchleft"] = () => Key.SwitchLeft,
            ["switchright"] = () => Key.SwitchRight, ["start"] = () => Key.Start, ["select"] = () => Key.Select,
            ["shortcut"] = () => Key.Shortcut,
        };

        public static void Register(string name, Func<string[], string> run, string help) =>
            commands[name] = ((args, tick) => tick == 0 ? run(args) ?? "" : "", help);

        public static void Register(string name, Handler run, string help) => commands[name] = (run, help);

        public static void Start(Harmony h, int port = DefaultPort)
        {
            if (Active) return;
            Active = true;
            Port = port;
            RegisterBuiltIns();
            int n = 0;
            foreach (var m in typeof(InputListener).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.ReturnType == typeof(bool)))
            {
                string name = m.Name switch
                {
                    "GetKeyDown" => nameof(KeyDown_Prefix), "GetKey" => nameof(Key_Prefix), "GetKeyUp" => nameof(KeyUp_Prefix),
                    "AnyKeyDown" => nameof(AnyDown_Prefix), "AnyKey" => nameof(Any_Prefix), "AnyKeyUp" => nameof(AnyUp_Prefix),
                    _ => null,
                };
                if (name == null) continue;
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(Harness), name)); n++; }
                catch (Exception e) { Kit.Warn($"[harness] {m.Name} not hooked: {e.Message}"); }
            }
            HookMessages(h);
            HookBattleLog(h);
            thread = new Thread(Listen) { IsBackground = true, Name = "prkit-harness" };
            thread.Start();
            Kit.Info($"[harness] listening on 127.0.0.1:{Port}; {n} input checks hooked");
        }

        // --- the network side (its own thread): a line in, the main thread's reply out ----------------

        static void Listen()
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (Exception e) { Kit.Warn($"[harness] can't listen on {Port}: {e.Message}"); return; }
            while (true)
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        var call = new Call { Line = line };
                        calls.Enqueue(call);
                        if (!call.Done.Wait(TimeSpan.FromSeconds(130))) call.Reply = "error: timed out";
                        writer.WriteLine((call.Reply ?? "").Replace("\n", " \\n "));
                    }
                }
                catch (Exception e) { Kit.Info($"[harness] connection: {e.Message}"); }
            }
        }

        // --- the main thread: one command at a time (TestRun.Update calls this) -----------------------

        public static void Update()
        {
            if (!Active) return;
            int f = Time.frameCount;
            if (pending != null)
            {
                if (pending.Handler != null)
                {
                    Step(pending, f);
                    if (pending != null) return;
                }
                else
                {
                    if (f < quietUntil || f < waitUntil) return;
                    Answer(pending, pending.Line.StartsWith("wait", StringComparison.OrdinalIgnoreCase) ? $"waited (frame {f})" : $"pressed (frame {f})");
                    pending = null;
                    pressKey = IntPtr.Zero;
                }
            }
            while (pending == null && calls.TryDequeue(out var c))
            {
                c.Args = c.Line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                c.Since = Time.realtimeSinceStartup;
                var name = c.Args[0].ToLowerInvariant();
                if (name == "press" || name == "wait")
                {
                    string err = name == "press" ? StartPress(c.Args, f) : StartWait(c.Args, f);
                    if (err != null) Answer(c, err);
                    else pending = c;
                    continue;
                }
                if (!commands.TryGetValue(name, out var cmd))
                {
                    Answer(c, $"error: unknown command '{c.Args[0]}' (help lists them)");
                    continue;
                }
                c.Handler = cmd.Run;
                pending = c;
                Step(c, f);
            }
        }

        static void Step(Call c, int frame)
        {
            string reply;
            try { reply = c.Handler(c.Args.Skip(1).ToArray(), c.Tick++); }
            catch (Exception e) { reply = $"error: {e.GetType().Name}: {e.Message}"; }
            if (reply == null && Time.realtimeSinceStartup - c.Since > 120) reply = "error: no answer in 120 s";
            if (reply == null) return;
            Answer(c, reply);
            if (pending == c) pending = null;
        }

        static void Answer(Call c, string text)
        {
            c.Reply = text;
            c.Done.Set();
        }

        static string StartPress(string[] parts, int frame)
        {
            if (parts.Length < 2 || !Keys.TryGetValue(parts[1], out var key)) return $"error: keys are {string.Join(" ", Keys.Keys)}";
            int hold = parts.Length > 2 && int.TryParse(parts[2], out var hv) ? Math.Max(1, hv) : 3;
            pressKey = key().Pointer;
            pressFrom = frame + 1;
            pressUntil = pressFrom + hold;
            quietUntil = pressUntil + 3;
            waitUntil = 0;
            return null;
        }

        static string StartWait(string[] parts, int frame)
        {
            int n = parts.Length > 1 && int.TryParse(parts[1], out var wv) ? Math.Max(1, wv) : 30;
            waitUntil = frame + n;
            quietUntil = 0;
            pressKey = IntPtr.Zero;
            return null;
        }

        // --- built-in commands (each its own method: one a game lacks a type for fails alone) ---------

        static void RegisterBuiltIns()
        {
            void Add(string name, Func<string[], string> run, string help)
            {
                if (!commands.ContainsKey(name)) Register(name, run, help);   // a mod's own wins
            }
            Add("ping", _ => "pong", "ping: pong");
            Add("help", _ => "press <key> [hold] | wait <frames> | " + string.Join(" | ", commands.Values.Select(c => c.Help)), "help: this list");
            Add("state", _ => State(), "state: scene, map, battle, script");
            Add("skipmsg", a =>
            {
                SkipMessages = a.Length > 0 && a[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                return $"messages {(SkipMessages ? "skipped" : "shown")}";
            }, "skipmsg on|off: scripts' message boxes complete at once");
            Add("newgame", _ => NewGame(), "newgame: New Game on the title screen");
            Add("slots", _ => Slots(), "slots: the save slots (read only)");
            Add("load", Load, "load [slot]: the latest save or a slot");
            Add("warp", Warp, "warp <map> [point]: to a map's entrance, as ChangeMap does");
            Add("script", RunScript, "script <name>: an event script of the current map");
            Add("battle", Battle, "battle <party>: a real battle with any monster party, as EncountBoss");
            Add("encount", Encount, "encount <party>: a random-encounter style battle");
            Add("log", _ => TakeLog(), "log: battle actions since the last log");
            Add("quit", _ => { TestRun.Quit("harness: quit"); return "quitting"; }, "quit: end the run");
        }

        static string NewGame()
        {
            var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            var set = typeof(Last.UI.KeyInput.TitleWindowController).GetMethod("SetCommand",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tw == null || set == null) return "error: no title window";
            var ps = set.GetParameters();
            set.Invoke(tw, new object[] { Last.Defaine.TitleCommandId.NewGame, Enum.ToObject(ps[1].ParameterType, 2), false, true });
            return "new game chosen";
        }

        static string Slots()
        {
            var list = SaveSlotManager.Instance()?.SaveSlotDataList;
            if (list == null) return "error: no slot list";
            var outs = new List<string>();
            foreach (var sd in list)
            {
                if (sd == null || string.IsNullOrEmpty(sd.TimeStamp)) continue;
                var who = new List<string>();
                if (sd.PartyList != null) foreach (var pc in sd.PartyList) if (pc != null) who.Add($"{pc.Name} {pc.Parameter?.BaseLevel}");
                outs.Add($"{sd.Id}: {sd.TimeStamp} {sd.CurrentArea}/{sd.CurrentLocation} [{string.Join(", ", who)}]");
            }
            return outs.Count == 0 ? "no saves" : string.Join(" | ", outs);
        }

        static string Load(string[] a)
        {
            // read only: the run's saves are blocked
            var mgr = SaveSlotManager.Instance();
            var slot = a.Length > 0 && int.TryParse(a[0], out var sid) ? mgr?.GetTargetSaveSlotData(sid) : mgr?.GetLatestSaveSlotData(false, false);
            if (slot == null) return "error: no such save";
            var tw = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
            if (tw == null)
            {
                mgr.GotoLoadSaveData(slot);   // on the field: the in-game load
                return $"loading save {slot.Id} ({slot.TimeStamp}) on the field";
            }
            // at the title, as its Load window does: the slot's data, the map load, the scene switch
            int id = slot.Id;
            Action<bool> loaded = ok =>
            {
                if (!ok) { Kit.Warn($"[harness] save {id} didn't load"); return; }
                var ld = new LoadData();
                ld.RestoreSaveData();
                MapLoadProcessor.Instance.Start(ld, true, false);
                tw.StartCoroutine(tw.SubSceneLoadingWait(false));
                Kit.Info($"[harness] save {id} read; loading its map {ld.MapId}");
            };
            tw.StartCoroutine(mgr.LoadAsync(id, loaded, false));
            return $"loading save {id} ({slot.TimeStamp})";
        }

        static string Warp(string[] a)
        {
            int map = a.Length > 0 && int.TryParse(a[0], out var mv) ? mv : -1;
            int point = a.Length > 1 && int.TryParse(a[1], out var pv) ? pv : 1;
            var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
            if (ep == null || map < 0) return "error: no event procedure or map";
            var ld = new LoadData();
            ld.NextMapData(map, point, (ViewType)0);
            ep.EventGotoMap(ld, false, false);
            return $"warping to map {map} point {point}";
        }

        static string RunScript(string[] a)
        {
            if (a.Length < 1) return "error: script <name>";
            var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
            if (ep == null) return "error: no event procedure";
            var integ = Last.Interpreter.Integrator.Instance;
            if (integ != null && integ.ExistsReserveScript()) integ.CancelReservedScript();
            Last.Interpreter.ICallbacks cb = null;
            try { cb = new EventCallBacks().Cast<Last.Interpreter.ICallbacks>(); }
            catch (Exception) { }
            return ep.EventRunScript(a[0], cb) ? $"script {a[0]} started" : $"error: script {a[0]} didn't start (not in this map's list?)";
        }

        static string Battle(string[] a)
        {
            int pid = a.Length > 0 && int.TryParse(a[0], out var bv) ? bv : -1;
            if (Master.Get<Last.Data.Master.MonsterParty>(pid) == null) return $"error: no party {pid}";
            if (Object.FindObjectOfType<FieldMap>()?.fieldController == null) return "error: not on the field";
            var integ = Last.Interpreter.Integrator.Instance;
            if (integ != null && integ.IsMainCoreAlive()) return $"error: a script runs ({integ.scriptName})";
            var mc = new Last.Interpreter.MainCore();
            var op = new Last.Interpreter.Instructions.Format.Operands();
            op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
            op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
            op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
            for (int k = 0; k < 8; k++) op.sValues[k] = "";
            op.iValues[0] = pid;
            var mn = new Last.Interpreter.Instructions.Format.Mnemonic { mnemonic = "EncountBoss", operands = op };
            mc.currentInstruction = mn;
            Last.Interpreter.Instructions.External.Misc.EncountBoss(mc);
            return $"battle {pid} started";
        }

        static string Encount(string[] a)
        {
            int pid = a.Length > 0 && int.TryParse(a[0], out var pv) ? pv : -1;
            var fc = Object.FindObjectOfType<FieldMap>()?.fieldController;
            var party = Master.Get<Last.Data.Master.MonsterParty>(pid);
            if (fc == null) return "error: not on the field";
            if (party == null) return $"error: no party {pid}";
            fc.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
            return $"encounter {pid} started";
        }

        // --- what the game shows ---------------------------------------------------------------------

        public static bool InBattle
        {
            get
            {
                var b = BattlePlugManager.Instance();
                return b != null && b.IsBattle();
            }
        }

        public static string Scene()
        {
            if (InBattle) return "battle";
            if (Object.FindObjectOfType<FieldMap>()?.fieldController != null) return "field";
            if (Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>() != null) return "title";
            return "other";
        }

        static string State()
        {
            var sb = new StringBuilder();
            sb.Append($"frame {Time.frameCount}; scene {Scene()}; map {UserDataManager.Instance()?.CurrentMapId}");
            var scenes = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                scenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);
            sb.Append($"; unity scenes [{string.Join(" ", scenes)}]");
            var manager = BattlePlugManager.Instance();
            if (manager != null && !InBattle)
                sb.Append($"; battle manager up, not in battle ({manager.GetPlayerUnits()?.Length ?? 0} players, {manager.GetEnemyUnits()?.Length ?? 0} enemies)");
            try
            {
                var bc = Object.FindObjectOfType<BattleController>();
                if (bc != null) sb.Append($"; battle phase {bc.stateMachine?.Current}");
            }
            catch (Exception e) { sb.Append($"; battle phase ? ({e.GetType().Name})"); }
            if (InBattle)
            {
                var battle = BattlePlugManager.Instance();
                sb.Append("; players [" + string.Join(", ", (battle.GetPlayerUnits() ?? new BattlePlayerData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
                sb.Append("; enemies [" + string.Join(", ", (battle.GetEnemyUnits() ?? new BattleEnemyData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
                try { sb.Append($"; end judgment {battle.BattleEndJugment?.GetEndJugment()}"); }
                catch (Exception) { }
            }
            var script = Last.Interpreter.Integrator.Instance;
            if (script != null && script.IsMainCoreAlive()) sb.Append($"; script {script.scriptName}");
            sb.Append($"; messages {(SkipMessages ? "skipped" : "shown")}");
            foreach (var part in StateParts)
            {
                try { sb.Append("; ").Append(part()); }
                catch (Exception e) { sb.Append($"; ? ({e.GetType().Name})"); }
            }
            return sb.ToString();
        }

        public static string Who(BattleUnitData u)
        {
            if (u == null) return "-";
            try
            {
                var e = u.TryCast<BattleEnemyData>();
                if (e != null) return $"monster {e.MonsterMaster?.Id}";
                return u.GetOwnedCharacterData()?.Name ?? "?";
            }
            catch (Exception) { return "?"; }
        }

        public static string Hp(BattleUnitData u)
        {
            var p = u?.BattleUnitDataInfo?.Parameter;
            if (p == null) return "?";
            var text = $"{p.CurrentHP}/{p.ConfirmedMaxHp()}";
            try
            {
                var list = u.BattleUnitDataInfo.GetConfirmedConditionType();
                if (list != null && list.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var c in list) names.Add(c.ToString());
                    text += " {" + string.Join(" ", names) + "}";
                }
            }
            catch (Exception) { }
            return text;
        }

        // --- skipmsg: the script message instruction completes at once ------------------------------

        static void HookMessages(Harmony h)
        {
            var msg = AccessTools.TypeByName("Last.Interpreter.Instructions.Message");
            var m = msg == null ? null : AccessTools.Method(msg, "Msg");
            if (m == null) { Kit.Info("[harness] no Message.Msg: skipmsg does nothing"); return; }
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(Harness), nameof(Msg_Prefix))); }
            catch (Exception e) { Kit.Warn($"[harness] Message.Msg not hooked: {e.Message}"); }
        }

        public static bool Msg_Prefix(ref int __result)
        {
            if (!SkipMessages) return true;
            __result = 0;
            return false;
        }

        // --- battle log: every action as it's built, HP after each ------------------------------------
        // Actions are built by the game's own Serial.FF<n>.Calc.FunctionProvider (looked up by name).

        static readonly List<string> battleLog = new List<string>();

        static void HookBattleLog(Harmony h)
        {
            var provider = AccessTools.TypeByName($"Serial.FF{(int)Game.Current}.Calc.FunctionProvider");
            var act = provider == null ? null : AccessTools.Method(provider, "CreateActFunction");
            if (act != null) Hooks.Patch(h, act, typeof(Harness), prefix: nameof(Act_Prefix));
            else Kit.Info("[harness] no FunctionProvider.CreateActFunction in this game: no action log");
            var atb = AccessTools.TypeByName("Last.Battle.BattleProgressATB");
            var end = atb == null ? null : AccessTools.Method(atb, "ActExectionEndDelegate");
            if (end != null) Hooks.Patch(h, end, typeof(Harness), postfix: nameof(ActEnd_Postfix));
        }

        public static void Note(string line)
        {
            line = $"[{Time.frameCount}] {line}";
            lock (battleLog)
            {
                battleLog.Add(line);
                if (battleLog.Count > 400) battleLog.RemoveAt(0);
            }
            Kit.Info($"[harness] {line}");
        }

        static string TakeLog()
        {
            lock (battleLog)
            {
                var text = battleLog.Count == 0 ? "(no actions)" : string.Join(" \\n ", battleLog);
                battleLog.Clear();
                return text;
            }
        }

        public static void Act_Prefix(BattleActData battleActData)
        {
            if (!Active || battleActData == null) return;
            try
            {
                var ids = new List<string>();
                if (battleActData.abilityList != null)
                    foreach (var a in battleActData.abilityList) if (a != null) ids.Add(a.Id.ToString());
                var targets = new List<string>();
                if (battleActData.TargetUnitDatas != null)
                    foreach (var t in battleActData.TargetUnitDatas) if (t != null) targets.Add($"{Who(t)} {Hp(t)}");
                Note($"{Who(battleActData.AttackUnitData)} {(battleActData.isCounter ? "counters with" : "uses")} ability [{string.Join(" ", ids)}] " +
                     $"(command {battleActData.Command?.Id}) on [{string.Join(", ", targets)}]");
            }
            catch (Exception e) { Note($"act: {e.GetType().Name}"); }
        }

        public static void ActEnd_Postfix(BattleUnitData battleUnitData, bool isCounter)
        {
            if (!Active) return;
            try
            {
                var battle = BattlePlugManager.Instance();
                Note($"  after {Who(battleUnitData)}'s {(isCounter ? "counter" : "act")}: players [" +
                     string.Join(", ", (battle?.GetPlayerUnits() ?? new BattlePlayerData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]; enemies [" +
                     string.Join(", ", (battle?.GetEnemyUnits() ?? new BattleEnemyData[0]).Select(u => $"{Who(u)} {Hp(u)}")) + "]");
            }
            catch (Exception e) { Note($"act end: {e.GetType().Name}"); }
        }

        // --- the injected presses ---------------------------------------------------------------------

        static bool Is(Key key) => pressKey != IntPtr.Zero && key != null && key.Pointer == pressKey;

        public static bool KeyDown_Prefix(Key key, ref bool __result)
        {
            if (!Is(key) || Time.frameCount != pressFrom) return true;
            __result = true;
            return false;
        }

        public static bool Key_Prefix(Key key, ref bool __result)
        {
            int f = Time.frameCount;
            if (!Is(key) || f < pressFrom || f >= pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool KeyUp_Prefix(Key key, ref bool __result)
        {
            if (!Is(key) || Time.frameCount != pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool AnyDown_Prefix(ref bool __result)
        {
            if (pressKey == IntPtr.Zero || Time.frameCount != pressFrom) return true;
            __result = true;
            return false;
        }

        public static bool Any_Prefix(ref bool __result)
        {
            int f = Time.frameCount;
            if (pressKey == IntPtr.Zero || f < pressFrom || f >= pressUntil) return true;
            __result = true;
            return false;
        }

        public static bool AnyUp_Prefix(ref bool __result)
        {
            if (pressKey == IntPtr.Zero || Time.frameCount != pressUntil) return true;
            __result = true;
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using WorldsCollide.PR;

// A battle for the interpreter to look at: 3 characters, monsters in slots, statuses, HP, variables,
// what hit whom. Deterministic random numbers.
sealed class FakeBattle : ISnesAiBattle
{
    public Random Rng = new Random(1);
    public int[] Actors = { 2, 4, 11, -1 };
    public bool[] Monsters = { true, false, false, false, false, false };
    public bool[] DeadUnits = new bool[10];
    public int[] Hps = Enumerable.Repeat(100, 10).ToArray();
    public int[] Mps = new int[10];
    public int[] Levels = Enumerable.Repeat(10, 10).ToArray();
    public int[,] Statuses = new int[10, 2];
    public byte[] Vars = new byte[0x24];
    public int Retal = -1, RetalCmdUnit = -1, RetalCmd, RetalAtk, RetalItm, RetalElemUnit = -1, RetalElem;
    public int Rand(int n) => Rng.Next(n);
    public int CharActor(int slot) => Actors[slot];
    public bool MonsterPresent(int slot) => Monsters[slot] && !DeadUnits[4 + slot];
    public int TargetableMask()
    {
        int m = 0;
        for (int u = 0; u < 10; u++)
            if (!DeadUnits[u] && (u < 4 ? Actors[u] >= 0 : Monsters[u - 4])) m |= SnesAi.MaskOf(u);
        return m;
    }
    public bool Dead(int unit) => DeadUnits[unit];
    public int Status(int unit, int word) => Statuses[unit, word];
    public int Hp(int unit) => Hps[unit];
    public int Mp(int unit) => Mps[unit];
    public int Level(int unit) => Levels[unit];
    public int WeakElements(int unit) => 0;
    public bool CharPresentFlag(int slot) => Actors[slot] >= 0;
    public int BattleTimer() => 0;
    public int MonsterTimer(int unit) => 0;
    public int Formation() => 482;
    public int CharsAlive() => Enumerable.Range(0, 4).Count(s => Actors[s] >= 0 && !DeadUnits[s]);
    public int MonstersAlive() => Enumerable.Range(0, 6).Count(s => Monsters[s] && !DeadUnits[4 + s]);
    public int MonsterTypesAlive() => MonstersAlive() > 0 ? 1 : 0;
    public int MonstersInBattleMask() { int m = 0; for (int s = 0; s < 6; s++) if (Monsters[s]) m |= 1 << s; return m; }
    public int MonstersAliveMask() { int m = 0; for (int s = 0; s < 6; s++) if (Monsters[s] && !DeadUnits[4 + s]) m |= 1 << s; return m; }
    public bool GauRecruited() => true;
    public byte BattleVar(int i) => Vars[i];
    public int RetalTarget(int unit) => Retal;
    public int RetalCmdTarget(int unit) => RetalCmdUnit;
    public int RetalCommand(int unit) => RetalCmd;
    public int RetalAttack(int unit) => RetalAtk;
    public int RetalItem(int unit) => RetalItm;
    public int RetalElemTarget(int unit) => RetalElemUnit;
    public int RetalElements(int unit) => RetalElem;
    public int PrevAttacker(int unit) => -1;
    public int TargettingTarget(int unit) => -1;
    public void ClearTargettingTarget(int unit) { }

    // what the battle does with the queued variable/switch changes (in order, as FF6 runs them)
    public void Apply(SnesAiMonster m, List<SnesAiAction> acts)
    {
        foreach (var a in acts)
        {
            if (a.Op == SnesAiOp.ChangeVar)
            {
                byte v = SnesAi.IsBattleVar(a.B1) ? Vars[a.B1] : m.Var;
                v = SnesAi.ChangeVar(v, a.B2);
                if (SnesAi.IsBattleVar(a.B1)) Vars[a.B1] = v; else m.Var = v;
            }
            else if (a.Op == SnesAiOp.ChangeSwitch)
            {
                byte v = SnesAi.IsBattleVar(a.B2) ? Vars[a.B2] : m.Var;
                v = SnesAi.ChangeSwitch(v, a.B1, a.B3);
                if (SnesAi.IsBattleVar(a.B2)) Vars[a.B2] = v; else m.Var = v;
            }
        }
    }
}

static class Program
{
    static int failures, checks;

    static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + what);
    }

    static byte[] Hex(string h) => Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(2 * i, 2), 16)).ToArray();

    static string Show(List<SnesAiAction> acts) => acts.Count == 0 ? "(nothing)" : string.Join("; ", acts);

    static int[] Attacks(List<SnesAiAction> acts) => acts.Where(a => a.Op == SnesAiOp.Attack).Select(a => a.Attack).ToArray();

    // WC's Atma (SNES 381) in the user's seed gaadl68ix7j4: scaled-spell placeholders, the charge after
    // 12 hits ("Unknown light surrounded Atma!", two idle turns, the big spell), the low-HP rotation
    const string Atma = "fc142400f38300fa0c0100f0fefefefdfa0c0100f0fefefefdfa0b0100f14750f9022400fefc06363cf147f0374641fdf147f0374f37fdf147f04f4146fdf147f0373737fefc152403fa0b0100f9012403fef0373a38fdf03d3a37fdf03d3d38fdf037c5c5fffc120000f50c01fffefc050000fc152400f0eefefef80381fc0d030cf9012400f80300ff";

    static void AtmaTest()
    {
        var b = new FakeBattle();
        b.Hps[4] = 9840;
        var m = SnesAiMonster.FromScript(Hex(Atma));
        Check(m.CounterStart == 0x66, $"Atma: counter section at 0x66 ({m.CounterStart:x})");
        List<SnesAiAction> Turn()
        {
            var a = SnesAi.RunMain(m, 4, b);
            b.Apply(m, a);
            return a;
        }
        // turn 1: the flash (switch 3 of its own byte), no attack
        var t1 = Turn();
        Check(Attacks(t1).Length == 0 && t1.Any(a => a.Op == SnesAiOp.Animation) && (m.Var & 8) != 0, "Atma turn 1: only the flash: " + Show(t1));
        int[][] rotation = { new[] { 0x37, 0x3A, 0x38 }, new[] { 0x3D, 0x3A, 0x37 }, new[] { 0x3D, 0x3D, 0x38 }, new[] { 0x37, 0xC5, 0xC5 } };
        for (int cycle = 0; cycle < 3; cycle++)
            for (int k = 0; k < 4; k++)
            {
                var t = Turn();
                var at = Attacks(t);
                Check(at.Length == 1 && rotation[k].Contains(at[0]), $"Atma rotation cycle {cycle} turn {k}: {Show(t)}");
            }
        // two turns into the rotation, then 13 hits by character slot 0: the counter sets the charge bit
        Turn(); Turn();
        int battles = 0;
        for (int hit = 1; hit <= 13; hit++)
        {
            b.Retal = 0;
            var c = SnesAi.RunCounter(m, 4, b, false);
            battles += Attacks(c).Count(x => x == 0xEE);
            b.Apply(m, c);
            Check((m.Var & 1) == (hit == 13 ? 1 : 0), $"Atma counter: hit {hit} charge bit {(m.Var & 1)} var3 {b.Vars[3]}: {Show(c)}");
        }
        Check(battles > 0 && battles < 13, $"Atma counters with Battle about 1 in 3 ({battles} of 13)");
        b.Retal = -1;
        var c1 = Turn();
        Check(c1.Any(a => a.Op == SnesAiOp.Message && a.B1 == 0x83) && Attacks(c1).Length == 0, "Atma charge 1: message 0x83, no attack: " + Show(c1));
        var c2 = Turn();
        Check(Attacks(c2).Length == 0 && c2.Any(a => a.Op == SnesAiOp.Animation), "Atma charge 2: idle: " + Show(c2));
        var c3 = Turn();
        Check(Attacks(c3).SequenceEqual(new[] { 0x50 }) && (m.Var & 1) == 0, "Atma charge 3: the big spell (0x50), bit cleared: " + Show(c3));
        var r1 = Turn();
        Check(Attacks(r1).Length == 1 && rotation[0].Contains(Attacks(r1)[0]), "Atma after the charge: the rotation from its start: " + Show(r1));
        // low HP: the 4-turn rotation under 7680, cycling
        b.Hps[4] = 5000;
        int[][] low = { new[] { 0x37, 0x46, 0x41 }, new[] { 0x37, 0x4F, 0x37 }, new[] { 0x4F, 0x41, 0x46 }, new[] { 0x37 } };
        for (int cycle = 0; cycle < 2; cycle++)
            for (int k = 0; k < 4; k++)
            {
                var t = Turn();
                var at = Attacks(t);
                Check(at.Length == 1 && low[k].Contains(at[0]), $"Atma low HP cycle {cycle} turn {k}: {Show(t)}");
            }
        // dying: the counter section's death block (unresponsive): its exit animation
        b.DeadUnits[4] = true;
        var d = SnesAi.RunCounter(m, 4, b, true);
        Check(d.Count == 1 && d[0].Op == SnesAiOp.EntryExit && d[0].B1 == 0x0C && d[0].B2 == 0x01 && d[0].B3 == 0xFF, "Atma's death: everyone leaves (F5 0C 01 FF): " + Show(d));
    }

    // every monster of a seed (wc_ai_bytes.json: SNES id -> hex of its main + counter sections) under
    // random battle states: no exception, every turn ends, queued actions stay sane
    static void FuzzAll(string path)
    {
        var text = System.IO.File.ReadAllText(path);
        var doc = System.Text.Json.JsonDocument.Parse(text);
        int monsters = 0, turns = 0, acts = 0;
        foreach (var kv in doc.RootElement.EnumerateObject())
        {
            var m = SnesAiMonster.FromScript(Hex(kv.Value.GetString()));
            var b = new FakeBattle { Rng = new Random(int.Parse(kv.Name)) };
            monsters++;
            for (int t = 0; t < 200; t++)
            {
                // a changing battle: HP, deaths, statuses, hits
                b.Hps[4] = b.Rng.Next(1, 40000);
                b.Statuses[b.Rng.Next(10), b.Rng.Next(2)] = b.Rng.Next(65536);
                b.Monsters[b.Rng.Next(1, 6)] = b.Rng.Next(2) == 0;
                b.DeadUnits[b.Rng.Next(0, 4)] = b.Rng.Next(8) == 0;
                List<SnesAiAction> a;
                try
                {
                    a = SnesAi.RunMain(m, 4, b);
                    b.Apply(m, a);
                    b.Retal = b.Rng.Next(-1, 4);
                    b.RetalCmdUnit = b.Retal;
                    var c = SnesAi.RunCounter(m, 4, b, b.Rng.Next(10) == 0);
                    b.Apply(m, c);
                    a.AddRange(c);
                }
                catch (Exception e)
                {
                    Check(false, $"monster {kv.Name} turn {t}: {e.GetType().Name} {e.Message}");
                    break;
                }
                turns++;
                acts += a.Count;
                Check(a.Count < 64, $"monster {kv.Name} turn {t}: {a.Count} actions");
                foreach (var x in a)
                    Check(x.Op != SnesAiOp.Attack || x.Attack < 0xFE, $"monster {kv.Name}: attack {x.Attack:x2}");
            }
        }
        Console.WriteLine($"fuzz: {monsters} monsters, {turns} turns, {acts} actions");
    }

    static int Main(string[] args)
    {
        AtmaTest();
        if (args.Length > 0) FuzzAll(args[0]);
        Console.WriteLine($"{checks - failures} of {checks} checks passed");
        return failures == 0 ? 0 : 1;
    }
}

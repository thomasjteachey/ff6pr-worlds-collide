using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorldsCollide.PR;

static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL " + what);
    }

    static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : Path.Combine("..", "WcPlugin", "wc_flags.json");
        var outPath = args.Length > 1 ? args[1] : "wc_flags_cases.tsv";
        var set = WcFlagSet.Load(File.ReadAllText(path));
        Console.WriteLine($"WC {set.WcVersion}: {set.Flags.Count} flags, {set.Sections.Count} sections, {set.Presets.Count} presets");
        var cases = new List<(string Name, string Flags)>();

        // presets: parse -> serialize is stable and loses nothing
        foreach (var p in set.Presets)
        {
            var v = new FlagValues(set);
            v.Parse(p.Value);
            Check(v.Unknown.Count == 0, $"preset {p.Key}: unknown tokens {string.Join(" ", v.Unknown)}");
            var s1 = v.Serialize();
            var v2 = new FlagValues(set);
            v2.Parse(s1);
            Check(v2.Serialize() == s1, $"preset {p.Key}: serialize not stable");
            cases.Add(("preset " + p.Key, s1));
            cases.Add(("preset-original " + p.Key, p.Value));
        }

        // every non-text flag turned on by the screen (right from Off) must be valid for WC
        foreach (var d in set.Flags.Values)
        {
            if (d.Text) continue;
            var v = new FlagValues(set);
            v.Step(d, 0, +1);
            Check(v.IsOn(d.Flag), $"{d.Flag}: right from Off doesn't turn it on");
            cases.Add(("on " + d.Flag, v.Serialize()));
            // step through: right a few times, then left until off; never throws, ends off
            for (int i = 0; i < 3; i++) v.Step(d, 0, +1);
            cases.Add(("stepped " + d.Flag, v.Serialize()));
            for (int i = 0; i < 200000 && v.IsOn(d.Flag); i++) v.Step(d, 0, -1, 1000);
            Check(!v.IsOn(d.Flag), $"{d.Flag}: left doesn't turn it off");
        }

        // dropdowns keep one option
        foreach (var r in set.Sections.SelectMany(s => s.Rows).OfType<ChoiceRow>())
        {
            var v = new FlagValues(set);
            for (int i = 0; i < r.Options.Count; i++)
            {
                v.Select(r, i);
                Check(v.Selected(r) == i, $"{r.Label}: select {i}");
                Check(r.Options.Count(o => v.IsOn(o.Flag)) == 1, $"{r.Label}: more than one option on");
            }
            v.Select(r, -1);
            Check(v.Selected(r) == -1, $"{r.Label}: select none");
        }

        // quoting
        var q = new FlagValues(set);
        q.Parse("-nm1 \"\" -sc1 terra");
        Check(q.IsOn("-nm1") && q.Values("-nm1").Count == 1 && q.Values("-nm1")[0] == "", "empty value parse");
        Check(q.Serialize().Contains("-nm1 \"\""), "empty value serialize: " + q.Serialize());

        StatusClockChecks();

        File.WriteAllLines(outPath, cases.Select(c => c.Name + "\t" + c.Flags));
        Console.WriteLine($"{cases.Count} flag strings -> {outPath}; {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    // WcStatusClock.cs: the SNES's status clock (C2/111B, C2/5A83, C2/09D2, C2/5AEA, C2/5AA3, C2/5B06)
    // for one fighter in slot 0: `at` gives its statuses at a time (minus those the clock's timers took
    // off, as the game does), ticks land at once unless `held` says the game's tick can't run then;
    // the events: ticks, "<Status>-off" as a timer runs out, "Doom-0" as Condemned's count ends.
    static List<(double T, string Kind)> RunClock(double seconds, int step, Func<double, SnesStatus> at,
                                                  Func<double, bool> held = null, int doomLevel = -1, int seed = 1)
    {
        var c = new SnesStatusClock(step);
        var round = new SnesStatusRound();
        var rng = new Random(seed);
        var ev = new List<(double, string)>();
        var off = SnesStatus.None;
        double t = 0, frame = 1 / SnesStatusClock.FramesPerSecond;
        bool doomWas = false;
        while (t < seconds)
        {
            t += frame;
            var s = at(t) & ~off;
            if ((s & SnesStatus.Doom) != 0 && !doomWas && doomLevel >= 0) c.DoomLevel = doomLevel;
            doomWas = (s & SnesStatus.Doom) != 0;
            round.Advance(frame, slot =>
            {
                if (slot != 0) return;
                int doom = c.DoomLeft;
                c.Visit(s, rng);
                if (doom > 0 && c.DoomLeft == 0) ev.Add((t, "Doom-0"));
                if (doom < 0 && c.DoomLeft >= 0) ev.Add((t, "Doom-set " + c.DoomLeft));
                foreach (var f in new[] { SnesStatus.Stop, SnesStatus.Sleep, SnesStatus.Reflect, SnesStatus.Freeze })
                    if ((c.Expired & f) != 0) { off |= f; ev.Add((t, f + "-off")); }
                c.Expired = SnesStatus.None;
            });
            if (held != null && held(t)) continue;
            if (c.DueRegen) { c.DueRegen = false; ev.Add((t, "Regen")); }
            if (c.DueSap) { c.DueSap = false; ev.Add((t, "Sap")); }
            if (c.DuePoison) { c.DuePoison = false; ev.Add((t, "Poison")); }
        }
        return ev;
    }

    static List<double> Gaps(List<(double T, string Kind)> ticks, string kind)
    {
        var ts = ticks.Where(x => x.Kind == kind).Select(x => x.T).ToList();
        return ts.Zip(ts.Skip(1), (a, b) => b - a).ToList();
    }

    static bool All(List<double> gaps, double want, double tol = 0.02) =>
        gaps.Count > 0 && gaps.All(g => Math.Abs(g - want) <= tol);

    const SnesStatus N = SnesStatus.None, Ha = SnesStatus.Haste, Sl = SnesStatus.Slow, St = SnesStatus.Stop,
                     Po = SnesStatus.Poison, Re = SnesStatus.Regen, Sa = SnesStatus.Sap;

    static void StatusClockChecks()
    {
        double fps = SnesStatusClock.FramesPerSecond;
        double regenS = 512 / fps, poisonS = 1024 / fps, stepS = 128 / fps;     // 8.519 s, 17.039 s, 2.130 s
        Check(SnesStatusClock.StepTable == "R-P-R---", "C2/5AEA: Regen/Sap at steps 0 and 4, Poison at 2");
        Check(Math.Abs(SnesStatusClock.TickSeconds * 16 * 4 * 4 * fps / 2 - 256) < 1e-6, "a Regen tick: 4 steps of 4 visits of 16 two-frame ticks");

        var normal = RunClock(200, 0, t => Po | Re);
        Check(All(Gaps(normal, "Regen"), regenS), $"status clock: Regen every {regenS:0.000} s ({string.Join(" ", Gaps(normal, "Regen").Select(g => g.ToString("0.000")))})");
        Check(All(Gaps(normal, "Poison"), poisonS), $"status clock: Poison every {poisonS:0.000} s ({string.Join(" ", Gaps(normal, "Poison").Select(g => g.ToString("0.000")))})");

        var sap = RunClock(100, 0, t => Re | Sa);
        Check(Gaps(sap, "Sap").Count > 5 && All(Gaps(sap, "Sap"), regenS) && !sap.Any(x => x.Kind == "Regen"),
              "status clock: Regen's flag ticks Sap when the fighter has both (C2/5B74), never Regen");

        var haste = Gaps(RunClock(400, 0, t => Ha | Po | Re), "Regen");
        double hasteMean = 4 * 256.0 / 84 * 32 / fps;          // 6.49 s
        Check(haste.Count > 20 && Math.Abs(haste.Average() - hasteMean) < 0.05 && haste.All(g => g > 12 * 32 / fps - 0.02 && g < 13 * 32 / fps + 0.02),
              $"status clock: Haste's Regen every {haste.Average():0.000} s (SNES {hasteMean:0.000}: 12-13 visits)");
        var slow = RunClock(300, 0, t => Sl | Po | Re);
        Check(All(Gaps(slow, "Regen"), 2 * regenS) && All(Gaps(slow, "Poison"), 2 * poisonS), "status clock: Slow halves every tick");
        var both = RunClock(200, 0, t => Ha | Sl | Re);
        Check(All(Gaps(both, "Regen"), 2 * regenS), "status clock: Slow wins over Haste (C2/09D2)");

        // Stop cured after 20 s: nothing while stopped; the step index waits, so later ticks come whole steps late
        var plain = RunClock(120, 3, t => Re);
        var stopped = RunClock(120, 3, t => Re | (t >= 20 && t < 40 ? St : N));
        Check(!stopped.Any(x => x.Kind == "Regen" && x.T >= 20 && x.T < 40), "status clock: no tick while stopped");
        var after = stopped.Where(x => x.Kind == "Regen" && x.T >= 40).Select(x => x.T).ToList();
        var shift = after.Select(a => a - plain.Where(p => p.T >= 20).Select(p => p.T).OrderBy(p => Math.Abs(p - (a - 20))).First()).ToList();
        Check(after.Count > 3 && Gaps(stopped.Where(x => x.T >= 40).ToList(), "Regen").All(g => Math.Abs(g - regenS) < 0.02) &&
              shift.All(d => Math.Abs(d - Math.Round(d / stepS) * stepS) < 0.02),
              $"status clock: after Stop, Regen goes on every {regenS:0.000} s, whole steps later ({string.Join(" ", shift.Select(d => d.ToString("0.00")))})");

        // a tick the game can't land yet (held) waits, and holds the next ones: one at a time
        var heldTicks = RunClock(60, 0, t => Re, t => t < 30);
        Check(heldTicks.Count(x => x.T < 30.05) == 1 && heldTicks.First().T >= 30, $"status clock: held ticks don't pile up ({heldTicks.Count(x => x.T < 30.05)} at the release)");

        // the battle's start: a random step index puts the first tick anywhere in one interval
        double firstMax = 0, firstPoisonMax = 0;
        for (int step = 0; step < 8; step++)
        {
            var r = RunClock(40, step, t => Po | Re);
            firstMax = Math.Max(firstMax, r.First(x => x.Kind == "Regen").T);
            firstPoisonMax = Math.Max(firstPoisonMax, r.First(x => x.Kind == "Poison").T);
        }
        // (a flag set at a step is sent at the next one: up to an interval and a step)
        Check(firstMax <= regenS + stepS + 32 / fps && firstPoisonMax <= poisonS + stepS + 32 / fps,
              $"status clock: the first tick within an interval and a step (Regen by {firstMax:0.00} s, Poison by {firstPoisonMax:0.00} s)");

        // the timers (C2/46B0's on-set handlers): Sleep and Stop 18 steps, Reflect 26, Freeze 34, from
        // the visit that finds the status; Haste/Slow scale the steps; Stop holds the others
        foreach (var (f, steps) in new[] { (SnesStatus.Sleep, 18), (SnesStatus.Stop, 18), (SnesStatus.Reflect, 26), (SnesStatus.Freeze, 34) })
            foreach (var (speed, per) in new[] { (N, 4.0), (Ha, 256.0 / 84), (Sl, 8.0) })
            {
                var ev = RunClock(300, 5, t => speed | (t >= 3 ? f : N));
                var o = ev.FirstOrDefault(x => x.Kind == f + "-off");
                double want = steps * per * 32 / fps;
                double got = o.Kind == null ? -1 : o.T - 3;
                // the first step comes a visit to a step after the find (the counter's phase; Haste: 3-4 visits a step)
                Check(o.Kind != null && got >= want - (per + 1) * 32 / fps - 0.05 && got <= want + 32 / fps + 0.05,
                      $"status clock: {f} {(speed == N ? "" : speed + " ")}lasts {steps} steps ({got:0.00} s, SNES {want:0.00})");
            }
        var heldTimers = RunClock(200, 5, t => (t >= 3 ? SnesStatus.Sleep | SnesStatus.Reflect : N) | (t >= 10 && t < 50 ? St : N));
        double sleepOff = heldTimers.First(x => x.Kind == "Sleep-off").T, reflectOff = heldTimers.First(x => x.Kind == "Reflect-off").T;
        Check(sleepOff > 50 + 10 * stepS && reflectOff > 50 + 18 * stepS, $"status clock: a Stop holds Sleep and Reflect (off at {sleepOff:0.0} s and {reflectOff:0.0} s, Stop 10-50 s)");
        var start = RunClock(120, 5, t => SnesStatus.Reflect | SnesStatus.Sleep);
        Check(!start.Any(x => x.Kind.EndsWith("-off")), "status clock: statuses there from the start have no timer (equipment, innate)");
        var stopOff = RunClock(100, 5, t => t >= 3 ? St : N);
        Check(stopOff.Any(x => x.Kind == "Stop-off"), "status clock: Stop's own timer runs while stopped");

        // Condemned: 81 - (L + random 0..L-1), 20 past 60 (C2/09B4); its end N - 1 steps on
        var rng = new Random(7);
        var counts = new Dictionary<int, (int lo, int hi)>();
        foreach (int lv in new[] { 1, 10, 30, 31, 60, 61, 99 })
        {
            int lo = 999, hi = -1;
            for (int i = 0; i < 4000; i++) { int n = SnesStatusClock.CondemnedCount(lv, rng); lo = Math.Min(lo, n); hi = Math.Max(hi, n); }
            counts[lv] = (lo, hi);
        }
        Check(counts[1] == (80, 80) && counts[10] == (62, 71) && counts[30] == (22, 51) && counts[31] == (20, 50) &&
              counts[61] == (20, 20) && counts[99] == (20, 20),
              "Condemned counts: " + string.Join(", ", counts.Select(kv => $"Lv {kv.Key}: {kv.Value.lo}-{kv.Value.hi}")));
        var doom = RunClock(250, 5, t => t >= 3 ? SnesStatus.Doom : N, doomLevel: 30, seed: 3);
        var set = doom.FirstOrDefault(x => x.Kind.StartsWith("Doom-set"));
        var zero = doom.FirstOrDefault(x => x.Kind == "Doom-0");
        int left = set.Kind == null ? -1 : int.Parse(set.Kind.Split(' ')[1]);
        Check(left >= 21 && left <= 50 && zero.Kind != null && Math.Abs(zero.T - set.T - left * stepS) < stepS + 0.05,
              $"status clock: Condemned (Lv 30) counts {left} steps to its end ({(zero.Kind == null ? -1 : zero.T - set.T):0.0} s, {left * stepS:0.0} by the steps)");
        var doomStop = RunClock(300, 5, t => (t >= 3 ? SnesStatus.Doom : N) | (t >= 10 && t < 40 ? St : N), doomLevel: 30, seed: 3);
        var zeroStop = doomStop.FirstOrDefault(x => x.Kind == "Doom-0");
        Check(zeroStop.Kind != null && zeroStop.T > zero.T + 25, $"status clock: a Stop holds Condemned's count (end at {zeroStop.T:0.0} s, {zero.T:0.0} s without)");
        var noCaster = RunClock(100, 5, t => t >= 3 ? SnesStatus.Doom : N);
        Check(!noCaster.Any(x => x.Kind.StartsWith("Doom")), "status clock: Condemned with no caught attacker stays the game's");
        Console.WriteLine($"status clock: Regen/Sap {regenS:0.000} s, Poison {poisonS:0.000} s, Haste Regen {haste.Average():0.000} s, a step {stepS:0.000} s");
    }
}

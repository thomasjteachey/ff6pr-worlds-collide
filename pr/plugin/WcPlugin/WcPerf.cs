using System;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace WorldsCollide.PR
{
    // The plugin's own frame cost, for FPS reports: a [perf] line every 2 minutes with the frame
    // rate, the worst frame, and the milliseconds per second the plugin's parts took (its per-frame
    // runner, the GUI pass, and how often the input hooks run).
    static class WcPerf
    {
        const int Slots = 13;
        static readonly long[] ticks = new long[Slots];
        static readonly long[] worst = new long[Slots];
        static readonly string[] names = new string[Slots];
        internal static long InputCalls;
        static int frames;
        static float worstFrame, since = -1, nextLog = 60;
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        internal static long Now() => Stopwatch.GetTimestamp();

        internal static long Lap(int slot, string name, long t0)
        {
            long t = Stopwatch.GetTimestamp(), d = t - t0;
            ticks[slot] += d;
            if (d > worst[slot]) worst[slot] = d;
            names[slot] = name;
            return t;
        }

        internal static void Frame()
        {
            float now = Time.unscaledTime;
            if (since < 0) since = now;
            frames++;
            float dt = Time.unscaledDeltaTime;
            if (dt > worstFrame && frames > 1) worstFrame = dt;
            if (now < nextLog) return;
            float span = Math.Max(0.001f, now - since);
            var parts = Enumerable.Range(0, Slots).Where(i => names[i] != null && ticks[i] > 0)
                .OrderByDescending(i => ticks[i])
                .Select(i => $"{names[i]} {ticks[i] * TickMs / span:0.00} ms/s (worst {worst[i] * TickMs:0.0} ms)");
            Plugin.L.LogInfo($"[perf] {span:0} s: {frames / span:0.0} fps, worst frame {worstFrame * 1000:0} ms; " +
                             $"WC: {string.Join(", ", parts)}; input hooks {InputCalls / span:0}/s; " +
                             $"story lines skipped {WcEvents.StorySkipped}, kept {WcEvents.StoryKept}");
            Array.Clear(ticks, 0, Slots);
            Array.Clear(worst, 0, Slots);
            InputCalls = 0;
            frames = 0;
            worstFrame = 0;
            since = now;
            nextLog = now + 120;
        }
    }
}

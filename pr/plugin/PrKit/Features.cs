using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace PrKit
{
    // One thing a mod changes: its config switch, its hooks, its per-frame work, its self-check.
    // A new feature is one class (plugin/<Mod>Plugin/<Feature>.cs) added to the plugin's host:
    //
    //   class FasterBattles : Feature
    //   {
    //       public FasterBattles() : base("Battle", "FasterBattles", true, "Battle speed x2 at the fastest setting.") { }
    //       ConfigEntry<float> factor;
    //       public override void Bind() => factor = Setting("Factor", 2f, "How much faster.");
    //       public override void Apply(Harmony h) =>
    //           Hooks.Patch(h, typeof(SomeGameClass), "SomeMethod", typeof(FasterBattles), postfix: nameof(SomeMethod_Postfix));
    //       public static void SomeMethod_Postfix(SomeGameClass __instance, ref float __result)
    //       {
    //           if (!Hooks.IsA<SomeGameClass>(__instance)) return;
    //           ...
    //       }
    //       public override string Check() => Master.Ready ? "battle speed table seen" : null;
    //   }
    //
    // Hooks are static methods (Harmony calls them); keep the feature's state static too, or reach
    // the instance through a static field the constructor sets.
    public abstract class Feature
    {
        protected Feature(string section, string key, bool defaultOn, string description)
        {
            Section = section;
            Key = key;
            DefaultOn = defaultOn;
            Description = description;
        }

        // No switch: always on (what the mod's other features need).
        protected Feature() : this(null, null, true, null) { }

        public string Section { get; }
        public string Key { get; }
        public bool DefaultOn { get; }
        public string Description { get; }
        public bool Enabled { get; internal set; }
        public string Name => Key ?? GetType().Name;
        public FeatureHost Host { get; internal set; }

        // One more setting in this feature's config section.
        protected ConfigEntry<T> Setting<T>(string key, T value, string description) =>
            Host.Config.Bind(Section ?? "General", key, value, description);

        public virtual void Bind() { }                  // read its Setting()s; before Apply
        public virtual void Apply(Harmony harmony) { }  // install its hooks (once, at load)
        public virtual void Update() { }                // every frame
        public virtual void OnGUI() { }                 // Unity's IMGUI pass, every frame

        // Once the game's master data has loaded (Master.Ready): a line for the log saying what the
        // feature found (its data in place, a table's size...), "" for nothing, or null to be asked
        // again half a second later. The log line is how a headless run proves the feature works.
        public virtual string Check() => "";
    }

    // The plugin's features: binds their switches, applies the enabled ones (one failing doesn't
    // stop the others), ticks them from the plugin's runner, runs their checks, times them.
    public sealed class FeatureHost
    {
        public ConfigFile Config { get; }
        public Harmony Harmony { get; }
        public IReadOnlyList<Feature> All => features;
        public bool TestRuns = true;   // ApplyAll picks up a test-run request (TestRun.cs)

        readonly List<Feature> features = new List<Feature>();
        readonly HashSet<Feature> checkedOnes = new HashSet<Feature>();
        readonly Dictionary<Feature, long> cost = new Dictionary<Feature, long>();
        readonly bool perfLog;
        readonly Stopwatch clock = Stopwatch.StartNew();
        long perfSince, frames;
        int frame;

        public FeatureHost(ConfigFile config, Harmony harmony)
        {
            Config = config;
            Harmony = harmony;
            perfLog = config.Bind("Kit", "PerfLog", false,
                "Log what the plugin's features cost per frame, every 2 minutes ([perf] lines).").Value;
        }

        public T Add<T>(T feature) where T : Feature
        {
            feature.Host = this;
            feature.Enabled = feature.Key == null ||
                              Config.Bind(feature.Section ?? "General", feature.Key, feature.DefaultOn, feature.Description ?? "").Value;
            features.Add(feature);
            return feature;
        }

        public T Get<T>() where T : Feature => features.OfType<T>().FirstOrDefault();

        public void ApplyAll()
        {
            var on = new List<string>();
            var off = new List<string>();
            var failed = new List<string>();
            foreach (var f in features)
            {
                if (!f.Enabled) { off.Add(f.Name); continue; }
                try
                {
                    f.Bind();
                    f.Apply(Harmony);
                    on.Add(f.Name);
                }
                catch (Exception e)
                {
                    f.Enabled = false;
                    failed.Add(f.Name);
                    Kit.Error($"[kit] {f.Name} didn't start, so it stays off: {e}");
                }
            }
            Kit.Info($"[kit] features on: {List(on)}; off in the config: {List(off)}" +
                     (failed.Count > 0 ? $"; failed: {List(failed)}" : ""));
            if (TestRuns)
            {
                try { TestRun.StartIfRequested(Harmony); }
                catch (Exception e) { Kit.Error($"[test] request: {e}"); }
            }
        }

        static string List(List<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

        // From the plugin's runner (a MonoBehaviour's Update).
        public void Update()
        {
            frame++;
            if (TestRuns) Safe("test run", TestRun.Update);
            foreach (var f in features)
            {
                if (!f.Enabled) continue;
                long t0 = perfLog ? clock.ElapsedTicks : 0;
                try { f.Update(); }
                catch (Exception e) { Hooks.WarnOnce(f.Name + " update", e); }
                if (perfLog) cost[f] = (cost.TryGetValue(f, out var c) ? c : 0) + clock.ElapsedTicks - t0;
            }
            if (frame % 30 == 0 && checkedOnes.Count < features.Count) RunChecks();
            if (perfLog) Perf();
        }

        public void OnGUI()
        {
            foreach (var f in features)
            {
                if (!f.Enabled) continue;
                try { f.OnGUI(); }
                catch (Exception e) { Hooks.WarnOnce(f.Name + " gui", e); }
            }
        }

        void RunChecks()
        {
            bool ready;
            try { ready = Master.Ready; }
            catch (Exception) { ready = false; }
            if (!ready) return;
            foreach (var f in features)
            {
                if (checkedOnes.Contains(f)) continue;
                if (!f.Enabled) { checkedOnes.Add(f); continue; }
                string line;
                try { line = f.Check(); }
                catch (Exception e) { line = $"check failed: {e.GetType().Name}: {e.Message}"; }
                if (line == null) continue;
                checkedOnes.Add(f);
                if (line.Length > 0) Kit.Info($"[check] {f.Name}: {line}");
            }
        }

        void Perf()
        {
            frames++;
            long now = clock.ElapsedTicks;
            double seconds = (now - perfSince) / (double)Stopwatch.Frequency;
            if (seconds < 120) return;
            double ms = 1000.0 / Stopwatch.Frequency;
            var parts = cost.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.Name} {kv.Value * ms / seconds:0.00} ms/s");
            Kit.Info($"[perf] {seconds:0} s, {frames / seconds:0.0} fps; {string.Join(", ", parts)}");
            cost.Clear();
            frames = 0;
            perfSince = now;
        }

        static void Safe(string where, Action a)
        {
            try { a(); }
            catch (Exception e) { Hooks.WarnOnce(where, e); }
        }
    }
}

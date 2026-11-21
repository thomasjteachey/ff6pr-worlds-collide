using PrKit.Seeds;

namespace WorldsCollide.PR
{
    // Runs the seed generator (tools/wc_seed.py: Worlds Collide + our porter) in a background
    // process with no window, and reports its "[wc] ..." progress lines: the kit's SeedGenerator
    // (PrKit/Seeds; WC's own code until 2026-11-20). WcSeeds applies the finished seed.
    //  Python: config [Generator] Python, else the bundled generator/python/python.exe
    //  (tools/wc_bundle.py), else the project's pr/.venv (development PC).
    static class WcGenerator
    {
        internal static readonly SeedGenerator Gen = new SeedGenerator
        {
            Tag = "wc", Script = "wc_seed.py", SeedJson = "wc_seed.json", BundleDir = () => WcDataDir.Generator,
        };

        internal static string PythonSetting { get => Gen.PythonSetting; set => Gen.PythonSetting = value; }
        internal static string ScriptSetting { get => Gen.ScriptSetting; set => Gen.ScriptSetting = value; }
        internal static string ProjectDir { get => Gen.ProjectDir; set => Gen.ProjectDir = value; }

        public static bool Running => Gen.Running;
        public static string Status => Gen.Status;
        public static double Seconds => Gen.Seconds;
        public static string OutDir => Gen.OutDir;
        public static string Log => Gen.Log;
        public static bool TakeResult(out bool ok, out string error, out string dir) => Gen.TakeResult(out ok, out error, out dir);
        public static (string Python, string Script, string Error) Resolve() => Gen.Resolve();
        public static string Start(string seed, string flags, string dir) => Gen.Start(seed, flags, dir);
        public static void Cancel() => Gen.Cancel();
    }
}

using PrKit.Seeds;

namespace WorldsCollide.PR
{
    // Worlds Collide's data: the seed generator (generator/, with its own Python), the baseline
    // (baseline/: vanilla copies of every file a seed changes) and the seeds (seeds/), in
    // BepInEx/WorldsCollide, beside BepInEx/plugins and not inside it (0.6.4: the Deck's ~6.5 s stall
    // per failed assembly lookup with WC's ~13,700 data folders under plugins). The kit's ModDataDir
    // (PrKit/Seeds; WC's own code until 2026-11-20) moves an older install's folders out of the plugin
    // folder first thing at start: the installed generator and baseline stay, taking the parts made on
    // the player's machine (generator/vanilla and cache, baseline/files and vanilla_data: WcGameData.cs)
    // from the old copy; seeds are merged (duplicates to BepInEx/wc-seed-backups).
    static class WcDataDir
    {
        static ModDataDir dir;
        internal static string Root => dir.Root;
        internal static string Generator => dir.Generator;
        internal static string Baseline => dir.Baseline;
        internal static string Seeds => dir.Seeds;

        static readonly (string Part, string[] Made)[] Shipped =
        {
            ("generator", new[] { "vanilla", "cache" }),
            ("baseline", new[] { "files", "vanilla_data" }),
        };

        internal static void Init()
        {
            dir = new ModDataDir("WorldsCollide", "[wc]");
            dir.Migrate(Plugin.Folder, Shipped, "wc-seed-backups");
        }
    }
}

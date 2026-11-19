using System;
using System.IO;

namespace WorldsCollide.PR
{
    // Worlds Collide's data: the seed generator (generator/, with its own Python), the baseline
    // (baseline/: vanilla copies of every file a seed changes) and the seeds (seeds/), in
    // BepInEx/WorldsCollide, beside BepInEx/plugins and not inside it.
    //
    // Why (2026-11-18, the user on the Steam Deck: "the startup is still INCREDIBLY slow", "the battle
    // entrance hitches for a long time too"): whenever .NET can't find an assembly, BepInEx's resolver
    // (Utility.TryResolveDllAssembly) lists every folder under BepInEx/plugins and tests each for the
    // DLL, and its plugin scan (TypeLoader.FindPluginTypes) lists every file there. Il2CppInterop asks
    // for a missing Il2CppSystem.Private.CoreLib at every class registration (each mod's runner), so
    // with WC's ~13,700 data folders under plugins every registration, and the first battle, took
    // ~6.5 s on the Deck (Wine looks each name up case-insensitively, folder by folder; ~0.6 s on
    // the PC). Measured: perf on the Deck (Wine's lookup_unix_name / NtQueryDirectoryFile) and the
    // failed lookups logged by name.
    //
    // An older install's folders under plugins/WorldsCollide are moved here first thing at start. The
    // player zip (since 0.6.3) and deploy.sh put generator and baseline here, so where both exist this
    // folder's copy is the one just installed: it stays, taking from the old copy only the parts made
    // on the player's machine that it lacks (generator/vanilla and cache, baseline/files and
    // vanilla_data: WcGameData.cs), and the old copy goes. Seeds are merged (a seed already here keeps
    // its copy; the other goes to BepInEx/wc-seed-backups).
    static class WcDataDir
    {
        internal static string Root;
        internal static string Generator => Path.Combine(Root, "generator");
        internal static string Baseline => Path.Combine(Root, "baseline");
        internal static string Seeds => Path.Combine(Root, "seeds");

        static readonly (string Part, string[] Made)[] Shipped =
        {
            ("generator", new[] { "vanilla", "cache" }),
            ("baseline", new[] { "files", "vanilla_data" }),
        };

        internal static void Init()
        {
            Root = Path.Combine(BepInEx.Paths.BepInExRootPath, "WorldsCollide");
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var (part, made) in Shipped)
                {
                    var old = Path.Combine(Plugin.Folder, part);
                    if (!Directory.Exists(old)) continue;
                    var now = Path.Combine(Root, part);
                    if (!Directory.Exists(now))
                    {
                        Directory.Move(old, now);
                        Plugin.L.LogInfo($"[wc] data: {part} moved out of the plugin folder to {now}");
                        continue;
                    }
                    int kept = 0;
                    foreach (var m in made)
                    {
                        var from = Path.Combine(old, m);
                        var to = Path.Combine(now, m);
                        if (Directory.Exists(from) && !Directory.Exists(to)) { Directory.Move(from, to); kept++; }
                    }
                    Directory.Delete(old, true);
                    Plugin.L.LogInfo($"[wc] data: the plugin folder's old {part} removed ({now} is the installed one" +
                                     (kept > 0 ? $"; {kept} part(s) made on this machine moved into it)" : ")"));
                }
                var oldSeeds = Path.Combine(Plugin.Folder, "seeds");
                if (Directory.Exists(oldSeeds))
                {
                    if (!Directory.Exists(Seeds))
                    {
                        Directory.Move(oldSeeds, Seeds);
                        Plugin.L.LogInfo($"[wc] data: seeds moved out of the plugin folder to {Seeds}");
                    }
                    else
                    {
                        int moved = 0, kept = 0;
                        var backup = Path.Combine(BepInEx.Paths.BepInExRootPath, "wc-seed-backups",
                                                  "moved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                        foreach (var entry in Directory.GetFileSystemEntries(oldSeeds))
                        {
                            var name = Path.GetFileName(entry);
                            var dest = Path.Combine(Seeds, name);
                            if (!Directory.Exists(dest) && !File.Exists(dest)) { Move(entry, dest); moved++; continue; }
                            Directory.CreateDirectory(backup);
                            Move(entry, Path.Combine(backup, name));
                            kept++;
                        }
                        Directory.Delete(oldSeeds, true);
                        Plugin.L.LogInfo($"[wc] data: {moved} seed entries moved to {Seeds}" +
                                         (kept > 0 ? $"; {kept} already there (the plugin folder's copies are in {backup})" : ""));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"[wc] data: moving WC's folders out of the plugin folder failed: {e.Message}");
            }
        }

        static void Move(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
    }
}

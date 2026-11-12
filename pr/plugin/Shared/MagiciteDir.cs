using System;
using System.IO;

namespace Ff6Mod.Shared
{
    // Where Magicite loads mods from (BepInEx/config/silvris.magicite.cfg, [Magicite Paths]
    // Import Directory; the standard one is %StreamingAssets%/Magicite). Each mod checks that
    // its own data folder is there before it changes the game. Brave New World and Worlds
    // Collide are separate mods, installed one at a time (the user, 2026-11-07: "WC/BNW need to
    // be separate mods entirely"; tools/deploy.sh --bnw / --wc): neither knows about the other.
    public static class MagiciteDir
    {
        const string Key = "Import Directory";
        public const string Standard = "%StreamingAssets%/Magicite";

        static string CfgPath => Path.Combine(BepInEx.Paths.ConfigPath, "silvris.magicite.cfg");
        static string StreamingAssets => Path.Combine(BepInEx.Paths.GameDataPath, "StreamingAssets");

        // read once: what Magicite loaded at startup
        static string setting;
        public static string Setting => setting ??= ReadSetting() ?? Standard;

        public static string ImportDir => Resolve(Setting);

        public static string Resolve(string value) =>
            Path.GetFullPath(value.Replace("%StreamingAssets%", StreamingAssets).Replace("%DataPath%", BepInEx.Paths.GameDataPath));

        public static bool Has(string folder) => folder != null && Directory.Exists(Path.Combine(ImportDir, folder));

        public static string FolderPath(string folder) => Path.Combine(ImportDir, folder);

        static string ReadSetting()
        {
            try
            {
                if (!File.Exists(CfgPath)) return null;
                foreach (var l in File.ReadAllLines(CfgPath))
                {
                    var t = l.Trim();
                    if (t.StartsWith("#") || !t.StartsWith(Key, StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0) return t.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }
    }
}

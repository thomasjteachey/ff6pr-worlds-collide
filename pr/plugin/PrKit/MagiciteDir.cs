using System;
using System.IO;

namespace PrKit
{
    // Where Magicite loads mods from (BepInEx/config/silvris.magicite.cfg, [Magicite Paths] Import
    // Directory; the standard one is %StreamingAssets%/Magicite). A plugin that changes the game's
    // rules on top of its own data checks that data is installed first:
    //   if (!MagiciteDir.Has("MyMod")) { Kit.Warn("MyMod's data isn't installed: off"); return; }
    // (else it would put its rules on the game's own data).
    public static class MagiciteDir
    {
        const string Key = "Import Directory";
        public const string Standard = "%StreamingAssets%/Magicite";

        static string CfgPath => Path.Combine(Game.ConfigDir, "silvris.magicite.cfg");

        // read once: what Magicite loaded at startup
        static string setting;
        public static string Setting => setting ??= ReadSetting() ?? Standard;

        public static string ImportDir => Resolve(Setting);

        public static string Resolve(string value) =>
            Path.GetFullPath(value.Replace("%StreamingAssets%", Game.StreamingAssets).Replace("%DataPath%", Game.DataDir));

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
            catch (Exception) { }
            return null;
        }
    }
}

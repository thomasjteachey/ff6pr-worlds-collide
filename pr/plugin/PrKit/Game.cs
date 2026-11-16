using System;
using System.IO;
using System.Linq;

namespace PrKit
{
    public enum PrGame { Unknown = 0, FF1 = 1, FF2 = 2, FF3 = 3, FF4 = 4, FF5 = 5, FF6 = 6 }

    // Which Pixel Remaster is running, and its folders.
    //
    // All six share one engine: the Last.* namespaces (master data, messages, maps, events, battle,
    // menus, saves). Each game adds its own code under Serial.FF<n>.* (FF6's espers:
    // Serial.FF6.Management.MagicStoneData). A plugin is compiled against one game's interop
    // assemblies (PrKit.props: <PrGame>), so code that uses only Last.* types usually runs in the
    // other games too, while Serial.FF<n> code never does: call Game.Require() for those.
    public static class Game
    {
        static readonly string[] Titles =
            { "", "FINAL FANTASY", "FINAL FANTASY II", "FINAL FANTASY III", "FINAL FANTASY IV", "FINAL FANTASY V", "FINAL FANTASY VI" };
        static readonly int[] AppIds = { 0, 1173770, 1173780, 1173790, 1173800, 1173810, 1173820 };
        static PrGame? current;

        public static PrGame Current
        {
            get
            {
                if (current == null) current = FromProcessName(ProcessName());
                return current.Value;
            }
        }

        // "FINAL FANTASY V" / "FINAL FANTASY V.exe" / "final fantasy v pr" -> FF5
        public static PrGame FromProcessName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return PrGame.Unknown;
            var n = name.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            n = Path.GetFileName(n).Trim().ToUpperInvariant();
            if (n.EndsWith(" PR")) n = n.Substring(0, n.Length - 3).TrimEnd();
            for (int i = Titles.Length - 1; i >= 1; i--)
                if (n == Titles[i]) return (PrGame)i;
            return PrGame.Unknown;
        }

        public static string Title(PrGame g) => (int)g >= 1 && (int)g < Titles.Length ? Titles[(int)g] : "an unknown game";
        public static int SteamAppId(PrGame g) => (int)g >= 1 && (int)g < AppIds.Length ? AppIds[(int)g] : 0;

        public static bool Is(params PrGame[] games) => games.Contains(Current);

        // For a plugin (or one feature) built on one game's own code: true when that game runs,
        // else logs that it stays off.
        public static bool Require(params PrGame[] games)
        {
            if (Is(games)) return true;
            Kit.Warn($"[kit] {Kit.ModName} is made for {string.Join(" / ", games.Select(g => Title(g) + " PR"))}; " +
                     $"this is {Describe()}: it stays off");
            return false;
        }

        public static string Describe() => Current == PrGame.Unknown ? $"unknown ({ProcessName() ?? "?"})" : Title(Current) + " PR";

        static string ProcessName()
        {
            try { return BepInEx.Paths.ProcessName; }
            catch (Exception) { return null; }
        }

        public static string Root => BepInEx.Paths.GameRootPath;
        public static string DataDir => BepInEx.Paths.GameDataPath;
        public static string StreamingAssets => Path.Combine(DataDir, "StreamingAssets");
        public static string BepInExDir => BepInEx.Paths.BepInExRootPath;
        public static string ConfigDir => BepInEx.Paths.ConfigPath;
        public static string PluginsDir => BepInEx.Paths.PluginPath;
    }
}

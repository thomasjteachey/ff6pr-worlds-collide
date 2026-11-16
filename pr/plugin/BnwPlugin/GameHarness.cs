using System.Collections.Generic;
using HarmonyLib;
using Last.Management;
using Harness = PrKit.Harness;

namespace BraveNewWorld.PR
{
    // A test harness to play the game headless (the user, 2026-11-07: "make a test harness by
    // which you can communicate with the game efficiently and play the first battle yourself").
    // Since 2026-11-11 the harness itself is the modding kit's (plugin/PrKit/Harness.cs, pr/MODDING.md):
    // the 127.0.0.1:47811 listener, injected key presses, and ping | help | state | press | wait |
    // newgame | slots | load | warp | script | battle | encount | log. Brave New World adds its own:
    //   party            each character's level, magicite and spells (known, still learning)
    //   skipmsg on|off   the probe's message skip (FormulaProbe.SkipMessages) as well as the kit's
    //   quit             ends the probe run ("[probe] done", which tools/probe_headless.ps1 waits for)
    // Headless runs only: the probe request's "harness=1" (tools/probe_headless.ps1 -Extra "harness=1");
    // the run's saves are blocked like every probe run's (FormulaProbe.PatchNoSaves). The client is
    // tools/game_harness.py.
    static class GameHarness
    {
        internal static bool Active => Harness.Active;

        public static void Start(Harmony h)
        {
            Harness.Register("party", _ => Party(), "party: each character's level, magicite and spells");
            Harness.Register("skipmsg", a =>
            {
                bool on = a.Length > 0 && a[0] == "on";
                FormulaProbe.SkipMessages = Harness.SkipMessages = on;
                return $"messages {(on ? "skipped" : "shown")}";
            }, "skipmsg on|off: scripts' message boxes complete at once");
            Harness.Register("quit", _ =>
            {
                FormulaProbe.Quit("harness: quit");
                return "quitting";
            }, "quit: end the probe run");
            Harness.Start(h);
        }

        public static void Update() => Harness.Update();

        static string Party()
        {
            var udm = UserDataManager.Instance();
            var list = udm?.OwnedCharacterList;
            if (list == null) return "no characters";
            var parts = new List<string>();
            foreach (var c in list)
            {
                if (c == null) continue;
                var known = new List<int>();
                var partial = new List<string>();
                if (c.OwnedAbilityList != null)
                    foreach (var a in c.OwnedAbilityList)
                    {
                        if (a?.Ability == null) continue;
                        if (a.SkillLevel >= 100) known.Add(a.Ability.Id);
                        else partial.Add($"{a.Ability.Id}:{a.SkillLevel}");
                    }
                known.Sort();
                parts.Add($"{c.Name} ({c.characterStatusId}, lv {c.Parameter.ConfirmedLevel()}, magicite {c.MagicStoneId}): {known.Count} known [{string.Join(" ", known)}]" +
                          (partial.Count > 0 ? $"; below 100 [{string.Join(" ", partial)}]" : ""));
            }
            var magic = new List<int>();
            if (udm.OwnedMagicList != null) foreach (var id in udm.OwnedMagicList) magic.Add(id);
            magic.Sort();
            return string.Join(" | ", parts) + $" || party magic list {magic.Count} [{string.Join(" ", magic)}]";
        }
    }
}

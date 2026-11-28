using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data.User;
using Last.Management;

namespace WorldsCollide.PR
{
    // WC's skills follow the commands. In the SNES, known SwdTechs, Blitzes, Lores, Dances and Rages
    // are party-wide: whoever holds the command (WC randomizes commands) uses the party's set. In the
    // PR they are each character's own abilities, so a WC game shares them: every character with one
    // of these commands knows what any owned character knows of that set (taught at 100%). Synced
    // every few seconds in the field (WcObjectives.Poll), on joins and after objective results.
    static class WcSkills
    {
        // (PR command id, first ability, last ability)
        internal static readonly (int Command, int First, int Last)[] Sets =
        {
            (12, 124, 131),   // Bushido
            (15, 132, 139),   // Blitz
            (17, 140, 163),   // Lore
            (22, 164, 171),   // Dance
            (23, 800, 1054),  // Rage (ability 799 + PR monster id)
        };

        internal static string LastSync = "";

        static bool Has(OwnedCharacterData c, int command)
        {
            var list = c.CommandList;
            if (list == null) return false;
            foreach (var x in list) if (x != null && x.Id == command) return true;
            return false;
        }

        // known = learned to 100. Rages too: the PR gives Gau an entry for every rage, the unknown
        // ones at 0 (counting any entry shared all 252 at a WC start: the user's Deck seed). Both
        // copies of a character's abilities (list + the menu's dictionary): the kit's StartSkills
        internal static bool Knows(OwnedCharacterData c, int id) => PrKit.Ff6.StartSkills.Knows(c, id);

        internal static bool Teach(OwnedCharacterData c, int id) => PrKit.Ff6.StartSkills.Teach(c, id);

        // the owned character who should learn a technique set first (a holder of its command, else
        // nobody: the result waits for one)
        internal static OwnedCharacterData Holder(int command) =>
            Owned().FirstOrDefault(c => Has(c, command));

        static IEnumerable<OwnedCharacterData> Owned()
        {
            var list = UserDataManager.Instance()?.OwnedCharacterList;
            if (list == null) yield break;
            foreach (var c in list)
                if (c != null && c.characterStatusId >= 1 && c.characterStatusId <= 14) yield return c;
        }

        public static string Sync()
        {
            var report = new List<string>();
            try
            {
                var owned = Owned().ToList();
                foreach (var (command, first, last) in Sets)
                {
                    var holders = owned.Where(c => Has(c, command)).ToList();
                    if (holders.Count == 0) continue;
                    var known = new HashSet<int>();
                    foreach (var c in owned) known.UnionWith(PrKit.Ff6.StartSkills.Known(c, first, last));   // either copy
                    int taught = 0;
                    foreach (var h in holders)
                        foreach (var id in known)
                            if (!Knows(h, id) && Teach(h, id)) taught++;
                    if (taught > 0) report.Add($"command {command}: {taught} taught to {string.Join(",", holders.Select(h => h.characterStatusId))}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("skills-sync", e); }
            if (report.Count > 0)
            {
                LastSync = string.Join("; ", report);
                Plugin.L.LogInfo("[wc] skills shared: " + LastSync);
            }
            return string.Join("; ", report);
        }
    }
}

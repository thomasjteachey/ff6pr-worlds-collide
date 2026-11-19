using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data.Master;
using Last.Data.User;
using Last.Management;

namespace WorldsCollide.PR
{
    // WC's natural magic. In the SNES a character learns spells from its natural-magic list when it
    // joins and as it levels (data/natural_magic.py: the "learn spells for the current level" event
    // call and the after-battle check, both only for the seed's learners, -nm1/-nm2); everything
    // else comes from espers, the "Learn Spells" objective, -scan/-warp or gear. The port moves the
    // lists into the learning table (port_spells.py: type-1 rows on the learner's job), but the PR
    // also builds spells into its characters outside that table: Terra is created knowing Cure and
    // Fire (her rows start at level 6). A seed that gives her list to someone else, or to nobody,
    // still left her both (the user's seed gaadl68ix7j4: Terra's list goes to Cyan).
    static class WcSpells
    {
        internal const int First = 31, Last = 84;   // the PR's spells (white 31-39, black 40-63, gray 64-84)
        internal const int Scan = 64, Warp = 82;    // Libra, Teleport (-scan / -warp)
        // the spells the PR builds into a character outside its learning rows (WcTest "spells"
        // joins every character and logs what its first recruit removed)
        internal static readonly Dictionary<int, int[]> BuiltIn = new Dictionary<int, int[]>
        {
            [1] = new[] { 31, 40 },   // Terra: Cure, Fire
            [7] = new[] { 41 },       // Celes: Blizzard (her list went to Sabin in the user's seed; she still knew it)
        };

        internal static bool IsSpell(int id) => id >= First && id <= Last;

        static bool Known(OwnedAbility a) => a?.Ability != null && IsSpell(a.Ability.Id) && a.SkillLevel > 0;

        // the seed's natural magic for this character up to `level`: type-1 learning rows on its job
        internal static HashSet<int> Natural(OwnedCharacterData c, int level)
        {
            var set = new HashSet<int>();
            var mm = MasterManager.Instance;
            if (c == null || mm == null) return set;
            bool learner = false, cureRow = false;
            foreach (var kv in mm.GetList<Learning>())
            {
                var l = kv.Value;
                if (l == null || l.TypeId != 1 || l.JobId != c.JobId) continue;
                var content = mm.GetData<Content>(l.ContentId);
                if (content == null || content.TypeId != 4 || !IsSpell(content.TypeValue)) continue;
                learner = true;
                if (content.TypeValue == 31) cureRow = true;
                if (l.Value1 <= level) set.Add(content.TypeValue);
            }
            // Seeds made before port_spells wrote a Terra learner's level-1 spell left that row out
            // (it took the PR's built-in Cure for it). With her own spells (no -rns1) that spell is
            // vanilla's level-1 Cure, so a learner Terra with no Cure row keeps it there. With
            // random spells the dropped one can't be known: those old seeds just lose it.
            var s = WcSeeds.Active;
            if (c.characterStatusId == 1 && s != null && !s.NaturalLv1 && learner && !cureRow && !s.Flags.Split(' ').Contains("-rns1"))
                set.Add(31);
            return set;
        }

        // what a WC game gives anyone regardless of natural magic: -scan / -warp, and the
        // "Learn Spells" objective's table once one is complete (it teaches every owned character)
        internal static HashSet<int> Granted()
        {
            var set = new HashSet<int>();
            var s = WcSeeds.Active;
            if (s == null) return set;
            var flags = s.Flags.Split(' ');
            if (flags.Contains("-scan")) set.Add(Scan);
            if (flags.Contains("-warp")) set.Add(Warp);
            if (WcObjectives.Completed("Learn Spells")) set.UnionWith(s.SpellTable);
            return set;
        }

        // A first WC recruit, at its final level (after -sal): knows its natural magic up to that
        // level and nothing else, as the SNES's join call leaves it. Gives what the PR left out,
        // removes what the PR built in.
        internal static string Recruit(int statusId)
        {
            var c = UserDataManager.Instance()?.OwnedCharacterList?.ToArray().FirstOrDefault(x => x != null && x.characterStatusId == statusId);
            if (c == null || c.OwnedAbilityList == null) return "";
            int level = c.Parameter.ConfirmedLevel();
            var natural = Natural(c, level);
            var keep = new HashSet<int>(natural);
            keep.UnionWith(Granted());
            var dropped = new List<int>();
            foreach (var a in c.OwnedAbilityList)
                if (Known(a) && !keep.Contains(a.Ability.Id)) { a.SkillLevel = 0; dropped.Add(a.Ability.Id); }
            var taught = new List<int>();
            foreach (var id in natural.OrderBy(x => x))
                if (!WcSkills.Knows(c, id) && WcSkills.Teach(c, id)) taught.Add(id);
            if (dropped.Count == 0 && taught.Count == 0) return "";
            var what = $"character {statusId} at level {level}: spells un-learned [{string.Join(" ", dropped)}] (not its natural magic), learned [{string.Join(" ", taught)}]";
            Plugin.L.LogInfo("[wc] natural magic: " + what);
            return what;
        }

        // Saves recruited before this: a built-in spell nothing in the seed could have taught goes.
        // Kept when the seed's natural magic gives it by the character's level, an owned esper or
        // any gear teaches it, or -scan/-warp/"Learn Spells" do (the character may have learned it
        // there: the PR doesn't record where a spell came from).
        internal static string RepairBuiltIns()
        {
            var s = WcSeeds.Active;
            var udm = UserDataManager.Instance();
            var owned = udm?.OwnedCharacterList;
            if (s == null || owned == null) return "";
            var mm = MasterManager.Instance;
            HashSet<int> espers = null, gear = null, granted = null;
            var done = new List<string>();
            foreach (var c in owned)
            {
                if (c == null || !BuiltIn.TryGetValue(c.characterStatusId, out var builtIn) || !WcEvents.IsRecruited(c.characterStatusId) ||
                    c.OwnedAbilityList == null) continue;
                HashSet<int> natural = null;
                foreach (var a in c.OwnedAbilityList)
                {
                    if (!Known(a) || !builtIn.Contains(a.Ability.Id)) continue;
                    if (espers == null)
                    {
                        espers = new HashSet<int>(); gear = new HashSet<int>();
                        var stones = udm.OwnedMagicStoneList;
                        foreach (var kv in mm.GetList<Learning>())
                        {
                            var l = kv.Value;
                            if (l == null || (l.TypeId != 2 && l.TypeId != 4)) continue;
                            var content = mm.GetData<Content>(l.ContentId);
                            if (content == null || content.TypeId != 4) continue;
                            if (l.TypeId == 4) gear.Add(content.TypeValue);
                            else if (stones != null && stones.Contains(l.Value1)) espers.Add(content.TypeValue);
                        }
                        granted = Granted();
                    }
                    natural ??= Natural(c, c.Parameter.ConfirmedLevel());
                    int id = a.Ability.Id;
                    if (natural.Contains(id) || espers.Contains(id) || gear.Contains(id) || granted.Contains(id)) continue;
                    a.SkillLevel = 0;
                    done.Add($"{c.characterStatusId}:{id}");
                }
            }
            if (done.Count == 0) return "";
            var what = string.Join(" ", done);
            Plugin.L.LogInfo($"[wc] natural magic: built-in spells nothing in the seed teaches un-learned ({what})");
            return what;
        }
    }
}

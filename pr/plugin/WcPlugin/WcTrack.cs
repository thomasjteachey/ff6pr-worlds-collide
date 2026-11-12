using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Last.Data;
using Last.Defaine;
using Last.Interpreter;
using Last.Management;

namespace WorldsCollide.PR
{
    // Worlds Collide's Track menu (its menus/track.py): a "Track" entry in the field menu that
    // opens the WC screen on its Track pages: Checks (WC's 62, by gating character, done ones
    // dimmed, characters you haven't found hidden as in WC), Progress, Objectives and Flags.
    // The check list is baseline/wc_track.json (tools/wc_events.py write_track: WC's check
    // names, gates and the ScenarioFlag4 done flag of each check the PR port has).
    // How the game builds the field menu (decompiled, tools/decomp/mainmenu): KeyInput
    // MainMenuController.CreateCommandMenu gives CommandMenuController.Initalize a list of
    // CommandMenuContentData (MenuCommandId, message key); a pick calls the controller's
    // OnKeyAction -> MainMenuController.OnCommandMenuKeyAction(id). Our entry is id 100.
    static class WcTrack
    {
        const int MenuId = 100;
        const string MenuKey = "MENU_WC_TRACK";
        const string MessageId = "MSG_WC_TRACK_MENU";
        const string Label = "Track";

        internal sealed class TrackCheck { public string Name; public int Gate, Flag; }
        static List<TrackCheck> checks;
        internal static int Ported;

        internal static List<TrackCheck> Checks
        {
            get
            {
                if (checks != null) return checks;
                checks = new List<TrackCheck>();
                try
                {
                    var path = Path.Combine(Plugin.Folder, "baseline", "wc_track.json");
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var c in doc.RootElement.GetProperty("checks").EnumerateArray())
                        checks.Add(new TrackCheck
                        {
                            Name = c.GetProperty("name").GetString(),
                            Gate = c.GetProperty("gate").GetInt32(),
                            Flag = c.GetProperty("flag").GetInt32(),
                        });
                    Ported = checks.Count(c => c.Flag >= 0);
                }
                catch (Exception e) { WcRunner.WarnOnce("track-list", e); }
                return checks;
            }
        }

        public static void Patch(Harmony h)
        {
            var init = AccessTools.Method(typeof(Last.UI.CommandMenuController), "Initalize");
            var pick = AccessTools.Method(typeof(Last.UI.KeyInput.MainMenuController), "OnCommandMenuKeyAction");
            if (init != null)
            {
                h.Patch(init, prefix: new HarmonyMethod(typeof(WcTrack), nameof(Initalize_Prefix)),
                              postfix: new HarmonyMethod(typeof(WcTrack), nameof(Initalize_Postfix)));
            }
            if (pick != null) h.Patch(pick, prefix: new HarmonyMethod(typeof(WcTrack), nameof(Pick_Prefix)));
            Plugin.L.LogInfo($"[wc] track menu entry: {(init != null && pick != null ? "hooked" : "not found")}");
        }

        // a Worlds Collide game: the WC mod and a seed in play
        internal static bool Wanted => Plugin.WcMode && WcSeeds.Active != null;

        internal static int LastEntries = -1;   // tests: the menu's entry count after our change
        internal static bool LastAdded;

        // -nosaves: Quick Save (8) and Save (9) taken out of the menu's list, put back when the game
        // isn't an Ironmog one (the list can be the menu's own, kept between openings)
        static readonly List<CommandMenuContentData> hiddenSaves = new List<CommandMenuContentData>();

        static void SaveEntries(Il2CppSystem.Collections.Generic.List<CommandMenuContentData> data)
        {
            if (WcSeeds.IronMog())
            {
                for (int i = data.Count - 1; i >= 0; i--)
                {
                    var c = data[i];
                    if (c == null || !(c.Id == MenuCommandId.Save || (int)c.Id == 8)) continue;
                    if (!hiddenSaves.Any(h => h.Id == c.Id)) hiddenSaves.Insert(0, c);
                    data.RemoveAt(i);
                }
                return;
            }
            if (hiddenSaves.Count == 0) return;
            int at = data.Count;
            for (int i = 0; i < data.Count; i++)
                if (data[i] != null && (data[i].Id == MenuCommandId.Config || (int)data[i].Id == MenuId)) at = i + 1;
            foreach (var c in hiddenSaves.OrderBy(h => (int)h.Id))
            {
                bool present = false;
                for (int i = 0; i < data.Count; i++) if (data[i]?.Id == c.Id) present = true;
                if (!present) data.Insert(Math.Min(at++, data.Count), c);
            }
            hiddenSaves.Clear();
        }

        public static void Initalize_Prefix(Il2CppSystem.Collections.Generic.List<CommandMenuContentData> data)
        {
            try
            {
                if (data == null) return;
                try { SaveEntries(data); } catch (Exception e) { WcRunner.WarnOnce("nosaves-menu", e); }
                int ours = -1, config = -1;
                for (int i = 0; i < data.Count; i++)
                {
                    var c = data[i];
                    if (c == null) continue;
                    if ((int)c.Id == MenuId) ours = i;
                    if (config < 0 && c.Id == MenuCommandId.Config) config = i;
                }
                // the list can be the menu's own (kept between openings): add once, drop it again
                // when the game isn't a WC one
                if (!Wanted)
                {
                    if (ours >= 0) data.RemoveAt(ours);
                    LastAdded = false;
                    LastEntries = data.Count;
                    return;
                }
                if (ours < 0)
                {
                    var msg = MessageManager.Instance;
                    if (msg != null)
                    {
                        var conclusion = msg.messageConclusionDictionary;
                        if (conclusion != null) conclusion[MenuKey] = MessageId;
                        if (msg.messageDictionary.ContainsKey(MessageId)) msg.messageDictionary[MessageId] = Label;
                        else msg.AddMessage(MessageId, Label);
                    }
                    var entry = new CommandMenuContentData((MenuCommandId)MenuId, MenuKey);
                    // WC puts Track above Config and Save
                    if (config < 0) data.Add(entry); else data.Insert(config, entry);
                }
                LastAdded = true;
                LastEntries = data.Count;
            }
            catch (Exception e) { WcRunner.WarnOnce("track-menu", e); }
        }

        static bool labelLogged;

        public static void Initalize_Postfix(Last.UI.CommandMenuController __instance)
        {
            try
            {
                var views = __instance?.contents;
                if (views == null) return;
                for (int i = 0; i < views.Count; i++)
                {
                    var v = views[i];
                    if (v?.Data == null || (int)v.Data.Id != MenuId || v.NameText == null) continue;
                    if (v.NameText.text != Label) v.NameText.text = Label;
                    if (!labelLogged) { labelLogged = true; Plugin.L.LogInfo("[wc] track menu: entry added"); }
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("track-label", e); }
        }

        public static bool Pick_Prefix(MenuCommandId id)
        {
            if ((int)id != MenuId) return true;
            try
            {
                Plugin.L.LogInfo("[wc] track menu: opening");
                WcScreen.Show(true, track: true);
            }
            catch (Exception e) { WcRunner.WarnOnce("track-open", e); }
            return false;
        }

        // --- what the pages read --------------------------------------------------------------

        internal static bool Done(TrackCheck c) => c.Flag >= 0 && WcEvents.Flag(DataStorage.Category.kScenarioFlag4, c.Flag) == 1;
        internal static bool Gating => WcSeeds.Active?.Flags.Split(' ').Contains("-cg") ?? false;

        internal static readonly string[] CharacterNames =
            { "", "Terra", "Locke", "Cyan", "Shadow", "Edgar", "Sabin", "Celes", "Strago", "Relm", "Setzer", "Mog", "Gau", "Gogo", "Umaro" };

        internal static int CharactersFound()
        {
            int n = 0;
            for (int id = 1; id <= 14; id++) if (WcEvents.IsRecruited(id)) n++;
            return n;
        }

        internal static int EspersFound()
        {
            try { return UserDataManager.Instance()?.OwnedMagicStoneList?.Count ?? 0; }
            catch { return 0; }
        }

        // WC's dragons by its names (constants/objectives/condition_bits.py) -> PR "defeated" flags
        // (ScenarioFlag2, the ones the countdown sc_e_0975 counts)
        internal static readonly Dictionary<string, int> DragonFlags = new Dictionary<string, int>
        {
            ["Ice Dragon"] = 138, ["Storm Drgn"] = 108, ["Dirt Drgn"] = 178, ["Gold Drgn"] = 225,
            ["Skull Drgn"] = 220, ["Blue Drgn"] = 175, ["Red Dragon"] = 148, ["White Drgn"] = 166,
        };

        internal static int DragonsDefeated() =>
            DragonFlags.Values.Count(f => WcEvents.Flag(DataStorage.Category.kScenarioFlag2, f) == 1);

        // WC's bosses defeated: TreasureFlag4 0+ (boss list index; vanilla uses only 135/136 there),
        // set when a battle with that boss's party is won (WcScaling's reward hook)
        internal static bool BossDefeated(int index) => index >= 0 && index < 128 && WcEvents.Flag(DataStorage.Category.kTreasureFlag4, index) == 1;

        internal static int BossesDefeated()
        {
            var s = WcSeeds.Active;
            return s == null ? 0 : Enumerable.Range(0, s.Bosses.Count).Count(BossDefeated);
        }

        // WC's quests (condition_bits.quest_bit, its order): done flags ScenarioFlag4 70+i. Battle
        // quests count the event's battle (WcBattles.CurrentOriginal: the party its script named,
        // whatever WC put there); the Coliseum and the auction set theirs; story ones read the PR's
        // own flag for that moment (QuestStory)
        internal static readonly string[] QuestNames =
        {
            "Defeat Sealed Cave Ninja", "Help Injured Lad", "Let Cid Die", "Pass Security Checkpoint", "Perform In Opera",
            "Save Cid", "Set Zozo Clock", "Suplex A Train", "Win An Auction", "Win A Coliseum Match", "Defeat KT Ambusher",
            "Defeat KT Robot", "Defeat KT Left Statue", "Defeat KT Mid Statue", "Defeat KT Right Statue",
        };
        const int QuestFlag = 70;

        // story quests: the PR flag its own scene sets at that moment (category, index)
        // (research 2026-11-05: each set only by that scene). The checkpoint and the Zozo clock set
        // the quest's own flag from tools/wc_events.py hooks; Suplex from the battle (SuplexCheck).
        // Perform In Opera: the performance's end in a WC game (wc_events.opera_performance)
        internal static readonly Dictionary<string, (DataStorage.Category Category, int Index)> QuestStory =
            new Dictionary<string, (DataStorage.Category, int)>
        {
            ["Help Injured Lad"] = (DataStorage.Category.kScenarioFlag3, 5),   // Mobliz's last letter's reward (sc_e_0238)
            ["Save Cid"] = (DataStorage.Category.kScenarioFlag2, 49),          // "I'm all better now!" (sc_e_0625)
            ["Let Cid Die"] = (DataStorage.Category.kScenarioFlag3, 13),       // Cid's house's "dead" state (sc_e_0616)
        };
        static readonly HashSet<string> QuestHooked = new HashSet<string> { "Pass Security Checkpoint", "Set Zozo Clock", "Suplex A Train", "Perform In Opera" };

        internal static bool QuestDone(string name)
        {
            int i = Array.IndexOf(QuestNames, name);
            if (i < 0) return false;
            if (WcEvents.Flag(DataStorage.Category.kScenarioFlag4, QuestFlag + i) == 1) return true;
            return QuestStory.TryGetValue(name, out var f) && WcEvents.Flag(f.Category, f.Index) == 1;
        }

        internal static bool QuestTracked(string name) =>
            name == "Win An Auction" || name == "Win A Coliseum Match" || QuestStory.ContainsKey(name) || QuestHooked.Contains(name) ||
            (WcSeeds.Active?.Quests.ContainsKey(name) ?? false);

        internal static void SetQuest(string name)
        {
            int i = Array.IndexOf(QuestNames, name);
            if (i < 0 || !Plugin.WcMode || WcSeeds.Active == null || QuestDone(name)) return;
            DataStorage.Instance?.Set(DataStorage.Category.kScenarioFlag4, QuestFlag + i, 1);
            Plugin.L.LogInfo($"[wc] quest done: {name}");
        }

        internal static void BattleWon(int party)
        {
            var s = WcSeeds.Active;
            if (s == null || !Plugin.WcMode) return;
            try
            {
                int origin = WcBattles.CurrentOriginal;
                foreach (var q in s.Quests)
                    if (origin > 0 && q.Value.Contains(origin)) SetQuest(q.Key);
                if (party >= 1000 || (WcColiseum.MatchParty > 0 && party == WcColiseum.MatchParty)) SetQuest("Win A Coliseum Match");
                WcRages.BattleWon();
            }
            catch (Exception e) { WcRunner.WarnOnce("quest-won", e); }
            if (party < 0) return;
            for (int i = 0; i < s.Bosses.Count && i < 128; i++)
                if (s.Bosses[i].Party == party && !BossDefeated(i))
                {
                    DataStorage.Instance?.Set(DataStorage.Category.kTreasureFlag4, i, 1);
                    Plugin.L.LogInfo($"[wc] boss defeated: {s.Bosses[i].Name} (party {party}; {BossesDefeated()} so far)");
                }
        }

        // a condition: met, not met, or null when the port can't tell yet (bosses, quests, checks
        // not in the port)
        internal static bool? Met(WcSeeds.Condition c)
        {
            switch (c.Type)
            {
                case "Character": return WcEvents.IsRecruited(c.Status);
                case "Characters": return CharactersFound() >= c.Count;
                case "Esper":
                    try { return UserDataManager.Instance()?.OwnedMagicStoneList?.Contains(c.Content) ?? false; }
                    catch { return false; }
                case "Espers": return EspersFound() >= c.Count;
                case "Check":
                    var check = Checks.FirstOrDefault(k => k.Name == c.Name);
                    if (check == null || check.Flag < 0) return null;
                    return Done(check);
                case "Checks": return Checks.Count(Done) >= c.Count;
                case "Dragon": return DragonFlags.TryGetValue(c.Name, out var f) ? WcEvents.Flag(DataStorage.Category.kScenarioFlag2, f) == 1 : (bool?)null;
                case "Dragons": return DragonsDefeated() >= c.Count;
                case "Bosses": return WcSeeds.Active?.Bosses.Count > 0 ? BossesDefeated() >= c.Count : (bool?)null;
                case "Boss":
                    var bi = WcSeeds.Active?.Bosses.FindIndex(b => b.Name == c.Name) ?? -1;
                    return bi < 0 ? (bool?)null : BossDefeated(bi);
                case "Quest":
                    if (QuestDone(c.Name)) return true;
                    return QuestTracked(c.Name) ? false : (bool?)null;
                default: return null;
            }
        }

        // where a condition stands, for its row ("3 / 6", "Done")
        internal static string Progress(WcSeeds.Condition c)
        {
            switch (c.Type)
            {
                case "Characters": return $"{CharactersFound()} / {c.Count}";
                case "Espers": return $"{EspersFound()} / {c.Count}";
                case "Checks": return $"{Checks.Count(Done)} / {c.Count}";
                case "Dragons": return $"{DragonsDefeated()} / {c.Count}";
                case "Bosses": if (WcSeeds.Active?.Bosses.Count > 0) return $"{BossesDefeated()} / {c.Count}"; break;
            }
            var met = Met(c);
            return met == true ? "Done" : met == false ? "" : "(not tracked yet)";
        }

        internal static bool Complete(WcSeeds.Objective o) => o.Conditions.Count(c => Met(c) == true) >= o.Required;
    }

    static partial class WcScreen
    {
        internal static bool Track;     // showing the Track pages (from the field menu) instead of the options
        static readonly string[] TrackPages = { "Checks", "Progress", "Objectives", "Flags" };

        static List<UiRow> TrackRows()
        {
            switch (page)
            {
                case 0: return TrackChecks();
                case 1: return TrackProgress();
                case 2: return TrackObjectives();
                default: return TrackFlags(objectives: false);
            }
        }

        static UiRow CheckRow(WcTrack.TrackCheck c, int indent)
        {
            bool done = WcTrack.Done(c);
            return new UiRow
            {
                Label = c.Name,
                Indent = indent,
                Value = done ? "Done" : c.Flag < 0 ? "(vanilla event)" : "",
                Disabled = done,
                Help = c.Flag < 0 ? "Not in the Pixel Remaster port yet: this place plays its vanilla event and gives no WC reward."
                     : done ? "Done." : c.Gate > 0 && WcTrack.Gating ? $"Gated by {WcTrack.CharacterNames[c.Gate]}." : "Open.",
            };
        }

        static List<UiRow> TrackChecks()
        {
            var rows = new List<UiRow>();
            var all = WcTrack.Checks;
            if (all.Count == 0) { rows.Add(new UiRow { Header = true, Label = "The check list (baseline/wc_track.json) is missing." }); return rows; }
            int done = all.Count(WcTrack.Done);
            rows.Add(new UiRow { Header = true, Label = $"{done} of {all.Count} checks done ({WcTrack.Ported} in the Pixel Remaster port so far)" });
            if (!WcTrack.Gating)
            {
                // open world: every check, by name (as WC)
                foreach (var c in all.OrderBy(c => c.Name)) rows.Add(CheckRow(c, 0));
                return rows;
            }
            // character gating: the open checks, then each found character's (as WC: the
            // checks of characters not found yet stay hidden)
            int hidden = 0;
            foreach (var gate in all.Select(c => c.Gate).Distinct().OrderBy(g => g == 0 ? "" : WcTrack.CharacterNames[g]))
            {
                var group = all.Where(c => c.Gate == gate).OrderBy(c => c.Name).ToList();
                if (gate > 0 && !WcEvents.IsRecruited(gate)) { hidden++; continue; }
                rows.Add(new UiRow { Header = true, Label = gate == 0 ? "Open" : WcTrack.CharacterNames[gate] });
                foreach (var c in group) rows.Add(CheckRow(c, 1));
            }
            if (hidden > 0) rows.Add(new UiRow { Header = true, Label = $"{hidden} more characters' checks show once you find them." });
            return rows;
        }

        static List<UiRow> TrackProgress()
        {
            var all = WcTrack.Checks;
            var rows = new List<UiRow>
            {
                new UiRow { Label = "Checks Complete", Value = $"{all.Count(WcTrack.Done)} / {all.Count}",
                            Help = $"{WcTrack.Ported} of Worlds Collide's {all.Count} checks are in the Pixel Remaster port so far." },
                new UiRow { Label = "Characters Available", Value = $"{WcTrack.CharactersFound()} / 14" },
                new UiRow { Label = "Espers Found", Value = $"{WcTrack.EspersFound()}" },
                new UiRow { Label = "Dragons Defeated", Value = $"{WcTrack.DragonsDefeated()} / 8" },
                new UiRow { Label = "Bosses Defeated", Value = WcSeeds.Active?.Bosses.Count > 0 ? $"{WcTrack.BossesDefeated()} / {WcSeeds.Active.Bosses.Count}" : "-",
                            Help = WcSeeds.Active?.Bosses.Count > 0 ? "WC's boss list; counted from this version of the port on." : "This seed was made before the port counted bosses." },
            };
            var s = WcSeeds.Active;
            if (s != null)
            {
                rows.Add(new UiRow { Header = true, Label = "Seed" });
                rows.Add(new UiRow { Label = s.SeedText, Value = s.Hash, Indent = 1, Help = $"Made {s.Created} with WC {s.WcVersion}." });
            }
            return rows;
        }

        // WC's objectives (menus/objectives.py): each one's result, how many of its conditions it
        // needs, and every condition with where it stands
        static List<UiRow> TrackObjectives()
        {
            var rows = new List<UiRow>();
            var s = WcSeeds.Active;
            if (s == null || s.Objectives.Count == 0)
            {
                rows.Add(new UiRow { Header = true, Label = s == null ? "No seed." : "No Objectives" });
                return rows;
            }
            for (int oi = 0; oi < s.Objectives.Count; oi++)
            {
                var o = s.Objectives[oi];
                int met = o.Conditions.Count(c => WcTrack.Met(c) == true);
                bool done = met >= o.Required || WcObjectives.IsComplete(oi);
                rows.Add(new UiRow { Header = true, Label = $"{o.Letter}  {o.Result}" + (done ? "  (complete)" : "") });
                rows.Add(new UiRow
                {
                    Label = $"{o.Required} of these required", Value = $"{met} met", Indent = 1, Disabled = done,
                    Help = "Results applied in the Pixel Remaster port: stat changes, learning SwdTechs/Blitzes/Lores/Dances/Spells, items, Full Heal, Fallen One, enemy/boss/dragon/final levels, auto-statuses and the final Kefka's unlock; the others not yet.",
                });
                foreach (var c in o.Conditions)
                {
                    var m = WcTrack.Met(c);
                    rows.Add(new UiRow
                    {
                        Label = c.Text, Value = WcTrack.Progress(c), Indent = 2, Disabled = m == true,
                        Help = m == null ? "The port can't tell this one yet (bosses, quests and checks not in the port are untracked)." : "",
                    });
                }
            }
            return rows;
        }

        // the seed's flags by the screen's sections (Objectives alone on its page)
        static List<UiRow> TrackFlags(bool objectives)
        {
            var rows = new List<UiRow>();
            var s = WcSeeds.Active;
            if (s == null) { rows.Add(new UiRow { Header = true, Label = "No seed." }); return rows; }
            // WC's -hf: no flags menu (menus/flags.py shows "Flags Hidden")
            if (!objectives && s.Flags.Split(' ').Contains("-hf")) { rows.Add(new UiRow { Header = true, Label = "Flags Hidden" }); return rows; }
            var v = new FlagValues(Defs);
            v.Parse(s.Flags);
            foreach (var sec in Defs.Sections)
            {
                if ((sec.Title == "Objectives") != objectives) continue;
                var lines = new List<UiRow>();
                foreach (var r in sec.Rows)
                {
                    if (r is FlagRow fr && v.IsOn(fr.Flag.Flag))
                        lines.Add(new UiRow { Label = fr.Label, Value = string.Join(" ", v.Values(fr.Flag.Flag)), Indent = 1, Help = fr.Flag.Help + $"  [{fr.Flag.Flag}]" });
                    else if (r is ChoiceRow cr && v.Selected(cr) is int sel && sel >= 0)
                    {
                        var o = cr.Options[sel];
                        var extra = string.Join(" ", v.Values(o.Flag));
                        lines.Add(new UiRow { Label = cr.Label, Value = cr.OptionLabels[sel] + (extra != "" ? " " + extra : ""), Indent = 1, Help = o.Help + $"  [{o.Flag}]" });
                    }
                }
                if (lines.Count == 0) continue;
                var port = sec.Port == "no" ? "  (not in the Pixel Remaster port yet)" : sec.Port == "partly" ? "  (partly in the port)" : "";
                rows.Add(new UiRow { Header = true, Label = sec.Title + port });
                rows.AddRange(lines);
            }
            if (rows.Count == 0) rows.Add(new UiRow { Header = true, Label = objectives ? "No Objectives" : "No flags" });
            return rows;
        }

        // headless: the Track pages build and count what they should
        internal static string TrackTest()
        {
            bool keep = Track;
            int keepPage = page;
            try
            {
                Track = true;
                var parts = new List<string>();
                for (page = 0; page < TrackPages.Length; page++)
                {
                    var rows = TrackRows();
                    parts.Add($"{TrackPages[page]}:{rows.Count}");
                }
                return string.Join(" ", parts) + $" checks={WcTrack.Checks.Count} ported={WcTrack.Ported}";
            }
            finally { Track = keep; page = keepPage; }
        }
    }
}

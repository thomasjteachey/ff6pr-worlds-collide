using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Last.Interpreter;

namespace WorldsCollide.PR
{
    // The Veldt: the PR opens a Veldt fight when its party is met elsewhere (BeastFieldEncount.
    // EncounterMonsterParty; the misc asset's beast_field_encount_table names vanilla parties).
    // A WC seed fights copies of a formation's party: one per area look (port_encounters: 2000 + f,
    // then 4000 + 600 k + f; monster boxes share those ids, port_chests.box_look) and event battles
    // on their event's background (20000+, the seed's look_sources). Each counts as its formation's
    // own party, as the SNES counts the formation; without this the user's seed opened 76 of its
    // 752 encounter slots' Veldt fights (vanilla 707) and the Veldt stayed empty.
    static class WcVeldt
    {
        internal static string Mapped = "";
        static Dictionary<int, int> formationParty;   // SNES formation -> the PR party (formation_party.json)
        static string actorKey;

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(global::Last.Map.Encount.BeastFieldEncount), "EncounterMonsterParty");
            if (m == null) { Plugin.L.LogWarning("[wc] veldt: no BeastFieldEncount.EncounterMonsterParty"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcVeldt), nameof(Encounter_Prefix)));
            var gau = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.SystemCall.Current), "SetGauMode");
            if (gau != null) h.Patch(gau, postfix: new HarmonyMethod(typeof(WcVeldt), nameof(SetGauMode_Postfix)));
            else Plugin.L.LogWarning("[wc] veldt: no Current.SetGauMode; active-Gau reward encounters are off");
            var actor = AccessTools.Method(typeof(global::Last.Interpreter.Instructions.SystemCall.Current), "ChangeEnemyPartyGau");
            if (actor != null) h.Patch(actor, prefix: new HarmonyMethod(typeof(WcVeldt), nameof(ChangeEnemyPartyGau_Prefix)));
            else Plugin.L.LogWarning("[wc] veldt: no Current.ChangeEnemyPartyGau; reward appearance stays Gau");
        }

        // the formation behind a party the port made, or -1 (no vanilla party has these ids)
        internal static int Formation(int party)
        {
            if (party >= 2000 && party < 2576) return party - 2000;
            if (party >= 4000 && party < 20000 && (party - 4000) % 600 < 576) return (party - 4000) % 600;
            return -1;
        }

        // the party the Veldt knows for a party the port or the seed made: the formation's own
        internal static int Source(int party)
        {
            var s = WcSeeds.Active;
            if (s != null && s.LookSources.TryGetValue(party, out var copied)) party = copied;
            int f = Formation(party);
            if (f < 0) return party;
            if (formationParty == null) formationParty = LoadFormations();
            return formationParty.TryGetValue(f, out var p) ? p : f + 1;   // idmap.party's default: formation + 1
        }

        static Dictionary<int, int> LoadFormations()
        {
            var map = new Dictionary<int, int>();
            foreach (var path in new[]
            {
                Path.Combine(Plugin.Folder, "generator", "tools", "formation_party.json"),
                string.IsNullOrEmpty(WcGenerator.ProjectDir) ? null : Path.Combine(WcGenerator.ProjectDir, "pr", "tools", "formation_party.json"),
            })
            {
                if (path == null || !File.Exists(path)) continue;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                        foreach (var p in doc.RootElement.EnumerateObject()) map[int.Parse(p.Name)] = p.Value.GetInt32();
                    return map;
                }
                catch (Exception e) { Plugin.L.LogWarning($"[wc] veldt: {path}: {e.Message}"); }
            }
            Plugin.L.LogWarning("[wc] veldt: no formation_party.json (formation + 1 assumed)");
            return map;
        }

        static bool Active() => Plugin.WcMode && WcSeeds.Active != null &&
                                WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.WcGameFlag) == 1;

        public static void Encounter_Prefix(ref int __0)
        {
            try
            {
                if (!Active()) return;
                int to = Source(__0);
                if (to == __0) return;
                Mapped = $"party {__0} counts as {to}";
                Plugin.L.LogInfo("[wc] veldt: " + Mapped);
                __0 = to;
            }
            catch (Exception e) { WcRunner.WarnOnce("veldt", e); }
        }

        // PR's vanilla SetGauMode refuses to queue Gau while he is in the active corps. WC's
        // Veldt check instead uses recruited/available Gau as its gate and permits the reward
        // encounter with fewer than four party members. Restore WC's original 160/256 chance.
        // SetGauMode's value 2 means "Gau appears after this battle is won"; the battle script
        // itself changes it to 3 when that post-victory scene begins. Setting 3 here starts the
        // scene at battle start, removes the enemies, and leaves later Veldt fights poisoned.
        public static void SetGauMode_Postfix()
        {
            try
            {
                if (!Active() || !WcEvents.IsRecruited(12)) return;
                var beast = global::Last.Map.Encount.BeastFieldEncount.Instance();
                if (beast == null || !beast.IsBeastField()) return;
                var party = global::Last.Management.UserDataManager.Instance()?.GetPartyMemberData();
                if (party == null || party.Count >= 4 || !party.ToArray().Any(c => c?.characterStatusId == 12)) return;
                var storage = DataStorage.Instance;
                if (storage == null) return;

                // Vanilla returns early when Gau is already in the active party, so it does not
                // initialize this mode. Normalize it on every such battle, including old saves
                // left at 3 by the previous patch.
                storage.Set(DataStorage.Category.kScriptLocalVariable, 2, 1);
                if (WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 31) == 1) return;
                if (UnityEngine.Random.Range(0, 256) >= 160) return;
                storage.Set(DataStorage.Category.kScriptLocalVariable, 2, 2);
                Plugin.L.LogInfo($"[wc] veldt: Gau reward encounter queued ({party.Count} members)");
            }
            catch (Exception e) { WcRunner.WarnOnce("veldt-gau-mode", e); }
        }

        // The post-victory script changes the enemy party to special monsters 549/550. PR's
        // animation_enemy_params renders those monsters with Gau's player prefab. WC renders a
        // character reward as that character instead; for an esper it uses one of four guest
        // sprites rather than pretending Gau is the reward. Update the same live descriptor
        // before the party is instantiated. This is idempotent because the descriptor is global.
        public static void ChangeEnemyPartyGau_Prefix()
        {
            try
            {
                if (!Active() || WcSeeds.Active?.Checks.TryGetValue("veldt", out var check) != true ||
                    check.Rewards.Count == 0) return;
                var reward = check.Rewards[0];
                int status;
                if (reward.Kind == "character") status = reward.Id;
                else if (reward.Kind == "esper")
                {
                    // SNES WC chooses sprite 14, 15, 19, or 20 for a magicite reward. The PR's
                    // corresponding guest character-status rows are 15, 16, 20, and 21. Pick
                    // stably from the seed so saves always show the same non-Gau actor.
                    int[] guests = { 15, 16, 20, 21 };
                    int hash = 17;
                    foreach (char c in WcSeeds.Active.Id ?? "veldt") hash = unchecked(hash * 31 + c);
                    status = guests[(hash & 0x7fffffff) % guests.Length];
                }
                else return;

                var row = global::Last.Data.Master.MasterManager.Instance?
                    .GetData<global::Last.Data.Master.CharacterStatus>(status);
                var entries = global::Misc.MiscAssetDescAccessor.Desc?.animation_enemy_params;
                if (row == null || entries == null) return;
                foreach (var p in entries)
                {
                    if (p == null || (p.monster_id != 549 && p.monster_id != 550)) continue;
                    p.character_status_id = status;
                    p.character_status_job_id = row.JobId;
                }
                string key = $"{reward.Kind}:{reward.Id}:{status}";
                if (actorKey != key)
                {
                    actorKey = key;
                    Plugin.L.LogInfo($"[wc] veldt: reward actor is character-status {status} for {reward.Kind} {reward.Id}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("veldt-reward-actor", e); }
        }
    }
}

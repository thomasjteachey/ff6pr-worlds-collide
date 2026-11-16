using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using Il2CppSystem.Input;
using Last.Battle;
using Last.Data.Master;
using Last.Defaine;
using Last.Defaine.Audio;
using Last.Management;
using UnityEngine;
using Object = UnityEngine.Object;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Testing hotkeys (config [Testing] TestKit = true):
    //  F8 turns the loaded game into an esper test save: every BNW esper, EL 10 with 10
    //     ELs banked and EP one short of the next EL for Terra..Gau, 20 SP banked, gil
    //     topped up to 500,000. Save to a spare slot to keep it.
    //  F9 (on the field) starts the next boss fight that runs BNW's AI, in turn, like a
    //     random encounter, so bosses can be tried without playing up to them.
    //  F10 (in battle) defeats every enemy left, so the battle ends as a normal win
    //     (rewards, EP/EL) whatever the party's level.
    // Controller (Steam Deck, no F-keys): click both sticks in (L3 + R3) together.
    //  tap in battle = F10, tap on the field = F9, hold 1.5 s on the field = F8.
    static class TestKitHotkey
    {
        internal static bool Enabled;
        internal static KeyCode Key = KeyCode.F8, BossKey = KeyCode.F9, WinKey = KeyCode.F10;
        static int bossIndex = -1;

        const float HoldSeconds = 1.5f;
        static float chordSince = -1f;
        static bool chordHeldFired;

        public static void Update()
        {
            if (!Enabled) return;
            try
            {
                if (Input.GetKeyDown(Key)) Apply();
                else if (Input.GetKeyDown(BossKey)) NextBoss();
                else if (Input.GetKeyDown(WinKey)) WinBattle();
                PadChord();
            }
            catch (Exception e) { Plugin.L.LogWarning($"[test kit] {e}"); }
        }

        static bool InBattle()
        {
            var battle = BattlePlugManager.Instance();
            return battle != null && battle.IsBattle();
        }

        // L3 + R3: through the game's own gamepad keys, or Unity's joystick buttons 8/9
        // (Xbox layout, which is what the Deck looks like to the game under Proton).
        static bool BothSticksDown()
        {
            bool game = false;
            try
            {
                var il = InputListener.Instance;
                game = il != null && il.GetKey(Il2CppSystem.Input.Key.StickL, Device.GamePad, false)
                                  && il.GetKey(Il2CppSystem.Input.Key.StickR, Device.GamePad, false);
            }
            catch (Exception e) { Hooks.WarnOnce("TestKit.Pad", e); }
            bool unity = Input.GetKey(KeyCode.JoystickButton8) && Input.GetKey(KeyCode.JoystickButton9);
            if (game || unity)
                Hooks.NoteOnce("pad-chord", () => $"[test kit] L3+R3 seen ({(game ? "game input" : "Unity joystick buttons")})");
            return game || unity;
        }

        static void PadChord()
        {
            bool down = BothSticksDown();
            float now = Time.unscaledTime;
            if (down)
            {
                if (chordSince < 0f) { chordSince = now; chordHeldFired = false; }
                else if (!chordHeldFired && now - chordSince >= HoldSeconds && !InBattle())
                {
                    chordHeldFired = true;
                    Apply();  // hold = F8
                }
                return;
            }
            if (chordSince < 0f) return;
            bool tap = !chordHeldFired;
            chordSince = -1f;
            if (!tap) return;
            if (InBattle()) WinBattle();  // tap in battle = F10
            else NextBoss();              // tap on the field = F9
        }

        // Zero each remaining enemy's HP and force KO through the battle's own condition
        // controller; the game's end-of-battle check then awards the win as usual.
        static void WinBattle()
        {
            var battle = BattlePlugManager.Instance();
            if (battle == null || !battle.IsBattle())
            {
                Plugin.L.LogInfo("[test kit] F10 only works in a battle");
                Sound(SystemSeDefine.BeepId);
                return;
            }
            var conditions = battle.BattleConditionController;
            int defeated = 0, exp = 0;
            foreach (var enemy in battle.GetEnemyUnits())
            {
                var p = enemy?.BattleUnitDataInfo?.Parameter;
                if (p == null || p.CurrentHP <= 0) continue;
                p.CurrentHP = 0;
                try
                {
                    conditions?.InterruptAddCondition(enemy, ConditionType.UnableFight);
                    // the game puts the dead monster on its reward list itself (v0.8.6 log)
                    exp += enemy.GetMonster()?.Exp ?? 0;
                }
                catch (Exception e) { Hooks.WarnOnce("TestKit.F10", e); }
                defeated++;
            }
            if (defeated > 0) BattleRewards.NoteWin(exp);
            Plugin.L.LogInfo($"[test kit] F10: defeated {defeated} enemies ({exp} EXP)");
        }

        static void NextBoss()
        {
            var tests = EsperHooks.D?.BossTests;
            if (tests == null || tests.Count == 0)
            {
                Plugin.L.LogInfo("[boss test] no bosses run BNW's AI yet");
                Sound(SystemSeDefine.BeepId);
                return;
            }
            var field = Object.FindObjectOfType<FieldMap>();
            var controller = field?.fieldController;
            if (controller == null)
            {
                Plugin.L.LogInfo("[boss test] walk around on the field first (town or world map)");
                Sound(SystemSeDefine.BeepId);
                return;
            }
            bossIndex = (bossIndex + 1) % tests.Count;
            var t = tests[bossIndex];
            if (t.Parties.Length == 0) return;
            var party = MasterManager.Instance.GetData<MonsterParty>(t.Parties[0]);
            if (party == null)
            {
                Plugin.L.LogWarning($"[boss test] formation {t.Parties[0]} not found");
                return;
            }
            var name = string.IsNullOrEmpty(t.Name) ? $"SNES enemy {t.SnesId}" : t.Name;
            Plugin.L.LogInfo($"[boss test] {bossIndex + 1}/{tests.Count}: {name} (formation {t.Parties[0]})" +
                             (t.Hybrid ? " - hybrid: BNW's AI, the PR's own routine brings monsters in and out" : ""));
            controller.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
        }

        static void Apply()
        {
            var D = EsperHooks.D;
            var udm = UserDataManager.Instance();
            var chars = udm?.OwnedCharacterList;
            if (D == null || chars == null || chars.Count == 0)
            {
                Plugin.L.LogInfo("[test kit] load a save first");
                Sound(SystemSeDefine.BeepId);
                return;
            }

            var items = new OwnedItemClient();
            int espers = 0;
            foreach (var e in TestKit.Espers(D))
            {
                if (udm.OwnedMagicStoneList.Contains(e.ContentId)) continue;
                items.AddMagicStone(e.ContentId);
                if (!udm.OwnedMagicStoneList.Contains(e.ContentId)) udm.OwnedMagicStoneList.Add(e.ContentId);
                espers++;
            }

            var done = new List<string>();
            var present = new HashSet<int>();
            foreach (var c in chars)
            {
                if (c == null) continue;
                int sid = c.characterStatusId;
                if (sid < 1 || sid > D.Rules.MaxElCharacterStatusId || !present.Add(sid)) continue;
                EsperStore.Put(sid, TestKit.Progress(D, EsperStore.Get(sid), sid));
                done.Add(c.Name);
            }
            var missing = new List<int>();
            for (int sid = 1; sid <= D.Rules.MaxElCharacterStatusId; sid++)
                if (!present.Contains(sid)) missing.Add(sid);

            int gil = udm.OwendGil;
            if (gil < TestKit.Gil) items.AddGil(TestKit.Gil - gil);

            EsperStore.Invalidate();
            Sound(SystemSeDefine.ShopAccounting);
            Plugin.L.LogInfo($"[test kit] added {espers} espers; EL {TestKit.El} / {TestKit.ElBank} to spend / next EL one EP away for " +
                             $"{string.Join(", ", done)}; gil {gil} -> {udm.OwendGil}" +
                             (missing.Count > 0 ? $"; not in this save yet (character_status ids): {string.Join(", ", missing)}" : ""));
        }

        static void Sound(int id)
        {
            try { AudioManager.Instance?.PlaySe(id, 0f, 0f, 0f); }
            catch (Exception e) { Hooks.WarnOnce("TestKit.Sound", e); }
        }
    }
}

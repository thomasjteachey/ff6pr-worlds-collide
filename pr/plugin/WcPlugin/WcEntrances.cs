using System;
using HarmonyLib;
using Last.Interpreter;
using Last.Management;
using Last.Map;

namespace WorldsCollide.PR
{
    // World map entrances WC turns into a check's scene. WC replaces some world map exits with
    // events: Doma in the World of Balance (WC event/doma_wob.py) loads the siege map at the castle
    // gate (SNES map 0x78, 33,42) without fading in when Cyan's gate is met and the check isn't done,
    // and the siege starts there. The PR's Doma entrance (two world map exits at 156/157,85: map 75
    // point 1, no condition) showed the castle's entrance first; the map's script then switched to
    // the siege scene (wc_events doma_siege_start): the user's Deck, 2026-11-11, "doma siege ports me
    // halfway up the map. it also flashes at the very beginning" (headless: the entrance on screen
    // 0.65 s, then the gate). The exit now makes the map change vanilla's ChangeMap makes into the siege
    // (map 75 point 2, the siege scene reserved for the arrival). (Tried first: only the exit's point and
    // script name changed: the gate loaded with no scene; a castle script run from the world map: the
    // PR only runs the current map's scripts.) The map script's hook stays for any other way in.
    static class WcEntrances
    {
        const int DomaMap = 75, DomaEntrance = 1, SiegePoint = 2, SiegeFlag = 72, DomaCheck = 57;   // wc_events CHECK_FLAGS["doma_wob"]
        const string SiegeScene = "sc_e_0099_3";
        internal static string Last = "";

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(EventProcedure), "EventGotoMap");
            if (m == null) throw new MissingMethodException("EventProcedure.EventGotoMap");
            h.Patch(m, prefix: new HarmonyMethod(typeof(WcEntrances), nameof(EventGotoMap_Prefix)));
            Plugin.L.LogInfo("[wc] entrances: world map exits hooked (Doma's siege)");
        }

        static int Flag(DataStorage.Category c, int i) => DataStorage.Instance?.Get(c, i) ?? -1;

        // Doma's world map exit (map 75 point 1) in a WC game with the siege due: the map change the
        // interpreter's ChangeMap makes for vanilla's way in (sc_e_0099_2: ChangeMap [75, 2, 0, 0, 1, 0]
        // "sc_e_0099_3"; decompiled Misc.ChangeMap: the script reserved for the arrival, NextMapData(map,
        // point), EventGotoMap(skipFade 0, skipBgm 1))
        public static void EventGotoMap_Prefix(LoadData __0, ref bool __2)
        {
            try
            {
                if (!Plugin.WcMode || WcSeeds.Active == null || __0 == null) return;
                if (__0.MapId != DomaMap || __0.Point != DomaEntrance) return;
                int from = UserDataManager.Instance()?.CurrentMapId ?? -1;
                if (global::Last.Data.Master.MasterManager.Instance?.GetData<global::Last.Data.Master.Map>(from)?.EncountAreaGridId != 1) return;   // from the World of Balance map
                if (Flag(DataStorage.Category.kScenarioFlag4, 0) != 1) return;                     // a WC game
                if (Flag(DataStorage.Category.kScenarioFlag4, DomaCheck) == 1) return;             // done
                if (Flag(DataStorage.Category.kScenarioFlag1, SiegeFlag) == 1) return;             // the siege already on
                if (Flag(DataStorage.Category.kScenarioFlag2, WcEvents.CollapseFlag) == 1) return; // the World of Ruin
                if (!WcEvents.GateOpen("doma_wob")) return;
                var integ = Integrator.Instance;
                if (integ == null) return;
                integ.ReserveScript(SiegeScene);
                __0.NextMapData(DomaMap, SiegePoint, (ViewType)0);
                __2 = true;   // the siege keeps its own music (skipBgm, as vanilla's ChangeMap)
                Last = $"Doma from map {from}: map {DomaMap} point {SiegePoint}, {SiegeScene} on arrival";
                Plugin.L.LogInfo("[wc] entrances: " + Last);
            }
            catch (Exception e) { WcRunner.WarnOnce("entrances", e); }
        }
    }
}

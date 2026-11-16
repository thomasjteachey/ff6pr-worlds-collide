using System;
using Last.Data.Master;
using Last.Management;

namespace PrKit
{
    // The game's master data (the CSV tables Magicite mods change: monster, ability, item...),
    // as loaded, mod rows included. Read it after Master.Ready; tables are Dictionary<id, row>.
    //   var potion = Master.Get<Item>(1);       var all = Master.All<Monster>();
    // Changing a row in memory changes the game until it reloads master data (LiveData does that
    // for whole Magicite folders). Harmony can't patch MasterManager's generic methods.
    public static class Master
    {
        // The tables are there, not only the manager: at startup isInitialized was true while
        // GetList<T>() still returned nothing (WC's seeds applied to empty tables, 2026-11).
        public static bool Ready
        {
            get
            {
                var mm = MasterManager.Instance;
                if (mm == null || !mm.isInitialized || MessageManager.Instance == null) return false;
                try
                {
                    var monsters = mm.GetList<Monster>();
                    var abilities = mm.GetList<Ability>();
                    return monsters != null && monsters.Count > 0 && abilities != null && abilities.Count > 0;
                }
                catch (Exception) { return false; }
            }
        }

        public static T Get<T>(int id) where T : MasterBase
        {
            var mm = MasterManager.Instance;
            if (mm == null || !mm.isInitialized) return null;
            try { return mm.GetData<T>(id); }
            catch (Exception) { return null; }
        }

        public static Il2CppSystem.Collections.Generic.Dictionary<int, T> All<T>() where T : MasterBase
        {
            var mm = MasterManager.Instance;
            if (mm == null || !mm.isInitialized) return null;
            try { return mm.GetList<T>(); }
            catch (Exception) { return null; }
        }
    }

    // The game's text by message key (system_en.txt and the other message files, mods' included).
    // (Not "Text": that would collide with UnityEngine.UI.Text in any file using both.)
    public static class GameText
    {
        public static string Get(string key)
        {
            try { return MessageManager.Instance?.GetMessage(key, false); }
            catch (Exception) { return null; }
        }

        public static bool Has(string key)
        {
            try { return MessageManager.Instance?.messageDictionary?.ContainsKey(key) ?? false; }
            catch (Exception) { return false; }
        }

        // Sets (or adds) a text for this session; returns false before the text has loaded.
        public static bool Set(string key, string value)
        {
            var msg = MessageManager.Instance;
            if (msg?.messageDictionary == null) return false;
            if (msg.messageDictionary.ContainsKey(key)) msg.messageDictionary[key] = value;
            else msg.AddMessage(key, value);
            return true;
        }
    }
}

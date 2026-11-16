using System.Collections.Generic;
using BraveNewWorld.Rules;
using Last.Management;

namespace BraveNewWorld.PR
{
    // Keeps each character's EP / EL / banks in the save file. The PR games share one
    // save format; FF6 never uses FF2's keyword list (UserDataManager.OwnedKeyWaordList,
    // a List<int> saved as "ownedKeyWaordList"), so our block lives there:
    //
    //   marker "BNW1", then three packed words per character_status id 1..14
    //
    // Anything else in the list is left untouched. (The scenario-variable array would
    // have been the obvious place, but it only has 16 slots.)
    static class EsperStore
    {
        const int Marker = 0x424E5731;
        const int Characters = 14;
        const int Words = 3;
        const int BlockSize = 1 + Words * Characters;

        // The game computes stats on more than one thread (seen on the Steam Deck: a plain
        // Dictionary got corrupted), so everything here goes through one lock.
        static readonly object Sync = new object();
        static readonly Dictionary<int, EsperProgress> cache = new Dictionary<int, EsperProgress>();
        static string lastState = "";
        public static string State { get; private set; } = "not checked";

        public static void Invalidate()
        {
            lock (Sync) cache.Clear();
        }

        static Il2CppSystem.Collections.Generic.List<int> List()
        {
            var list = UserDataManager.Instance()?.OwnedKeyWaordList;
            SetState(list == null ? "no save data" : "ok");
            return list;
        }

        // Index of our marker, or -1.
        static int Find(Il2CppSystem.Collections.Generic.List<int> list)
        {
            int n = list.Count;
            for (int i = 0; i + BlockSize <= n; i++)
                if (list[i] == Marker) return i;
            return -1;
        }

        static void SetState(string s)
        {
            State = s;
            if (s != lastState) { lastState = s; Plugin.L.LogInfo($"[esper store] {s}"); }
        }

        public static bool Available
        {
            get { lock (Sync) return List() != null; }
        }

        public static EsperProgress Get(int statusId)
        {
            if (statusId < 1 || statusId > Characters) return EsperProgress.Empty;
            lock (Sync) return GetLocked(statusId);
        }

        static EsperProgress GetLocked(int statusId)
        {
            if (cache.TryGetValue(statusId, out var p)) return p.Clone();
            p = EsperProgress.Empty;
            var list = List();
            if (list != null)
            {
                int at = Find(list);
                if (at >= 0)
                {
                    int o = at + 1 + Words * (statusId - 1);
                    p = EsperProgress.Unpack(list[o], list[o + 1], list[o + 2]);
                }
            }
            cache[statusId] = p;
            return p.Clone();
        }

        public static bool Put(int statusId, EsperProgress p)
        {
            if (statusId < 1 || statusId > Characters) return false;
            lock (Sync) return PutLocked(statusId, p);
        }

        static bool PutLocked(int statusId, EsperProgress p)
        {
            var list = List();
            if (list == null) return false;
            int at = Find(list);
            if (at < 0)
            {
                at = list.Count;
                list.Add(Marker);
                for (int i = 1; i < BlockSize; i++) list.Add(0);
                Plugin.L.LogInfo("[esper store] started BNW esper data in this save");
            }
            var w = p.Pack();
            int o = at + 1 + Words * (statusId - 1);
            for (int i = 0; i < Words; i++) list[o + i] = w[i];
            cache[statusId] = p.Clone();
            return true;
        }
    }
}

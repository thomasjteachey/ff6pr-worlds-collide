using System;
using HarmonyLib;
using Last.Interpreter;
using Last.Interpreter.Instructions;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // SysCall "BNW:<name>" lines in the mod's own scripts (tools/port_merchants.py):
    //   BNW:shop <group>   a shop for that product group, through the game's own opener
    //                      SystemCall.Current.OpenShopCommon(group, isFirstClock) (decompiled
    //                      tools/decomp/shop): it opens the shop on the call's first frame and
    //                      returns 1 while the shop is open (the script engine repeats the call),
    //                      0 once it's closed, as every vanilla "ショップを開く:..." call does.
    // A prefix on the SysCall instruction takes these names and leaves every other call to the
    // game (the WC plugin does the same for its "WC:" names).
    static class ShopCalls
    {
        internal static int Opened;   // tests: shops this session opened

        public static void Apply(Harmony h)
        {
            var m = AccessTools.Method(typeof(External.Misc), "SystemCall");
            if (m == null) { Plugin.L.LogWarning("[shop] no External.Misc.SystemCall: the mod's merchants are off"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(ShopCalls), nameof(SystemCall_Prefix)));
        }

        public static bool SystemCall_Prefix(MainCore mc, ref int __result)
        {
            string name;
            try
            {
                var sv = mc?.op?.sValues;
                name = sv != null && sv.Length > 0 ? sv[0] : null;
            }
            catch { return true; }
            if (name == null || !name.StartsWith("BNW:")) return true;
            __result = 0;
            try
            {
                var parts = name.Substring(4).Split(' ');
                if (parts.Length == 2 && parts[0] == "shop" && int.TryParse(parts[1], out int group))
                {
                    bool first = mc.IsFirstClock();
                    __result = global::Last.Interpreter.Instructions.SystemCall.Current.OpenShopCommon(group, first);
                    if (first)
                    {
                        Opened++;
                        Hooks.NoteOnce("shop-" + group, () => $"[shop] the mod's shop {group} opened");
                    }
                }
                else Plugin.L.LogWarning($"[shop] unknown call {name}");
            }
            catch (Exception e) { __result = 0; Hooks.WarnOnce("ShopCalls", e); }
            return false;
        }
    }
}

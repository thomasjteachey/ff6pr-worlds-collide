using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Data.User;
using Last.Defaine.Audio;
using Serial.FF6.UI;
using UnityEngine;
using DetailsController = Serial.FF6.UI.KeyInput.MagicStoneDetailsController;
using ListController = Serial.FF6.UI.KeyInput.MagicStoneListController;
using ListContent = Serial.FF6.UI.KeyInput.MagicStoneListContentController;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Esper list (keyboard/gamepad UI): espers the character can't use are greyed out, as
    // in BNW, and trying to equip one plays the buzzer.
    static class EsperList
    {
        static OwnedCharacterData listTarget;
        static readonly HashSet<IntPtr> greyed = new HashSet<IntPtr>();
        static readonly Color32 Grey = new Color32(140, 140, 150, 255);

        static BnwData D => EsperHooks.D;

        public static void Apply(Harmony h)
        {
            var l = typeof(ListController);
            Hooks.Patch(h, l, "Show", typeof(EsperList), prefix: nameof(ListShow_Prefix));
            Hooks.Patch(h, l, "ListUpdate", typeof(EsperList), prefix: nameof(ListShow_Prefix),
                args: new[] { typeof(OwnedCharacterData), typeof(Il2CppSystem.Collections.Generic.List<OwnedCharacterData>),
                              typeof(Il2CppSystem.Collections.Generic.List<int>), typeof(Il2CppSystem.Collections.Generic.List<int>) });
            Hooks.Patch(h, l, "ContentUpdate", typeof(EsperList), postfix: nameof(ContentUpdate_Postfix));
            Hooks.Patch(h, typeof(ListContent), "SetFocus", typeof(EsperList), postfix: nameof(ContentFocus_Postfix));
            Hooks.Patch(h, typeof(DetailsController), "EquipMagicStone", typeof(EsperList), prefix: nameof(Equip_Prefix),
                args: new[] { typeof(OwnedCharacterData), typeof(Il2CppSystem.Collections.Generic.List<OwnedCharacterData>), typeof(bool) });
        }

        public static void ListShow_Prefix(ListController __instance, OwnedCharacterData target)
        {
            if (Hooks.IsA<ListController>(__instance)) listTarget = target;
        }

        public static void ContentUpdate_Postfix(ListController __instance, MagicStoneListData data, GameObject gameObject)
        {
            if (!EsperHooks.Restrict || data == null || gameObject == null || !Hooks.IsA<ListController>(__instance)) return;
            try
            {
                var content = gameObject.GetComponent<ListContent>() ?? gameObject.GetComponentInChildren<ListContent>(true);
                var name = content?.view?.ItemNameView;
                if (name == null) return;
                var target = listTarget ?? __instance.targetCharacter;
                bool grey = data.IsOwned && target != null && !EsperRules.CanEquip(D, target.characterStatusId, data.ContentId);
                if (grey)
                {
                    greyed.Add(content.Pointer);
                    name.SetTextColor(Grey);
                }
                else if (greyed.Remove(content.Pointer))
                {
                    name.SetFocusColor(false); // back to the game's normal colour
                }
            }
            catch (Exception e) { Hooks.WarnOnce("EsperList.ContentUpdate", e); }
        }

        // Focus changes recolour the name; keep greyed entries grey.
        public static void ContentFocus_Postfix(ListContent __instance)
        {
            try
            {
                if (__instance != null && greyed.Contains(__instance.Pointer) && Hooks.IsA<ListContent>(__instance))
                    __instance.view?.ItemNameView?.SetTextColor(Grey);
            }
            catch (Exception e) { Hooks.WarnOnce("EsperList.ContentFocus", e); }
        }

        // Equipping an esper this character can't use: buzzer, stay on the screen.
        public static bool Equip_Prefix(DetailsController __instance, OwnedCharacterData target)
        {
            if (!EsperHooks.Restrict || !Hooks.IsA<DetailsController>(__instance)) return true;
            try
            {
                var who = target ?? __instance.targetCharacter;
                var stone = __instance.targetmagicStone;
                if (who == null || stone == null || EsperRules.CanEquip(D, who.characterStatusId, stone.ContentId)) return true;
                EsperMenu.Sound(SystemSeDefine.BeepId);
                return false;
            }
            catch (Exception e) { Hooks.WarnOnce("EsperList.Equip", e); return true; }
        }
    }
}

using System;
using BraveNewWorld.Rules;
using HarmonyLib;
using Last.Data.User;
using Last.Management;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using PanelController = Serial.FF6.UI.KeyInput.StatusWindowContentController;
using HeaderController = Serial.FF6.UI.KeyInput.AbilityCharaStatusController;
using StatusController = Serial.FF6.UI.KeyInput.StatusDetailsController;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // Where BNW shows Esper Levels outside the esper screen (keyboard/gamepad UI):
    //  * "EL 10" right after the level ("LV 35  EL 10") in the main-menu party panels and
    //    the character header of the ability/status screens. The two texts are copies of
    //    the game's own level text (so font and outline match), kept as children of it so
    //    no layout group moves anything else;
    //  * on the status screen the "Level Up Bonus" row (meaningless in BNW, which replaced
    //    level-up bonuses with ELs) becomes "Next EL" with the EP still needed (0 at max).
    // Both only once the party owns an esper, and never for Gogo, Umaro or guests (as in BNW).
    static class EsperLevelDisplay
    {
        const string ContainerName = "BnwEl";
        static string originalBonusLabel;

        static BnwData D => EsperHooks.D;

        public static void Apply(Harmony h)
        {
            Hooks.Patch(h, typeof(PanelController), "SetData", typeof(EsperLevelDisplay), postfix: nameof(Panel_Postfix));
            Hooks.Patch(h, typeof(HeaderController), "UpdateView", typeof(EsperLevelDisplay), postfix: nameof(Header_Postfix));
            // Not StatusDetailsController.UpdateView: it is tiny and shares its machine code with
            // other methods, so a hook on it ran for unrelated objects and crashed the game.
            Hooks.Patch(h, typeof(StatusController), "SetParameter", typeof(EsperLevelDisplay), postfix: nameof(Status_Postfix));
            Hooks.Patch(h, typeof(StatusController), "SetMagicStoneBouns", typeof(EsperLevelDisplay), postfix: nameof(Status_Postfix));
        }

        static bool Shows(OwnedCharacterData c)
        {
            if (!EsperHooks.Levels || c == null) return false;
            var owned = UserDataManager.Instance()?.OwnedMagicStoneList;
            return EsperRules.ShowsEl(D, c.characterStatusId, owned != null && owned.Count > 0);
        }

        // --- EL beside LV -----------------------------------------------------------------

        public static void Panel_Postfix(PanelController __instance, OwnedCharacterData data)
        {
            if (!Hooks.IsA<PanelController>(__instance)) return;
            var v = __instance.view;
            if (v != null) ShowEl(v.fixedLevelText, v.CurrentLevelText, data, "party panel");
        }

        public static void Header_Postfix(HeaderController __instance, OwnedCharacterData data)
        {
            if (!Hooks.IsA<HeaderController>(__instance)) return;
            var v = __instance.view;
            if (v != null) ShowEl(v.fixedLevelText, v.CurrentLevelText, data, "header");
        }

        static void ShowEl(Text levelLabel, Text level, OwnedCharacterData c, string where)
        {
            try
            {
                if (level == null) return;
                var existing = level.transform.Find(ContainerName);
                if (!Shows(c) || !level.gameObject.activeInHierarchy)
                {
                    if (existing != null) existing.gameObject.SetActive(false);
                    return;
                }
                var container = existing != null ? existing.gameObject : Build(levelLabel, level);
                var label = container.transform.Find("Label").GetComponent<Text>();
                var value = container.transform.Find("Value").GetComponent<Text>();
                value.text = EsperStore.Get(c.characterStatusId).El.ToString();
                Layout(container, level, label, value);
                container.SetActive(true);
                Hooks.NoteOnce("el-" + where, () => $"[esper levels] EL beside LV in the {where}: " +
                    $"LV label {Span(levelLabel)} level {Span(level)} ('{level.text}', {level.alignment}) EL {Span(label)}..{Span(value)}");
            }
            catch (Exception e) { Hooks.WarnOnce("EsperLevels." + where, e); }
        }

        // Two copies of the level text: "EL" styled like the "LV" label, and the number.
        static GameObject Build(Text levelLabel, Text level)
        {
            var labelGo = Object.Instantiate(level.gameObject);
            var valueGo = Object.Instantiate(level.gameObject);
            var container = new GameObject(ContainerName);
            container.AddComponent<RectTransform>();
            container.transform.SetParent(level.transform, false);
            labelGo.name = "Label";
            valueGo.name = "Value";
            labelGo.transform.SetParent(container.transform, false);
            valueGo.transform.SetParent(container.transform, false);
            var label = labelGo.GetComponent<Text>();
            label.text = "EL";
            if (levelLabel != null)
            {
                label.color = levelLabel.color;
                label.font = levelLabel.font;
                label.fontSize = levelLabel.fontSize;
            }
            foreach (var t in new[] { label, valueGo.GetComponent<Text>() })
            {
                t.alignment = TextAnchor.MiddleLeft;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.raycastTarget = false;
            }
            return container;
        }

        // Just right of where the level number ends, in the level text's own space.
        static void Layout(GameObject container, Text level, Text label, Text value)
        {
            var r = level.rectTransform.rect;
            float width = level.preferredWidth;
            var a = level.alignment;
            float end = a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight ? r.xMax
                : a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter ? r.center.x + width / 2
                : r.xMin + width;
            float gap = level.fontSize * 0.8f;
            float labelWidth = label.preferredWidth, valueWidth = value.preferredWidth;

            var rt = container.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(end - r.xMin + gap, 0f);
            rt.sizeDelta = new Vector2(labelWidth + gap / 2 + valueWidth, r.height);
            Place(label.rectTransform, 0f, labelWidth, r.height);
            Place(value.rectTransform, labelWidth + gap / 2, valueWidth, r.height);
        }

        static void Place(RectTransform t, float x, float width, float height)
        {
            t.anchorMin = t.anchorMax = new Vector2(0f, 0.5f);
            t.pivot = new Vector2(0f, 0.5f);
            t.anchoredPosition = new Vector2(x, 0f);
            t.sizeDelta = new Vector2(width + 2f, height);
        }

        static string Span(Text t)
        {
            if (t == null) return "-";
            var c = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            t.rectTransform.GetWorldCorners(c);
            return $"x {c[0].x:0.00}..{c[2].x:0.00} y {c[0].y:0.00}..{c[1].y:0.00}";
        }

        // --- Next EL on the status screen --------------------------------------------------

        public static void Status_Postfix(StatusController __instance)
        {
            if (!EsperHooks.Levels || !Hooks.IsA<StatusController>(__instance)) return;
            try
            {
                var v = __instance.view;
                var c = __instance.statusController?.targetData;
                var name = v?.FixedLevelUpBonusText;
                var value = v?.LevelUpBonusText;
                if (name == null || value == null || c == null) return;
                if (name.text != "Next EL" && name.text.Length > 0) originalBonusLabel = name.text;
                Hooks.NoteOnce("status-texts", () => $"[esper levels] status screen: exp '{v.FixedExpText?.text}' '{v.ExpText?.text}' " +
                    $"next '{v.FixedNextExpText?.text}' '{v.NextExpText?.text}' ({v.NextExpText?.alignment}) magicite '{v.MagicalStoneText?.text}' " +
                    $"bonus row '{originalBonusLabel}' '{value.text}' ({value.alignment}) shown {v.MagicStoneBase?.activeInHierarchy}");
                if (!Shows(c))
                {
                    name.text = "";   // BNW blanks it for Gogo and up
                    value.text = "";
                    return;
                }
                name.text = "Next EL";
                value.text = EsperRules.EpToNextEl(D, EsperStore.Get(c.characterStatusId)).ToString("N0");
            }
            catch (Exception e) { Hooks.WarnOnce("EsperLevels.Status", e); }
        }
    }
}

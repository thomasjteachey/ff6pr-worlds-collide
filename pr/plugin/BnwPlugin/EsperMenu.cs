using System;
using System.Collections.Generic;
using System.Reflection;
using BraveNewWorld.Rules;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Input;
using Last.Data.User;
using Last.Defaine.Audio;
using Last.Management;
using Serial.FF6.UI;
using UnityEngine;
using UnityEngine.UI;
using DetailsController = Serial.FF6.UI.KeyInput.MagicStoneDetailsController;
using DetailsView = Serial.FF6.UI.KeyInput.MagicStoneDetailsView;
using Row = Serial.FF6.UI.KeyInput.MagicStoneDetailsContentController;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;
using KeyHelpController = Last.UI.KeyInput.KeyHelpController;
using KeyIconController = Last.UI.KeyInput.KeyIconController;
using Object = UnityEngine.Object;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's esper details screen (keyboard/gamepad UI), laid out as SNES BNW's: three kinds
    // of cursor stop, each with its own Confirm (c3.asm Pressed_A):
    //  * the esper's name, where the screen opens: equip/remove (the game's own Confirm);
    //  * a spell row: learn it now with banked SP (SpellBank), or buzz;
    //  * the bonus row under the spells ("Unspent EL: 2", gold while there are ELs to
    //    spend): spend one banked EL on this esper's bonus (shop "cha-ching"), or buzz.
    // Up/Down move between them and the description box says what Confirm will do.
    // Everything goes through the game's own keys, so it follows the player's bindings on
    // keyboard and gamepad; keys a stop takes are hidden from the screen's own handling
    // (its Confirm would otherwise equip the esper from any row).
    // Optional: config SpendKey names an extra game key that spends an EL from anywhere.
    static class EsperMenu
    {
        internal static string SpendKeyName = "";
        static Key spendKey;
        static bool spendKeyResolved;

        static int activeFrame = -10;
        static bool querying, inWait;
        static readonly HashSet<string> blocked = new HashSet<string>();
        static readonly HashSet<string> polled = new HashSet<string>();
        static int waitFrames;

        enum Stop { Spells, Name, Bonus }
        static Stop stop = Stop.Spells;
        static int stopIndex, enterFrame;  // the game's cursor index while we hold the hand
        static int shownStone = -1, shownTarget = -1;

        static Color labelColor;
        static bool labelColorKnown;
        static string savedDescription, hintText, originalLabel, nameHint, nameSavedDescription;
        static GameObject anchor;
        // EL status in gold while there are ELs to spend (the label is normally the cyan
        // of the screen's headings)
        static readonly Color Gold = new Color(1f, 0.82f, 0.30f, 1f);

        static BnwData D => EsperHooks.D;

        public static void Apply(Harmony h)
        {
            var t = typeof(DetailsController);
            Hooks.Patch(h, t, "UpdateView", typeof(EsperMenu), postfix: nameof(UpdateView_Postfix));
            Hooks.Patch(h, t, "UpdateStoneDetailsView", typeof(EsperMenu), postfix: nameof(UpdateStoneDetailsView_Postfix));
            Hooks.Patch(h, t, "WaitInputUpdate", typeof(EsperMenu), prefix: nameof(Wait_Prefix), postfix: nameof(Wait_Postfix));
            var il = typeof(InputListener);
            Hooks.Patch(h, il, "GetKeyDown", typeof(EsperMenu), postfix: nameof(Key_Postfix));
            Hooks.Patch(h, il, "GetKey", typeof(EsperMenu), postfix: nameof(Key_Postfix)); // held keys (cursor repeat)
            Hooks.Patch(h, typeof(Serial.FF6.UI.KeyInput.AbilityWindowController), "SetMagicStoneDetailsKeyHelp",
                typeof(EsperMenu), postfix: nameof(KeyHelp_Postfix));
        }

        // --- the button bar: Confirm says what it does on each stop ------------------------

        const string EquipLabel = "MSG_SYSTEM_135", RemoveLabel = "MSG_SYSTEM_185";
        const string LearnLabel = "MSG_BNW_KEY_LEARN", SpendLabel = "MSG_BNW_KEY_SPEND_EL";
        static KeyIconController confirmIcon;
        static string confirmOriginal;

        // The game rebuilt the bar (screen opened, esper equipped/removed): find its button again.
        public static void KeyHelp_Postfix() => confirmIcon = null;

        static KeyIconController ConfirmIcon()
        {
            if (confirmIcon != null && !confirmIcon.WasCollected && confirmIcon.gameObject.activeInHierarchy) return confirmIcon;
            confirmIcon = null;
            foreach (var bar in Object.FindObjectsOfType<KeyHelpController>())
            {
                if (bar == null || !bar.gameObject.activeInHierarchy || bar.contentList == null) continue;
                foreach (var icon in bar.contentList)
                {
                    var id = icon?.MessageId;
                    if (id != EquipLabel && id != RemoveLabel || !icon.gameObject.activeInHierarchy) continue;
                    confirmIcon = icon;
                    confirmOriginal = id;
                    return icon;
                }
            }
            return null;
        }

        static void UpdateHelp(DetailsController c)
        {
            try
            {
                var icon = ConfirmIcon();
                if (icon == null) return;
                string want = stop == Stop.Bonus ? SpendLabel : stop == Stop.Spells && SpellBank.Active(c) ? LearnLabel : confirmOriginal;
                if (want == null || icon.MessageId == want) return;
                icon.UpdateView(want, icon.messageId2, icon.keys, false);
                Hooks.NoteOnce("key-help", () => $"[esper menu] button bar: Confirm now reads {want}");
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.KeyHelp", e); }
        }

        // --- input --------------------------------------------------------------------

        static bool Pressed(Key key)
        {
            var il = InputListener.Instance;
            if (key == null || il == null) return false;
            querying = true;
            try { return il.GetKeyDown(key, Device.KeyBoard, false, false) || il.GetKeyDown(key, Device.GamePad, false, false); }
            finally { querying = false; }
        }

        static Key SpendKey
        {
            get
            {
                if (spendKeyResolved) return spendKey;
                spendKeyResolved = true;
                if (string.IsNullOrWhiteSpace(SpendKeyName) || SpendKeyName.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
                spendKey = typeof(Key).GetProperty(SpendKeyName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Key;
                if (spendKey == null) Plugin.L.LogWarning($"[esper menu] unknown SpendKey '{SpendKeyName}'");
                return spendKey;
            }
        }

        // Runs before the screen reads its input: decide what our stops take this frame.
        public static void Wait_Prefix(DetailsController __instance)
        {
            if (!Hooks.IsA<DetailsController>(__instance)) return;
            inWait = true;
            blocked.Clear();
            if (!EsperHooks.Levels) return;
            try
            {
                var c = __instance;
                var cursor = c.selectCursor;
                if (cursor == null) return;
                int rows = RowCount(c.view);
                if (Time.frameCount - activeFrame > 2)
                    EnterName(c);  // screen opened, or back from a popup or the magicite list
                else if (stop != Stop.Spells && cursor.Index != stopIndex)
                    Drop(c);       // the game moved its own cursor (mouse)
                switch (stop)
                {
                    case Stop.Name:
                        MoveAnchor(c);
                        blocked.Add("Up");
                        blocked.Add("Down");
                        if (Pressed(Key.Down))
                        {
                            if (rows > 0) GoToRow(c, 0);
                            else Enter(c);
                        }
                        break;  // Confirm, Cancel, Left/Right, Shortcut stay the game's
                    case Stop.Spells:
                        if (Pressed(Key.Up) && cursor.Index <= 0)
                        {
                            blocked.Add("Up");
                            EnterName(c);
                        }
                        else if (Pressed(Key.Down) && (rows == 0 || cursor.Index >= rows - 1))
                        {
                            blocked.Add("Down");
                            Enter(c);
                        }
                        else if (SpellBank.Active(c))
                        {
                            blocked.Add("Action");
                            if (Pressed(Key.Action)) SpellBank.Learn(c, cursor.Index);
                        }
                        break;
                    case Stop.Bonus:
                        MoveAnchor(c);
                        if (Time.frameCount - enterFrame == 10)
                            Hooks.NoteOnce("cursor-follow", () =>
                                $"[esper menu] on the EL row: hand at {cursor.transform.position}, target {anchor?.transform.position} " +
                                (Vector3.Distance(cursor.transform.position, anchor.transform.position) < 0.05f ? "(following)" : "(NOT following)"));
                        blocked.Add("Down");
                        blocked.Add("Up");
                        blocked.Add("Action");
                        if (Pressed(Key.Action)) Spend(c);
                        else if (Pressed(Key.Up))
                        {
                            if (rows > 0) Leave(c, restoreCursor: true);
                            else EnterName(c);
                        }
                        break;
                }
                var extra = SpendKey;
                if (extra != null)
                {
                    blocked.Add(extra.Name);
                    if (Pressed(extra)) Spend(c);
                }
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.Wait", e); }
        }

        public static void Wait_Postfix(DetailsController __instance)
        {
            if (!Hooks.IsA<DetailsController>(__instance)) return;
            inWait = false;
            activeFrame = Time.frameCount;
            if (++waitFrames == 600) Plugin.L.LogInfo($"[esper menu] keys the details screen polls: {string.Join(", ", polled)}");
            if (!EsperHooks.Levels) return;
            try
            {
                var c = __instance;
                // keep each stop's description current (the game rewrites it as it redraws)
                if (stop == Stop.Spells) SpellBank.ApplyHint(c, c.selectCursor?.Index ?? -1);
                else if (stop == Stop.Name) ShowNameHint(c);
                if (waitFrames % 10 == 0) SpellBank.Decorate(c);
                if (waitFrames % 5 == 0) UpdateHelp(c);
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.WaitPost", e); }
        }

        // Hides the keys our stops took from the screen's own input handling.
        public static void Key_Postfix(InputListener __instance, Key key, ref bool __result)
        {
            if (querying || !inWait || key == null || !Hooks.IsA<InputListener>(__instance)) return;
            try
            {
                if (waitFrames < 600) polled.Add(key.Name);
                if (__result && blocked.Count > 0 && blocked.Contains(key.Name)) __result = false;
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.Key", e); }
        }

        // --- stops ------------------------------------------------------------------------

        internal static List<Row> Rows(DetailsView v)
        {
            var rows = new List<Row>();
            var list = v?.Contents;
            if (list == null) return rows;
            foreach (var r in list)
                if (r != null && r.gameObject.activeInHierarchy && r.AbilityData != null) rows.Add(r);
            return rows;
        }

        static int RowCount(DetailsView v) => Rows(v).Count;

        // The esper's name: BNW's equip stop, where the screen opens. The game's own cursor
        // stays on the first spell, so its Confirm (equip/remove) works as always.
        static void EnterName(DetailsController c)
        {
            var v = c?.view;
            if (v?.NameText == null || c.selectCursor == null) { stop = Stop.Spells; return; }
            if (stop == Stop.Bonus) RestoreBonusDescription(v);
            SpellBank.ClearHint(c);
            var rows = Rows(v);
            if (rows.Count > 0 && c.selectCursor.Index != 0) c.SelectAbilityDataContent(0);
            foreach (var r in rows) r.SetNameTextFocus(false);
            stop = Stop.Name;
            stopIndex = c.selectCursor.Index;
            enterFrame = Time.frameCount;
            shownStone = c.targetmagicStone?.ContentId ?? -1;
            shownTarget = c.targetCharacter?.Id ?? -1;
            PlaceCursor(c);
            nameSavedDescription = v.DescriptionText?.text;
            ShowNameHint(c);
            Decorate(c);
            Hooks.NoteOnce("name-stop", () => $"[esper menu] name stop: hand target {anchor?.transform.position}, " +
                $"name '{v.NameText.text}' starts {TextStartX(v.NameText):0.00}, shift {NameShift(c, rows.Count > 0 ? rows[0] : null):0.00}");
        }

        static void ShowNameHint(DetailsController c)
        {
            var desc = c.view?.DescriptionText;
            if (desc == null) return;
            var target = c.targetCharacter;
            var stone = c.targetmagicStone;
            int sid = target?.characterStatusId ?? 0;
            var esper = stone != null && stone.IsOwned ? D.EsperByContent(stone.ContentId) : null;
            bool equipped = target != null && stone != null && target.MagicStoneId == stone.ContentId;
            var want = EsperRules.NameHint(D, EsperStore.Get(sid), sid, esper, equipped);
            if (desc.text == want) return;
            if (desc.text != nameHint) nameSavedDescription = desc.text;  // the game's own text
            nameHint = want;
            desc.text = want;
        }

        // From the name or the bonus row back onto a spell row: the game's cursor again.
        static void GoToRow(DetailsController c, int index)
        {
            var v = c.view;
            var desc = v?.DescriptionText;
            if (stop == Stop.Name && desc != null && desc.text == nameHint && nameSavedDescription != null) desc.text = nameSavedDescription;
            stop = Stop.Spells;
            var rows = Rows(v);
            if (rows.Count == 0) return;
            index = Math.Min(Math.Max(index, 0), rows.Count - 1);
            c.SelectAbilityDataContent(index);
            var row = rows[index];
            if (row.CursorParent != null) c.selectCursor.SetSource(row.CursorParent);
            row.SetNameTextFocus(true);
            Decorate(c);
        }

        // The game moved its cursor itself (a click): follow it.
        static void Drop(DetailsController c)
        {
            if (stop == Stop.Bonus) RestoreBonusDescription(c.view);
            var desc = c.view?.DescriptionText;
            if (stop == Stop.Name && desc != null && desc.text == nameHint && nameSavedDescription != null) desc.text = nameSavedDescription;
            stop = Stop.Spells;
            var rows = Rows(c.view);
            int i = c.selectCursor.Index;
            if (i >= 0 && i < rows.Count && rows[i].CursorParent != null) c.selectCursor.SetSource(rows[i].CursorParent);
            Decorate(c);
        }

        // The bonus row under the spells.
        static void Enter(DetailsController c)
        {
            var v = c.view;
            if (v?.LevelUpText == null) return;
            var desc = v.DescriptionText;
            if (stop == Stop.Name && desc != null && desc.text == nameHint && nameSavedDescription != null) desc.text = nameSavedDescription;
            SpellBank.ClearHint(c);
            stop = Stop.Bonus;
            stopIndex = c.selectCursor.Index;
            enterFrame = Time.frameCount;
            var rows = Rows(v);
            if (rows.Count > 0) rows[Math.Min(Math.Max(stopIndex, 0), rows.Count - 1)].SetNameTextFocus(false);
            PlaceCursor(c);
            ShowHint(c);
            Decorate(c);
        }

        static void Leave(DetailsController c, bool restoreCursor)
        {
            try
            {
                RestoreBonusDescription(c?.view);
                stop = Stop.Spells;
                if (c == null) return;
                Decorate(c);
                if (restoreCursor) GoToRow(c, stopIndex);
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.Leave", e); }
        }

        static void RestoreBonusDescription(DetailsView v)
        {
            var desc = v?.DescriptionText;
            if (desc != null && desc.text == hintText && savedDescription != null) desc.text = savedDescription;
        }

        // --- the hand cursor --------------------------------------------------------------

        // The hand follows a target object (Cursor.SetSource -> its PositionConstraint);
        // moving its transform does nothing. Our target sits in the spell rows' cursor
        // column, on the name's or the EL label's line (a child of that text, so the
        // panel's layout ignores it).
        static Text AnchorText(DetailsController c) => stop == Stop.Name ? c.view?.NameText : c.view?.LevelUpText;

        static void PlaceCursor(DetailsController c)
        {
            var label = AnchorText(c);
            if (label == null) return;
            if (anchor == null || anchor.WasCollected)
            {
                anchor = new GameObject("BnwMenuCursor");
                anchor.AddComponent<RectTransform>();
            }
            if (anchor.transform.parent == null || anchor.transform.parent.Pointer != label.transform.Pointer)
                anchor.transform.SetParent(label.transform, false);
            MoveAnchor(c);
            c.selectCursor.SetSource(anchor);
        }

        static void MoveAnchor(DetailsController c)
        {
            var label = AnchorText(c);
            if (anchor == null || anchor.WasCollected || label == null) return;
            var rows = Rows(c.view);
            var row = rows.Count > 0 ? rows[Math.Min(Math.Max(stopIndex, 0), rows.Count - 1)] : null;
            var mid = LeftMiddle(label.rectTransform);
            var column = row?.CursorParent != null ? row.CursorParent.transform.position
                : mid - new Vector3(40f * label.rectTransform.lossyScale.x, 0f, 0f);
            float x = column.x + (stop == Stop.Name ? NameShift(c, row) : 0f);
            anchor.transform.position = new Vector3(x, mid.y, column.z);
        }

        // The name row can start further left than the spell rows (the "equipped" check
        // mark): keep the hand as far from it as it is from a spell.
        static float NameShift(DetailsController c, Row row)
        {
            var v = c.view;
            var spell = row?.view?.MagicNameText;
            if (spell == null || v?.NameText == null) return 0f;
            float rowLeft = LeftMiddle(spell.GetComponent<RectTransform>()).x;
            float nameLeft = TextStartX(v.NameText);
            foreach (var icon in new[] { v.EquipIcon, v.TargetEquippedIcon })
                if (icon != null && icon.isActiveAndEnabled) nameLeft = Math.Min(nameLeft, LeftMiddle(icon.rectTransform).x);
            return Math.Min(0f, nameLeft - rowLeft);
        }

        // Where a text's visible characters start, in world space.
        static float TextStartX(Text t)
        {
            var corners = new Il2CppStructArray<Vector3>(4);
            t.rectTransform.GetWorldCorners(corners);
            float left = corners[0].x, right = corners[2].x, w = t.preferredWidth * t.rectTransform.lossyScale.x;
            var a = t.alignment;
            return a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight ? right - w
                : a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter ? (left + right - w) / 2
                : left;
        }

        static Vector3 LeftMiddle(RectTransform rt)
        {
            var corners = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(corners);
            return new Vector3(corners[0].x, (corners[0].y + corners[1].y) / 2f, corners[0].z);
        }

        // --- the bonus row: spending ELs ---------------------------------------------------

        static void ShowHint(DetailsController c)
        {
            var desc = c.view?.DescriptionText;
            var target = c.targetCharacter;
            var stone = c.targetmagicStone;
            if (desc == null || target == null || stone == null) return;
            if (desc.text != hintText) savedDescription = desc.text;
            int sid = target.characterStatusId;
            var esper = stone.IsOwned ? D.EsperByContent(stone.ContentId) : null;
            hintText = EsperRules.SpendHint(D, EsperStore.Get(sid), sid, esper, target.Name);
            desc.text = hintText;
        }

        static void Spend(DetailsController c)
        {
            var target = c.targetCharacter;
            var stone = c.targetmagicStone;
            if (target == null || stone == null) return;
            var esper = stone.IsOwned ? D.EsperByContent(stone.ContentId) : null;
            int sid = target.characterStatusId;
            var p = EsperStore.Get(sid);
            var result = EsperRules.TrySpend(D, ref p, sid, esper, EsperHooks.BaseOf(target.Parameter), out _);
            if (result == EsperRules.SpendResult.Spent && EsperStore.Put(sid, p))
            {
                Sound(SystemSeDefine.ShopAccounting);
                Plugin.L.LogInfo($"[esper menu] {target.Name} spent an EL on {esper?.Name}: EL {p.El}, {p.ElBank} left, totals {p.Spent}");
            }
            else
            {
                Sound(SystemSeDefine.BeepId);
                Plugin.L.LogInfo($"[esper menu] {target.Name} can't spend on {esper?.Name}: {result}");
            }
            Decorate(c);
            if (stop == Stop.Bonus) ShowHint(c);
        }

        internal static void Sound(int id)
        {
            try { AudioManager.Instance?.PlaySe(id, 0f, 0f, 0f); }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.Sound", e); }
        }

        // --- redraws ----------------------------------------------------------------------

        public static void UpdateView_Postfix(DetailsController __instance)
        {
            if (!Hooks.IsA<DetailsController>(__instance)) return;
            // another esper or character (Left/Right): back to the esper's name, as BNW
            var c = __instance;
            if (EsperHooks.Levels && ((c.targetmagicStone?.ContentId ?? -1) != shownStone || (c.targetCharacter?.Id ?? -1) != shownTarget))
                EnterName(c);
            Decorate(c);
        }

        public static void UpdateStoneDetailsView_Postfix(DetailsController __instance)
        {
            if (Hooks.IsA<DetailsController>(__instance)) Decorate(__instance);
        }

        static void Decorate(DetailsController c)
        {
            if (!EsperHooks.Levels) return;
            try
            {
                var v = c?.view;
                var target = c?.targetCharacter;
                if (v == null || target == null) return;
                Hooks.NoteOnce("key-config", KeyConfigDump);
                SpellBank.Decorate(c);
                var label = v.LevelUpText;
                if (label == null) { Hooks.NoteOnce("no-label", () => "[esper menu] no level-up label; EL status not shown"); return; }
                if (!IsOurLabel(label.text))
                {
                    originalLabel = label.text;
                    labelColor = label.color;
                    labelColorKnown = true;
                }
                int sid = target.characterStatusId;
                var progress = EsperStore.Get(sid);
                var lines = EsperRules.StatusLines(D, progress, sid, SpendKey != null ? KeyLabel() : "");
                if (lines.Count == 0)
                {
                    if (originalLabel != null) label.text = originalLabel;
                    if (labelColorKnown) label.color = labelColor;
                    return;
                }
                float room = Room(label, v.ParameterValueText);
                foreach (var line in lines)
                {
                    label.text = line;
                    if (room <= 0 || label.preferredWidth <= room) break;
                }
                if (labelColorKnown) label.color = progress.ElBank > 0 ? Gold : labelColor;
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.Decorate", e); }
        }

        // our label texts, as opposed to the game's own "At level up:"
        static bool IsOurLabel(string text) =>
            text.StartsWith("Unspent EL", StringComparison.Ordinal) || text.StartsWith("EL", StringComparison.Ordinal);

        static string KeyLabel()
        {
            try
            {
                var il = InputListener.Instance;
                if (il != null && il.GetLastInputDeviceType() == Device.KeyBoard && SpendKey != null)
                {
                    var values = il.KeyConfig?.GetKeyValues(SpendKey.Name);
                    if (values != null)
                        foreach (var v in values)
                            if (v != null && v.DeviceType == Device.KeyBoard) return v.KeyCode.ToString();
                }
            }
            catch (Exception e) { Hooks.WarnOnce("EsperMenu.KeyLabel", e); }
            return SpendKeyName;
        }

        // Width the label can use before it would run into the bonus text on the same row
        // (in the label's own units), or -1 if the value isn't to its right on that row.
        static float Room(Text label, Text value)
        {
            if (value == null) return -1;
            var lc = new Il2CppStructArray<Vector3>(4);
            var vc = new Il2CppStructArray<Vector3>(4);
            label.rectTransform.GetWorldCorners(lc);
            value.rectTransform.GetWorldCorners(vc);
            float scale = label.rectTransform.lossyScale.x;
            float vScale = value.rectTransform.lossyScale.x;
            if (scale <= 0 || vScale <= 0) return -1;
            float vLeft = vc[0].x, vRight = vc[2].x, textWidth = value.preferredWidth * vScale;
            var a = value.alignment;
            float start = a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight ? vRight - textWidth
                : a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter ? (vLeft + vRight - textWidth) / 2
                : vLeft;
            float vMidY = (vc[0].y + vc[1].y) / 2;
            bool sameRow = vMidY >= lc[0].y && vMidY <= lc[1].y;
            float room = sameRow && start > lc[0].x ? (start - lc[0].x) / scale - 12 : -1;
            Hooks.NoteOnce("label-geometry", () =>
                $"[esper menu] label x {lc[0].x:0.00}..{lc[2].x:0.00} y {lc[0].y:0.00}..{lc[1].y:0.00} scale {scale:0.####} align {label.alignment} " +
                $"width {label.preferredWidth:0} ('{label.text}') overflow {label.horizontalOverflow}; " +
                $"value x {vLeft:0.00}..{vRight:0.00} y {vc[0].y:0.00}..{vc[1].y:0.00} align {a} width {value.preferredWidth:0} " +
                $"overflow {value.horizontalOverflow} text starts {start:0.00}; room {room:0.0}");
            return room;
        }

        static string KeyConfigDump()
        {
            var parts = new List<string>();
            try
            {
                var cfg = InputListener.Instance?.KeyConfig;
                foreach (var name in new[] { "Action", "Cancel", "Up", "Down", "Menu", "Shortcut", "Start", "Select", "PageUp", "PageDown" })
                {
                    var keys = new List<string>();
                    var values = cfg?.GetKeyValues(name);
                    if (values != null)
                        foreach (var v in values)
                            if (v != null) keys.Add(v.DeviceType == Device.KeyBoard ? v.KeyCode.ToString() : $"{v.DeviceType}:{v.KeyName}");
                    parts.Add($"{name}=[{string.Join("/", keys)}]");
                }
            }
            catch (Exception e) { parts.Add(e.Message); }
            return $"[esper menu] extra spend key = '{SpendKeyName}'; key config: {string.Join(" ", parts)}";
        }
    }
}

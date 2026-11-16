using System;
using System.Collections.Generic;
using BraveNewWorld.Rules;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.Data.User;
using Last.Defaine.Audio;
using Last.Management;
using Serial.FF6.UI;
using UnityEngine;
using UnityEngine.UI;
using DetailsController = Serial.FF6.UI.KeyInput.MagicStoneDetailsController;
using DetailsView = Serial.FF6.UI.KeyInput.MagicStoneDetailsView;
using Row = Serial.FF6.UI.KeyInput.MagicStoneDetailsContentController;
using Object = UnityEngine.Object;
using Hooks = PrKit.Hooks;

namespace BraveNewWorld.PR
{
    // BNW's SP bank on the esper details screen (c3.asm Pressed_A / Blue_Bank_Txt / MPCost):
    //  * spell rows as BNW's: name, MP cost, SP cost (blank once learned), under "Learn";
    //    no progress or rate (BNW learns nothing in battle; the port sets those rates to 0);
    //  * "SP 20/30" under the spell list;
    //  * Confirm on a spell row pays the full cost from the bank and teaches it at once
    //    (cha-ching), or buzzes. The description box says which before the press.
    // EsperMenu owns the cursor; this class does the spell rows' text and the learning.
    static class SpellBank
    {
        const int Mastered = 100;  // OwnedAbility.SkillLevel of a learned spell (100%)
        static BnwData D => EsperHooks.D;

        static string lastHint = "";

        struct Ctx
        {
            public OwnedCharacterData Target;
            public Esper Esper;
            public int Sid;
            public bool Banks => Target != null && EsperRules.BanksSp(D, Sid);
        }

        static Ctx Of(DetailsController c)
        {
            var target = c?.targetCharacter;
            var stone = c?.targetmagicStone;
            return new Ctx
            {
                Target = target,
                Sid = target?.characterStatusId ?? 0,
                Esper = stone != null && stone.IsOwned ? D.EsperByContent(stone.ContentId) : null,
            };
        }

        // Spell rows take Confirm (learn) only for characters with an SP bank and a BNW esper.
        internal static bool Active(DetailsController c)
        {
            var x = Of(c);
            return x.Banks && x.Esper != null && x.Esper.Spells.Length > 0;
        }

        static int AbilityIdOf(Row row) => row?.AbilityData?.OwnedAbility?.Ability?.Id ?? -1;

        static OwnedAbility Owned(OwnedCharacterData target, int abilityId)
        {
            var list = target?.OwnedAbilityList;
            if (list == null) return null;
            foreach (var a in list)
                if (a?.Ability != null && a.Ability.Id == abilityId) return a;
            return null;
        }

        static bool Known(OwnedCharacterData target, Row row)
        {
            var shown = row?.AbilityData?.OwnedAbility;
            var owned = Owned(target, AbilityIdOf(row));
            return (owned?.SkillLevel ?? 0) >= Mastered || (shown?.SkillLevel ?? 0) >= Mastered;
        }

        static Row RowAt(DetailsController c, int index)
        {
            var rows = EsperMenu.Rows(c?.view);
            return index >= 0 && index < rows.Count ? rows[index] : null;
        }

        // --- learning -------------------------------------------------------------------

        internal static void Learn(DetailsController c, int index)
        {
            var x = Of(c);
            var row = RowAt(c, index);
            if (row == null || x.Target == null) return;
            int abilityId = AbilityIdOf(row);
            var spell = x.Esper?.SpellByAbility(abilityId);
            var p = EsperStore.Get(x.Sid);
            var result = EsperRules.TryLearn(D, ref p, x.Sid, x.Esper, abilityId, Known(x.Target, row), out int cost);
            if (result == EsperRules.LearnResult.Learned && !EsperStore.Available) result = EsperRules.LearnResult.NoSpBank;
            if (result != EsperRules.LearnResult.Learned)
            {
                EsperMenu.Sound(SystemSeDefine.BeepId);
                Plugin.L.LogInfo($"[spell bank] {x.Target.Name} can't learn {spell?.Name ?? abilityId.ToString()} ({cost} SP, " +
                                 $"bank {EsperStore.Get(x.Sid).SpBank}): {result}");
                return;
            }
            var owned = Teach(x.Target, abilityId);
            if (owned == null)
            {
                EsperMenu.Sound(SystemSeDefine.BeepId);
                Plugin.L.LogWarning($"[spell bank] couldn't add ability {abilityId} to {x.Target.Name}; no SP taken");
                return;
            }
            EsperStore.Put(x.Sid, p);
            EsperMenu.Sound(SystemSeDefine.ShopAccounting);
            Plugin.L.LogInfo($"[spell bank] {x.Target.Name} learned {spell?.Name} for {cost} SP: SP {EsperRules.SpBankText(D, p)} " +
                             $"(skill level now {Owned(x.Target, abilityId)?.SkillLevel})");
            try { row.SetData(x.Target, new MagicStoneAbilityData(owned, row.AbilityData?.AcquisitionValue ?? 0)); }
            catch (Exception e) { Hooks.WarnOnce("SpellBank.Refresh", e); }
            Decorate(c);
            ApplyHint(c, index);
        }

        // The character's own copy of the spell at 100%, added first if battles never started it.
        static OwnedAbility Teach(OwnedCharacterData target, int abilityId)
        {
            try
            {
                var owned = Owned(target, abilityId);
                if (owned == null)
                {
                    var added = new OwnedCharacterClient().AddAbility(target.Id, abilityId);
                    owned = Owned(target, abilityId) ?? added;
                }
                if (owned == null) return null;
                owned.SkillLevel = Mastered;
                return owned;
            }
            catch (Exception e) { Hooks.WarnOnce("SpellBank.Teach", e); return null; }
        }

        // --- text -------------------------------------------------------------------------

        // Second line of the description box on a spell row: what Confirm will do.
        internal static void ApplyHint(DetailsController c, int index)
        {
            var desc = c?.view?.DescriptionText;
            if (desc == null) return;
            var x = Of(c);
            var row = RowAt(c, index);
            string hint = "";
            if (row != null && x.Esper != null)
                hint = EsperRules.LearnHint(D, EsperStore.Get(x.Sid), x.Sid, x.Esper, x.Esper.SpellByAbility(AbilityIdOf(row)),
                    Known(x.Target, row), x.Target?.Name ?? "");
            var text = desc.text ?? "";
            if (lastHint.Length > 0 && text.EndsWith("\n" + lastHint, StringComparison.Ordinal))
                text = text.Substring(0, text.Length - lastHint.Length - 1);
            lastHint = hint;
            // the game's own description stays on line 1 (two-line descriptions are left alone)
            var want = hint.Length > 0 && text.IndexOf('\n') < 0 ? text + "\n" + hint : text;
            if (desc.text != want) desc.text = want;
        }

        internal static void ClearHint(DetailsController c)
        {
            var desc = c?.view?.DescriptionText;
            if (desc != null && lastHint.Length > 0 && (desc.text ?? "").EndsWith("\n" + lastHint, StringComparison.Ordinal))
                desc.text = desc.text.Substring(0, desc.text.Length - lastHint.Length - 1);
            lastHint = "";
        }

        // BNW's spell rows (c3.asm MPCost/SPCost, "skip drawing spell progress"):
        //   Bolt ★        2 MP      1 SP        (SP cost blank once learned; the star marks it)
        // over a "Learn" heading, and "SP 20/30" under the list. The PR's number texts keep
        // the digits; the "MP"/"SP" units are small copies of a heading placed after them.
        // Cheap enough to run every few frames (the game rewrites row texts as it redraws).
        internal static void Decorate(DetailsController c)
        {
            if (!EsperHooks.Levels) return;
            var v = c?.view;
            if (v == null) return;
            var x = Of(c);
            var rows = EsperMenu.Rows(v);
            try
            {
                if (x.Esper == null) { ShowBank(v, rows, null); return; }
                Set(v.SkillLevelText, "");
                Set(v.AcquisitionRatetext, LearnHeader);
                foreach (var row in rows)
                {
                    var spell = x.Esper.SpellByAbility(AbilityIdOf(row));
                    var rv = row.view;
                    if (spell == null || rv == null) continue;
                    bool known = Known(x.Target, row);
                    Set(rv.ColonText, "");
                    Set(rv.PercentText, "");
                    Set(rv.MultiplicationText, "");
                    Set(rv.SkillLevelText, MpCost(row));
                    Set(rv.AcquisitionRateText, known ? "" : spell.Sp.ToString());
                    Unit(rv.SkillLevelText, v.AcquisitionRatetext, "MP");
                    Unit(rv.AcquisitionRateText, v.AcquisitionRatetext, known ? null : "SP");
                }
            }
            catch (Exception e) { Hooks.WarnOnce("SpellBank.Rows", e); }
            try { ShowBank(v, rows, x.Banks ? EsperRules.SpBankText(D, EsperStore.Get(x.Sid)) : null); }
            catch (Exception e) { Hooks.WarnOnce("SpellBank.Bank", e); }
        }

        const string LearnHeader = "Learn";
        const string UnitName = "BnwUnit", BankName = "BnwSpBank";

        static string MpCost(Row row)
        {
            var ability = row?.AbilityData?.OwnedAbility?.Ability;
            return ability == null ? "" : ability.UseValue.ToString();
        }

        static void Set(Text t, string s)
        {
            if (t != null && t.text != s) t.text = s;
        }

        // A unit label ("MP", "SP") just after a number's visible end, styled like `style`;
        // a child of the number text so it moves with its row. null hides it.
        static void Unit(Text number, Text style, string text)
        {
            if (number == null || style == null) return;
            var found = number.transform.Find(UnitName);
            if (text == null || number.text.Length == 0)
            {
                if (found != null) found.gameObject.SetActive(false);
                return;
            }
            var t = found != null ? found.GetComponent<Text>() : Copy(style, number.transform, UnitName, TextAnchor.MiddleLeft);
            t.gameObject.SetActive(true);
            Set(t, text);
            var r = number.rectTransform.rect;
            float gap = Math.Max(4f, t.fontSize * 0.35f);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(EndInRect(number) + gap, 0f);
            rt.sizeDelta = new Vector2(t.preferredWidth + 2f, r.height);
        }

        // Where a text's visible characters end, measured from its rect's left edge.
        static float EndInRect(Text t)
        {
            var r = t.rectTransform.rect;
            float w = t.preferredWidth;
            var a = t.alignment;
            return a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight ? r.width
                : a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter ? (r.width + w) / 2
                : w;
        }

        // A plain copy of a game text (its font, color, outline and shadow), without children.
        static Text Copy(Text style, Transform parent, string name, TextAnchor align)
        {
            var go = Object.Instantiate(style.gameObject);
            go.name = name;
            go.transform.SetParent(parent, false);
            for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
            var t = go.GetComponent<Text>();
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        // "SP 20/30" (BNW Blue_Bank_Txt: "SP" blue, the number white) in the empty row under
        // the spells (BNW espers teach at most 3 of the 5 slots), ending where the rows' SP
        // units end, as BNW right-aligns it under "Learn".
        static void ShowBank(DetailsView v, List<Row> rows, string bank)
        {
            var learn = v?.AcquisitionRatetext;
            if (learn == null) return;
            var holder = learn.transform.Find(BankName);
            if (bank == null || rows.Count == 0 || rows[0].CursorParent == null)
            {
                if (holder != null) holder.gameObject.SetActive(false);
                return;
            }
            Text label, value;
            if (holder == null)
            {
                var go = new GameObject(BankName);
                go.AddComponent<RectTransform>();
                go.transform.SetParent(learn.transform, false);
                label = Copy(learn, go.transform, "Label", TextAnchor.MiddleRight);
                value = Copy(v.ParameterValueText ?? learn, go.transform, "Value", TextAnchor.MiddleRight);
                holder = go.transform;
            }
            else
            {
                label = holder.Find("Label")?.GetComponent<Text>();
                value = holder.Find("Value")?.GetComponent<Text>();
                if (label == null || value == null) return;
            }
            holder.gameObject.SetActive(true);
            Set(label, "SP");
            Set(value, bank);

            // the 5th row slot: rows are evenly spaced, the first at rows[0]
            float y0 = rows[0].CursorParent.transform.position.y;
            float pitch = rows.Count >= 2 && rows[1].CursorParent != null
                ? rows[1].CursorParent.transform.position.y - y0
                : y0 - WorldMid(learn.rectTransform).y;
            float y = y0 + 4 * pitch;
            float right = UnitsEnd(rows) ?? WorldRight(learn.rectTransform);
            float z = learn.transform.position.z;
            float h = learn.rectTransform.rect.height;
            Place(value, right, y, z, h);
            float gap = value.fontSize * 0.4f * value.rectTransform.lossyScale.x;
            Place(label, right - value.preferredWidth * value.rectTransform.lossyScale.x - gap, y, z, h);
            Hooks.NoteOnce("sp-bank-layout", () =>
                $"[spell bank] rows at y {y0:0.00} pitch {pitch:0.00}; 'SP {bank}' at y {y:0.00} ending x {right:0.00}; " +
                $"'Learn' x {WorldLeft(learn.rectTransform):0.00}..{WorldRight(learn.rectTransform):0.00} align {learn.alignment}; " +
                $"SP number align {rows[0].view?.AcquisitionRateText?.alignment}, MP number align {rows[0].view?.SkillLevelText?.alignment}");
        }

        static void Place(Text t, float right, float y, float z, float height)
        {
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(t.preferredWidth + 2f, height);
            rt.position = new Vector3(right, y, z);
        }

        // Right edge of the rows' "SP" units (they all line up), or null if none shows.
        static float? UnitsEnd(List<Row> rows)
        {
            foreach (var row in rows)
            {
                var unit = row.view?.AcquisitionRateText?.transform.Find(UnitName);
                if (unit != null && unit.gameObject.activeSelf)
                {
                    var t = unit.GetComponent<Text>();
                    return WorldLeft(t.rectTransform) + t.preferredWidth * t.rectTransform.lossyScale.x;
                }
            }
            return null;
        }

        static Vector3[] Corners(RectTransform rt)
        {
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            return new[] { c[0], c[1], c[2], c[3] };
        }

        static float WorldLeft(RectTransform rt) => Corners(rt)[0].x;
        static float WorldRight(RectTransform rt) => Corners(rt)[2].x;

        static Vector3 WorldMid(RectTransform rt)
        {
            var c = Corners(rt);
            return (c[0] + c[2]) / 2f;
        }
    }
}

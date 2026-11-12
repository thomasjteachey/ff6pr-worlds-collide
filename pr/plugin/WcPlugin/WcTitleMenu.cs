using System;
using HarmonyLib;
using Last.Data;
using Last.Defaine;
using Last.Management;

namespace WorldsCollide.PR
{
    // The Worlds Collide screen as an entry of the title screen's own menu ("Worlds Collide"),
    // above the game's Option entry, so it works with
    // whatever buttons the game uses (Steam Deck included).
    // How the game builds the menu (decompiled, tools/decomp/title): KeyInput
    // TitleMenuCommandController.Initialize(contents) puts entries with id < 6 in the main menu
    // (6-11 Extra, 12-15 Options, the rest Config); their Name is a message key; picking one
    // calls TitleWindowController.SetCommand(id, ...), whose switch ignores ids it doesn't
    // know. Our entry uses id -1 (no game entry has it) and SetCommand(-1) opens the screen.
    static class WcTitleMenu
    {
        const int MenuId = -1;
        const string MenuKey = "MENU_TITLE_FF6MOD";
        const string MessageId = "MSG_FF6MOD_TITLE_MENU";

        public static void Patch(Harmony h)
        {
            var init = AccessTools.Method(typeof(Last.UI.KeyInput.TitleMenuCommandController), "Initialize");
            var set = AccessTools.Method(typeof(Last.UI.KeyInput.TitleWindowController), "SetCommand");
            if (init != null) h.Patch(init, prefix: new HarmonyMethod(typeof(WcTitleMenu), nameof(Initialize_Prefix)));
            if (set != null) h.Patch(set, prefix: new HarmonyMethod(typeof(WcTitleMenu), nameof(SetCommand_Prefix)));
            // the entry's label: the menu looks Names up in a table our key isn't in, so set our
            // entry's text once the menu is built, and again whenever it refreshes its views
            if (init != null) h.Patch(init, postfix: new HarmonyMethod(typeof(WcTitleMenu), nameof(Label_Postfix)));
            foreach (var name in new[] { "UpdateView", "SetActiveCommandList" })
            {
                var m = AccessTools.Method(typeof(Last.UI.KeyInput.TitleMenuCommandController), name);
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(WcTitleMenu), nameof(Label_Postfix)));
            }
            Plugin.L.LogInfo($"[wc] title menu entry: {(init != null && set != null ? "hooked" : "not found")}");
        }

        public static string Label => "Worlds Collide";

        public static void Initialize_Prefix(Il2CppSystem.Collections.Generic.List<TitleCommandContentData> data)
        {
            try
            {
                if (data == null) return;
                int at = -1;
                for (int i = 0; i < data.Count; i++)
                {
                    var c = data[i];
                    if (c == null) continue;
                    if ((int)c.Id == MenuId) return;  // already there
                    if (at < 0 && c.Id == TitleCommandId.Option) at = i;
                }
                // menu text goes key -> message id (messageConclusionDictionary) -> text
                // (GetMessageByMessageConclusion), so register both steps
                var msg = MessageManager.Instance;
                if (msg != null)
                {
                    var conclusion = msg.messageConclusionDictionary;
                    if (conclusion != null) conclusion[MenuKey] = MessageId;
                    if (msg.messageDictionary.ContainsKey(MessageId)) msg.messageDictionary[MessageId] = Label;
                    else msg.AddMessage(MessageId, Label);
                }
                var entry = new TitleCommandContentData((TitleCommandId)MenuId, MenuKey);
                if (at < 0) data.Add(entry); else data.Insert(at, entry);
                Plugin.L.LogInfo($"[wc] title menu: '{Label}' added ({data.Count} entries)");
            }
            catch (Exception e) { WcRunner.WarnOnce("title-menu", e); }
        }

        static bool labelLogged;

        public static void Label_Postfix(Last.UI.KeyInput.TitleMenuCommandController __instance)
        {
            try
            {
                var views = __instance?.contents;
                if (views == null) return;
                for (int i = 0; i < views.Count; i++)
                {
                    var v = views[i];
                    if (v == null || (int)v.CommandId != MenuId || v.nameText == null) continue;
                    if (v.nameText.text != Label) v.nameText.text = Label;
                    if (!labelLogged) { labelLogged = true; Plugin.L.LogInfo($"[wc] title menu: label '{Label}' set"); }
                    Fit(v);
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("title-label", e); }
        }

        internal static void FitForTest(Last.UI.KeyInput.TitleMenuCommandController ctrl) => Label_Postfix(ctrl);

        static bool fitLogged;

        // The main menu is a VerticalLayoutGroup in a fixed-height box sized for the game's 5
        // entries (5 x 53 + 4 x 12 = 313, centred); a 6th spills ~32 px above and below it, over
        // the logo and the copyright line. Close the gaps so every active entry fits the same box.
        static void Fit(Last.UI.KeyInput.TitleCommandContentView ours)
        {
            var parent = ours.transform.parent;
            var group = parent?.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            var box = parent?.GetComponent<UnityEngine.RectTransform>();
            var row = ours.GetComponent<UnityEngine.RectTransform>();
            if (group == null || box == null || row == null) return;
            int n = 0;
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).gameObject.activeSelf) n++;
            if (n < 2) return;
            float height = box.sizeDelta.y, rowH = row.sizeDelta.y;
            if (height <= 0 || rowH <= 0) return;
            float spacing = Math.Max(0f, Math.Min(12f, (height - n * rowH) / (n - 1)));
            if (Math.Abs(group.spacing - spacing) < 0.01f) return;
            if (!fitLogged)
            {
                fitLogged = true;
                Plugin.L.LogInfo($"[wc] title menu: {n} entries of {rowH} in a {height} box: spacing {group.spacing} -> {spacing:0.#}");
            }
            group.spacing = spacing;
            UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(box);
        }

        public static bool SetCommand_Prefix(TitleCommandId id)
        {
            // a New Game picked on the title menu: the opening (sc_e_0001) starts WC's game only
            // then (WC:start), not for the title's attract demo, which runs the same script
            if (id == TitleCommandId.NewGame || id == TitleCommandId.StrongNewGame)
            {
                WcEvents.NewGameChosenAt = UnityEngine.Time.realtimeSinceStartup;
                Plugin.L.LogInfo($"[wc] title menu: {id} chosen");
            }
            if ((int)id != MenuId) return true;
            try
            {
                Plugin.L.LogInfo("[wc] title menu: opening the screen");
                WcScreen.Show(true);
            }
            catch (Exception e) { WcRunner.WarnOnce("title-open", e); }
            return false;
        }
    }
}

using HarmonyLib;
using Last.Defaine;
using PrKit.Seeds;

namespace WorldsCollide.PR
{
    // The Worlds Collide screen as an entry of the title screen's own menu ("Worlds Collide"), above
    // the game's Option entry: the kit's TitleMenuEntry (PrKit/Seeds; WC's own code until 2026-11-20).
    // A New Game picked there also starts WC's game (the opening, sc_e_0001, runs WC:start only then,
    // not for the title's attract demo, which runs the same script).
    static class WcTitleMenu
    {
        public static string Label => "Worlds Collide";

        public static void Patch(Harmony h)
        {
            TitleMenuEntry.Label = Label;
            TitleMenuEntry.Tag = "[wc]";
            TitleMenuEntry.OnError = WcRunner.WarnOnce;
            TitleMenuEntry.Open = () => WcScreen.Show(true);
            TitleMenuEntry.Chosen = id =>
            {
                if (id == TitleCommandId.NewGame || id == TitleCommandId.StrongNewGame)
                {
                    WcEvents.NewGameChosenAt = UnityEngine.Time.realtimeSinceStartup;
                    Plugin.L.LogInfo($"[wc] title menu: {id} chosen");
                }
            };
            TitleMenuEntry.Patch(h);
        }

        internal static void FitForTest(Last.UI.KeyInput.TitleMenuCommandController ctrl) => TitleMenuEntry.FitForTest(ctrl);
    }
}

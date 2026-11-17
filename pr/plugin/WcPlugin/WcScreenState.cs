namespace WorldsCollide.PR
{
    // A tiny gate that lets normal startup avoid touching WcScreen (and Unity IMGUI) until the
    // player actually opens the WC/Track screen or a gameplay notification needs it.
    static class WcScreenState
    {
        internal static bool Active;
    }
}

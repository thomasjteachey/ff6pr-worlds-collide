namespace WorldsCollide.PR
{
    // Keep the production path from touching WcTest's very large generated type. On IL2CPP/
    // Proton, its first access costs several seconds even when there is no test request.
    static class WcTestState
    {
        internal static bool Running;
    }
}

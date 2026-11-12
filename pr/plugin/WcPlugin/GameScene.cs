using System;

namespace WorldsCollide.PR
{
    // which scene the game is in, from its scene manager (cheap; Object.FindObjectOfType searches
    // every object in the scene)
    static class GameScene
    {
        internal static bool OnTitle()
        {
            try
            {
                var m = global::Last.Management.SceneManager.Instance?.GetCurrentSubSceneManager();
                return m != null && m.TryCast<global::Last.Management.SubSceneManagerTitle>() != null;
            }
            catch (Exception) { return false; }
        }
    }
}

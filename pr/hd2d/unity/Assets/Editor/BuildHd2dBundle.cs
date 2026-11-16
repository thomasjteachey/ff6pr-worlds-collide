// Batch build of the HD-2D shader bundle (see pr/hd2d/build_bundle.sh):
//   Unity.exe -batchmode -nographics -quit -projectPath pr/hd2d/unity -executeMethod BuildHd2dBundle.Build
// Writes pr/hd2d/out/hd2d.bundle for StandaloneWindows64 (the Steam Deck runs the same
// Windows build through Proton). Exits non-zero if a shader has compile errors.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildHd2dBundle
{
    static readonly string[] ShaderPaths = { "Assets/HD2D/HD2DPost.shader", "Assets/HD2D/HD2DShadow.shader" };

    public static void Build()
    {
        // D3D11 is what the game uses on Windows and under Proton (DXVK); the others are
        // cheap insurance for -force-vulkan / -force-glcore launches.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLCore });

        foreach (var path in ShaderPaths)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) Fail($"can't load {path}");
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages)
                Debug.Log($"[hd2d] {path} {m.severity}: {m.message} ({m.file}:{m.line}, {m.platform})");
            if (messages.Any(m => m.severity == ShaderCompilerMessageSeverity.Error))
                Fail($"{path} has compile errors");
        }

        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "out"));
        Directory.CreateDirectory(outDir);
        var builds = new[]
        {
            new AssetBundleBuild { assetBundleName = "hd2d.bundle", assetNames = ShaderPaths },
        };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle |
            BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) Fail("BuildAssetBundles failed");
        Debug.Log($"[hd2d] built {Path.Combine(outDir, "hd2d.bundle")} with {Application.unityVersion}");
    }

    static void Fail(string why)
    {
        Debug.LogError("[hd2d] " + why);
        EditorApplication.Exit(1);
    }
}

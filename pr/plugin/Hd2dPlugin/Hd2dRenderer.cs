using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using Last.Camera;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hd2d
{
    // How the game draws a frame (offline scan of level0 + the API dump, confirmed by the
    // camera-order log of v0.1):
    //  - off-screen cameras first: the field (CameraTileMap, then CameraFieldMain) into the
    //    320x180 'Field' texture, the world map (CameraBirdView) into 960x540 'BirdView',
    //    battles into 384x216 'Battle' (backdrop camera CameraBattlebackGround at depth -5,
    //    then the characters), the UI into 'Overlay';
    //  - then a screen camera (CameraFrontFilter) draws the composite material
    //    (PostProcessLite.GetMaterial(), shader Last/PostProcessLite), which samples
    //    _MainGameTex (the current main image) and _OverlayTex (UI).
    // So: right after the battle backdrop camera renders, blur the backdrop in place (the
    // characters are drawn on top afterwards, sharp). When the first screen camera of a frame
    // is about to render, process the main image into our own texture and point _MainGameTex
    // at it; at the end of the frame put the game's texture back, so nothing in the game's
    // own code ever sees ours.
    static class Hd2dRenderer
    {
        internal const int PassUpscale = 0, PassDown = 1, PassFinal = 2, PassBackdrop = 3;
        const int Levels = 4;

        static AssetBundle bundle;
        static Shader shader;
        static Material mat;
        static RenderTexture output;
        static Camera.CameraCallback preCull, preRender, postRender;
        internal static RenderTexture BackdropTexture { get; private set; }

        static Material swappedOn;
        static Texture original;
        static bool swapped;
        static int processedFrame = -1, backdropFrame = -1;
        static int errors;
        static bool selfTested;

        static int mainGameTex, mainUL, mainUH, mainVL, mainVH;
        static readonly int[] idL = new int[Levels];
        static int idSharpScale, idView, idFocus, idBlur, idSet, idSetBlur, idHaze, idHazeColor, idBloom,
                   idMotes, idMotesColor, idShadows, idHighlights, idGrade, idAspect;

        static readonly List<RenderTexture> temps = new List<RenderTexture>();
        static RenderTextureFormat levelFormat = RenderTextureFormat.ARGBHalf;

        internal static Mode CurrentMode { get; private set; }
        static Mode lastMode = Mode.None;
        static int lastW, lastH;
        static Vector4 lastView = new Vector4(0, 1, 0, 1);
        static float focusY = -1f, lastTime;
        static readonly HashSet<string> logged = new HashSet<string>();
        static readonly Dictionary<IntPtr, bool> backdropCams = new Dictionary<IntPtr, bool>();

        // camera-order trace, logged once per kind of image
        static int traceFrame = -1;
        static readonly HashSet<Mode> traced = new HashSet<Mode>();
        static readonly StringBuilder trace = new StringBuilder();

        public static bool LoadShader(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                Plugin.L.LogError($"missing {path}: copy hd2d.bundle next to the plugin DLL. HD-2D is off.");
                return false;
            }
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.L.LogError($"can't load {path} (built for another Unity version?). HD-2D is off.");
                return false;
            }
            foreach (var o in bundle.LoadAllAssets(Il2CppType.Of<Shader>()))
            {
                var s = o?.TryCast<Shader>();
                if (s != null && s.name == "Hidden/HD2D/Post") shader = s;
            }
            if (shader == null)
            {
                Plugin.L.LogError("hd2d.bundle has no Hidden/HD2D/Post shader. HD-2D is off.");
                return false;
            }
            if (!shader.isSupported)
            {
                Plugin.L.LogError($"the HD-2D shader isn't supported with {SystemInfo.graphicsDeviceType}. HD-2D is off.");
                return false;
            }
            shader.hideFlags = HideFlags.DontUnloadUnusedAsset;
            mat = new Material(shader) { name = "HD2D Post", hideFlags = HideFlags.HideAndDontSave };
            Diorama.Load(bundle);
            if (!SystemInfo.SupportsRenderTextureFormat(levelFormat)) levelFormat = RenderTextureFormat.ARGB32;

            mainGameTex = Shader.PropertyToID(TexName());
            mainUL = Shader.PropertyToID("_MainGameUL");
            mainUH = Shader.PropertyToID("_MainGameUH");
            mainVL = Shader.PropertyToID("_MainGameVL");
            mainVH = Shader.PropertyToID("_MainGameVH");
            for (int i = 0; i < Levels; i++) idL[i] = Shader.PropertyToID("_L" + (i + 1));
            idSharpScale = Shader.PropertyToID("_SharpScale");
            idView = Shader.PropertyToID("_View");
            idFocus = Shader.PropertyToID("_Focus");
            idBlur = Shader.PropertyToID("_Blur");
            idSet = Shader.PropertyToID("_Set");
            idSetBlur = Shader.PropertyToID("_SetBlur");
            idHaze = Shader.PropertyToID("_Haze");
            idHazeColor = Shader.PropertyToID("_HazeColor");
            idBloom = Shader.PropertyToID("_Bloom");
            idMotes = Shader.PropertyToID("_Motes");
            idMotesColor = Shader.PropertyToID("_MotesColor");
            idShadows = Shader.PropertyToID("_Shadows");
            idHighlights = Shader.PropertyToID("_Highlights");
            idGrade = Shader.PropertyToID("_Grade");
            idAspect = Shader.PropertyToID("_Aspect");
            Plugin.L.LogInfo($"shader ready ({SystemInfo.graphicsDeviceType}, {QualitySettings.activeColorSpace} colour space, " +
                             $"blur buffers {levelFormat})");
            return true;
        }

        static string TexName()
        {
            try
            {
                var n = FrontRenderTarget.kMainGameTexName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch (Exception e) { Plugin.L.LogWarning($"kMainGameTexName: {e.Message}"); }
            return "_MainGameTex";
        }

        public static void Install()
        {
            preCull = new Action<Camera>(OnPreCull);
            preRender = new Action<Camera>(OnPreRender);
            postRender = new Action<Camera>(OnPostRender);
            var cull = Camera.onPreCull;
            Camera.onPreCull = cull == null ? preCull : cull + preCull;
            var pre = Camera.onPreRender;
            Camera.onPreRender = pre == null ? preRender : pre + preRender;
            var post = Camera.onPostRender;
            Camera.onPostRender = post == null ? postRender : post + postRender;
        }

        static void Fail(Exception e)
        {
            if (++errors <= 3 || errors == 21)
                Plugin.L.LogError($"[hd2d] {e}" + (errors == 21 ? "\n(too many errors: HD-2D turned itself off)" : ""));
        }

        // ---- per frame: the composite ----

        static void OnPreCull(Camera cam)
        {
            if (errors > 20) return;
            try { Diorama.PreCull(cam); }
            catch (Exception e) { Fail(e); }
        }

        static void OnPreRender(Camera cam)
        {
            try
            {
                if (cam == null) return;
                Diorama.PreRender(cam);
                if (traceFrame == Time.frameCount)
                    trace.Append($"{cam.name}(d{cam.depth}{(cam.targetTexture != null ? " -> " + cam.targetTexture.name : " -> screen")}) ");
                if (cam.targetTexture != null) return;          // off-screen camera
                int frame = Time.frameCount;
                if (frame == processedFrame) return;            // one pass per frame
                processedFrame = frame;
                Restore();
                if (!Settings.Enabled || errors > 20) { CurrentMode = Mode.None; return; }
                Process();
            }
            catch (Exception e)
            {
                Restore();
                Fail(e);
            }
        }

        // Put the game's own texture back (end of frame, and defensively at frame start).
        internal static void Restore()
        {
            if (!swapped) return;
            swapped = false;
            try
            {
                if (swappedOn != null && original != null) swappedOn.SetTexture(mainGameTex, original);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[hd2d] restore: {e.Message}"); }
            swappedOn = null;
            original = null;
        }

        static void Process()
        {
            var comp = PostProcessLite.GetMaterial();
            if (comp == null) { CurrentMode = Mode.None; return; }
            var src = comp.GetTexture(mainGameTex);
            if (src == null || (output != null && src.Pointer == output.Pointer)) { CurrentMode = Mode.None; return; }

            var mode = Classify(src);
            CurrentMode = mode;
            bool selfTestNow = Settings.SelfTestSeconds > 0 && !selfTested && Time.realtimeSinceStartup > 8f;
            bool forced = false;
            if (mode == Mode.None && selfTestNow) { mode = Mode.Field; forced = true; }
            if (mode == Mode.None) return;
            var p = Settings.Profiles[(int)mode];
            if (p == null || !p.Enabled) return;

            var view = new Vector4(0, 1, 0, 1);
            if (comp.HasProperty(mainVH))
                view = new Vector4(comp.GetFloat(mainUL), comp.GetFloat(mainUH), comp.GetFloat(mainVL), comp.GetFloat(mainVH));
            if (view.y - view.x < 0.05f || view.w - view.z < 0.05f) view = new Vector4(0, 1, 0, 1);
            lastView = view;

            UpdateFocus(src, p, mode, view);
            var prev = RenderTexture.active;
            // the field drawn at a higher resolution this frame (Diorama), if it was
            Texture source = mode == Mode.Field && Diorama.HiResFrame == Time.frameCount && Diorama.HiRes != null
                ? Diorama.HiRes : src;
            Render(source, p, view, mode);
            if (selfTestNow)
            {
                selfTested = true;
                traceFrame = Time.frameCount + 1;
                Plugin.L.LogInfo($"[hd2d] self-test on '{src.name}' as {mode}{(forced ? " (forced)" : "")}, focus {focusY:0.##}:\n" +
                                 $"  source {Probe(src)}\n  output {Probe(output)}");
            }
            RenderTexture.active = prev;

            comp.SetTexture(mainGameTex, output);
            swappedOn = comp;
            original = src;
            swapped = true;
            lastMode = mode;
            lastW = src.width;
            lastH = src.height;
        }

        static Mode Classify(Texture tex)
        {
            if (Same(tex, FrontRenderTarget.Type.kField)) return Mode.Field;
            if (Same(tex, FrontRenderTarget.Type.kBirdView)) return Mode.WorldMap;
            if (Same(tex, FrontRenderTarget.Type.kBattle)) return Mode.Battle;
            // a snapshot of the scene (transition): same size, same look
            if (lastMode != Mode.None && tex.width == lastW && tex.height == lastH) return lastMode;
            Once($"other:{tex.name}:{tex.width}x{tex.height}",
                 () => $"[hd2d] main image '{tex.name}' {tex.width}x{tex.height} isn't the field, world map or battle: left as is");
            return Mode.None;
        }

        static bool Same(Texture tex, FrontRenderTarget.Type t)
        {
            var rt = FrontRenderTarget.GetRenderTex(t);
            return rt != null && tex != null && rt.Pointer == tex.Pointer;
        }

        // ---- the effect ----

        static RenderTexture Temp(int w, int h, RenderTextureFormat f)
        {
            var t = RenderTexture.GetTemporary(Math.Max(1, w), Math.Max(1, h), 0, f);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            temps.Add(t);
            return t;
        }

        static void ReleaseTemps()
        {
            foreach (var t in temps) RenderTexture.ReleaseTemporary(t);
            temps.Clear();
        }

        static void EnsureOutput(int w, int h)
        {
            if (output != null && output.width == w && output.height == h && output.IsCreated()) return;
            if (output != null) { output.Release(); Object.Destroy(output); }
            output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "HD2D Output",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            output.Create();
        }

        // The blur pyramid: four 13-tap halvings of `src` (a level too small to halve repeats).
        static void Pyramid(Texture src)
        {
            Texture prev = src;
            for (int i = 0; i < Levels; i++)
            {
                if (prev.width > 2 && prev.height > 2)
                {
                    var t = Temp(prev.width / 2, prev.height / 2, levelFormat);
                    Graphics.Blit(prev, t, mat, PassDown);
                    prev = t;
                }
                mat.SetTexture(idL[i], prev);
            }
        }

        static void Render(Texture src, Profile p, Vector4 view, Mode mode)
        {
            int w = src.width, h = src.height;
            float visV = view.w - view.z;
            bool crt = false;
            try { crt = PostProcessLite.GetFakeCRT(); } catch { }
            var (ow, oh) = crt ? (w, h) : Hd2dMath.OutputSize(w, h, Screen.height, visV, Settings.OutputHeight);
            EnsureOutput(ow, oh);
            try
            {
                // 1. the pixel art at about screen size, so blur sizes mean the same everywhere
                Texture full = src;
                if (ow != w || oh != h)
                {
                    var u = Temp(ow, oh, RenderTextureFormat.ARGB32);
                    var scale = Settings.SharpUpscale ? new Vector4((float)ow / w, (float)oh / h, 0, 0) : new Vector4(1, 1, 0, 0);
                    mat.SetVector(idSharpScale, scale);
                    Graphics.Blit(src, u, mat, PassUpscale);
                    full = u;
                }
                // 2. blur pyramid, 3. everything else
                Pyramid(full);
                float levelOffset = Hd2dMath.LevelOffset(Screen.height, full.height * visV);
                bool backdropDone = mode == Mode.Battle && backdropFrame >= Time.frameCount - 1;
                float top = backdropDone ? 0 : p.BlurTop, bottom = backdropDone ? 0 : p.BlurBottom;
                float aspect = (float)Screen.width / Math.Max(1, Screen.height);

                mat.SetVector(idView, view);
                mat.SetVector(idFocus, new Vector4(focusY, p.FocusWidth, p.FalloffTop, p.FalloffBottom));
                mat.SetVector(idBlur, new Vector4(top, bottom, levelOffset, Settings.DebugBlur ? 1 : 0));
                bool hazeHere = mode != Mode.Battle;
                mat.SetVector(idHaze, new Vector4(hazeHere ? Clamp01(p.Haze) : 0, p.HazeStart, 0, 0));
                mat.SetVector(idHazeColor, V(p.HazeColor));
                mat.SetVector(idBloom, new Vector4(Mathf.Clamp(p.BloomThreshold, 0, 0.99f), Math.Max(0, p.Bloom), 0, 0));
                mat.SetVector(idMotes, new Vector4(Math.Max(0, p.Motes), 0, 0, 0));
                mat.SetVector(idMotesColor, V(p.MotesColor));
                mat.SetVector(idShadows, V(p.Shadows));
                mat.SetVector(idHighlights, V(p.Highlights));
                mat.SetVector(idGrade, new Vector4(Clamp01(p.Contrast), p.Saturation, p.Exposure, Clamp01(p.Vignette)));
                mat.SetVector(idAspect, new Vector4(aspect, 0, 0, 0));
                Graphics.Blit(full, output, mat, PassFinal);

                Once($"mode:{mode}:{w}x{h}:{ow}x{oh}", () =>
                    $"[hd2d] {mode}: '{src.name}' {w}x{h} filter={src.filterMode} -> {ow}x{oh} " +
                    $"(screen {Screen.width}x{Screen.height}, visible uv x {view.x:0.###}-{view.y:0.###} y {view.z:0.###}-{view.w:0.###}, " +
                    $"level offset {levelOffset:0.##}, fake CRT {crt}" +
                    (mode == Mode.Battle ? $", backdrop blurred separately: {backdropDone}" : "") + ")");
                if ((mode == Mode.Field || mode == Mode.Battle) && traced.Add(mode)) traceFrame = Time.frameCount + 1;
            }
            finally { ReleaseTemps(); }
        }

        // ---- battle backdrop, blurred in place before the characters are drawn ----

        static bool IsBackdropCamera(Camera cam)
        {
            var ptr = cam.Pointer;
            if (backdropCams.TryGetValue(ptr, out bool b)) return b;
            var name = cam.name ?? "";
            b = name.IndexOf("BattlebackGround", StringComparison.OrdinalIgnoreCase) >= 0;
            backdropCams[ptr] = b;
            return b;
        }

        static void OnPostRender(Camera cam)
        {
            if (cam == null) return;
            try { Diorama.PostRender(cam); }   // always: puts back what PreCull changed
            catch (Exception e) { Fail(e); }
            try
            {
                if (!Settings.Enabled || errors > 20) return;
                if (!IsBackdropCamera(cam)) return;
                var rt = cam.targetTexture;
                if (rt == null) return;
                BackdropTexture = rt;
                bool battleRt = Same(rt, FrontRenderTarget.Type.kBattle);
                if (!battleRt && CurrentMode != Mode.Battle) return;
                var p = Settings.Profiles[(int)Mode.Battle];
                if (p == null || !p.Enabled) return;
                var prev = RenderTexture.active;
                try
                {
                    Pyramid(rt);
                    float levelOffset = Hd2dMath.LevelOffset(Screen.height, rt.height * (lastView.w - lastView.z));
                    mat.SetVector(idView, lastView);
                    mat.SetVector(idBlur, new Vector4(0, 0, levelOffset, Settings.DebugBlur ? 1 : 0));
                    mat.SetVector(idSet, new Vector4(p.UnitsBottom, p.UnitsTop, p.Horizon, 0));
                    mat.SetVector(idSetBlur, new Vector4(p.NearBlur, p.FarBlur, Clamp01(p.WallHaze), p.WallHazeAuto ? 1 : 0));
                    mat.SetVector(idHazeColor, V(p.HazeColor));
                    var tmp = Temp(rt.width, rt.height, rt.format);
                    Graphics.Blit(rt, tmp, mat, PassBackdrop);
                    Graphics.Blit(tmp, rt);
                }
                finally
                {
                    ReleaseTemps();
                    RenderTexture.active = prev;
                }
                backdropFrame = Time.frameCount;
                Once("backdrop:" + rt.name, () =>
                    $"[hd2d] battle backdrop: '{cam.name}' (depth {cam.depth}) -> '{rt.name}' {rt.width}x{rt.height} " +
                    $"{rt.format}, {(battleRt ? "the battle image itself" : "its own texture")}; blurred before the characters");
            }
            catch (Exception e) { Fail(e); }
        }

        // ---- focus band ----

        static void UpdateFocus(Texture src, Profile p, Mode mode, Vector4 view)
        {
            float target = p.FocusCenter;
            // world map: where the leader is in the tilted view this frame (projected through the
            // real camera while it was tilted), if that's on screen
            if (mode == Mode.WorldMap && Diorama.WorldFocusFrame == Time.frameCount)
            {
                float wy = (Diorama.WorldFocusY - view.z) / Math.Max(view.w - view.z, 1e-4f);
                if (Hd2dMath.Plausible(wy)) target = Mathf.Clamp(wy + p.FocusOffset, 0.12f, 0.88f);
            }
            else if (p.FollowPlayer && (mode == Mode.Field || mode == Mode.WorldMap) &&
                PlayerTracker.TryScreenY(src, view, out float y))
            {
                if (mode == Mode.Field) y = Diorama.TiltedViewportY(y);
                Once($"follow:{mode}", () => $"[hd2d] {mode}: player on screen at height {y:0.##} (0 bottom, 1 top)");
                if (Hd2dMath.Plausible(y)) target = Mathf.Clamp(y + p.FocusOffset, 0.12f, 0.88f);
            }
            float now = Time.unscaledTime;
            float dt = now - lastTime;
            lastTime = now;
            if (focusY < 0f || mode != lastMode || dt > 0.5f) focusY = target;
            else focusY = Hd2dMath.Damp(focusY, target, 6f, dt);
        }

        // ---- self-test readback ----

        // Mean colour, and detail (mean step between neighbouring pixels) in the top fifth
        // and the middle fifth of a 160x90 copy: the tilt-shift should cut the top's detail
        // and keep the middle's.
        static string Probe(Texture t)
        {
            const int W = 160, H = 90;
            var small = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(t, small);
            var prev = RenderTexture.active;
            RenderTexture.active = small;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(small);
            var px = tex.GetPixels32();
            Object.Destroy(tex);
            double r = 0, g = 0, b = 0;
            int magenta = 0;
            var lum = new float[W * H];
            for (int i = 0; i < W * H; i++)
            {
                var c = px[i];
                r += c.r; g += c.g; b += c.b;
                if (c.r > 240 && c.b > 240 && c.g < 20) magenta++;
                lum[i] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }
            int n = W * H;
            string Detail(int y0, int y1)
            {
                double d = 0; int k = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x + 1 < W; x++) { d += Math.Abs(lum[y * W + x + 1] - lum[y * W + x]); k++; }
                return (d / Math.Max(k, 1)).ToString("0.00");
            }
            return $"mean rgb {r / n:0} {g / n:0} {b / n:0}, detail top {Detail(H * 4 / 5, H)} middle {Detail(H * 2 / 5, H * 3 / 5)}" +
                   (magenta > n / 10 ? $", {magenta} magenta pixels (shader error colour!)" : "");
        }

        // ---- helpers ----

        static Vector4 V(Rgb c) => new Vector4(c.R, c.G, c.B, 1);
        static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

        internal static void Once(string key, Func<string> msg)
        {
            if (logged.Count > 200 || !logged.Add(key)) return;
            Plugin.L.LogInfo(msg());
        }

        internal static void FlushTrace()
        {
            if (traceFrame < 0 || Time.frameCount <= traceFrame || trace.Length == 0) return;
            Plugin.L.LogInfo($"[hd2d] camera order, frame {traceFrame}: {trace}");
            trace.Clear();
            traceFrame = -1;
        }
    }

    // Where the party leader is on screen, so the sharp band can follow them.
    static class PlayerTracker
    {
        static FieldMap fieldMap;
        static Transform player;
        static int layer = -1;
        static Camera cam;
        static IntPtr camFor;
        static int nextLookup, nextCam;

        internal static void Refresh()
        {
            if (Time.frameCount < nextLookup) return;
            nextLookup = Time.frameCount + 30;
            try
            {
                if (Hd2dRenderer.CurrentMode != Mode.Field && Hd2dRenderer.CurrentMode != Mode.WorldMap)
                {
                    player = null;
                    return;
                }
                if (fieldMap == null) fieldMap = Object.FindObjectOfType<FieldMap>();
                var p = fieldMap?.fieldController?.player;
                var t = p != null ? p.transform : null;
                if (t == null) { player = null; return; }
                if (player == null || t.Pointer != player.Pointer || t.gameObject.layer != layer)
                {
                    player = t;
                    layer = t.gameObject.layer;
                    cam = null;
                }
            }
            catch (Exception e)
            {
                player = null;
                Hd2dRenderer.Once("tracker:" + e.GetType().Name, () => $"[hd2d] player lookup: {e.Message}");
            }
        }

        internal static bool TryScreenY(Texture src, Vector4 view, out float y)
        {
            y = 0;
            if (player == null || layer < 0) return false;
            if (cam == null || camFor != src.Pointer || Time.frameCount >= nextCam)
            {
                FindCamera(src);
                nextCam = Time.frameCount + 120;
            }
            if (cam == null) return false;
            var vp = cam.WorldToViewportPoint(player.position);
            if (vp.z < 0) return false;
            var r = cam.rect;
            float v = r.y + vp.y * r.height;
            y = (v - view.z) / Math.Max(view.w - view.z, 1e-4f);
            return true;
        }

        // the highest camera that draws the player's layer into the main image
        static void FindCamera(Texture src)
        {
            cam = null;
            camFor = src.Pointer;
            Camera any = null;
            foreach (var c in Camera.allCameras)
            {
                var t = c.targetTexture;
                if (t == null || t.Pointer != src.Pointer) continue;
                if (any == null || c.depth > any.depth) any = c;
                if ((c.cullingMask & (1 << layer)) == 0) continue;
                if (cam == null || c.depth > cam.depth) cam = c;
            }
            if (cam == null) cam = any;
            var found = cam;
            Hd2dRenderer.Once($"cam:{src.name}:{found?.name}:{layer}", () =>
                $"[hd2d] focus follows the player (layer {layer}) through camera '{found?.name ?? "none"}'" +
                (found != null ? $" (ortho {found.orthographic}, rect {found.rect})" : " - band stays at FocusCenter"));
        }
    }

    static class FrameEnd
    {
        // the game's texture goes back after every frame's rendering
        internal static IEnumerator Loop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                Hd2dRenderer.Restore();
                Diorama.RestoreBird();
            }
        }
    }

    // Per-frame driver: keys, player lookup, end-of-frame restore of the game's texture.
    public class Hd2dRunner : MonoBehaviour
    {
        public Hd2dRunner(IntPtr ptr) : base(ptr) { }
        bool quitting;

        void Start()
        {
            this.StartCoroutine(FrameEnd.Loop().WrapToIl2Cpp());
        }

        void Update()
        {
            Hd2dRenderer.Restore();
            Diorama.RestoreBird();
            Hd2dRenderer.FlushTrace();
            if (Settings.SelfTestSeconds > 0 && Time.realtimeSinceStartup > Settings.SelfTestSeconds && !quitting)
            {
                quitting = true;
                Plugin.L.LogInfo($"[hd2d] self-test: quitting after {Settings.SelfTestSeconds:0} s");
                Application.Quit();
            }
            try
            {
                if (Input.GetKeyDown(Settings.Toggle))
                {
                    Settings.Enabled = !Settings.Enabled;
                    Plugin.L.LogInfo($"HD-2D {(Settings.Enabled ? "on" : "off")}");
                }
                else if (Input.GetKeyDown(Settings.Reload))
                {
                    bool on = Settings.Enabled;
                    Settings.ReloadFile();
                    Settings.Enabled = on;
                    Plugin.L.LogInfo("HD-2D settings reloaded");
                }
            }
            catch (Exception e) { Hd2dRenderer.Once("keys", () => $"[hd2d] keys: {e.Message}"); }
            PlayerTracker.Refresh();
            Diorama.Scan(Hd2dRenderer.CurrentMode);
            if (Time.frameCount % 600 == 300) Diorama.Report(Hd2dRenderer.CurrentMode);
        }
    }
}

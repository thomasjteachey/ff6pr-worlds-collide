using System;
using System.Collections.Generic;
using Last.Battle;
using Last.Camera;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hd2d
{
    // The field as a diorama: drawn at a higher resolution, its ground tilted away from the
    // camera (a keystone projection that keeps the game's 2D depth layering), characters
    // standing upright on it, and shadows under every character on the field and in battle.
    //
    // What the game draws (offline scan of the bundles + v0.1/v0.2 logs):
    //  field: CameraTileMap (ortho, half-height 90) then CameraFieldMain into the 320x180
    //    'Field' texture. Queues: ground tiles AlphaTest (opaque, write depth), the game's own
    //    shadow sprites AlphaTest+2, objects +3, characters +5 (two SpriteRenderers each,
    //    'VisualInstanceBody' and 'VisualInstanceHead', material FieldCharaEntityBody/Head,
    //    shader Last/Sprites/FieldCharaEntityShader; the head is queue 2457), upper tiles +6.
    //  battle: BattleSpriteEntity.CharacterSR (EntityMeshRenderer: MeshRenderer + MeshFilter,
    //    material LastMaterial, queue 3000) per unit; it also has a 'shadow' EntityMeshRenderer
    //    of the game's own (logged, left alone). CameraBattle draws them into 'Battle'.
    static class Diorama
    {
        const int FieldShadowQueue = 2452;   // AlphaTest+2, where the game's own shadows go

        static Shader shadowShader;
        static int idGround, idShadowColor, idContact, idMainTex;
        static Mesh quad;
        static GameObject root;

        // field render target redirect + tilt, per camera for the duration of its render
        static RenderTexture hiRes;
        static readonly Dictionary<IntPtr, RenderTexture> redirected = new Dictionary<IntPtr, RenderTexture>();
        static readonly HashSet<IntPtr> tilted = new HashSet<IntPtr>();
        static bool hiResBroken, hiResChecked;
        internal static int HiResFrame = -1;
        internal static RenderTexture HiRes => hiRes;
        static float[,] lastKeystone;
        static float lastSize;
        static int lastKeystoneFrame = -1;

        // A height on the flat (orthographic) field view, 0..1, as the tilted view shows it.
        internal static float TiltedViewportY(float flatY)
        {
            if (lastKeystone == null || Time.frameCount - lastKeystoneFrame > 2) return flatY;
            float viewY = (flatY - 0.5f) * 2f * lastSize;
            return (Hd2dMath.KeystoneScreenY(lastKeystone, viewY) + 1f) * 0.5f;
        }

        // characters
        static readonly Dictionary<IntPtr, FieldChar> fieldChars = new Dictionary<IntPtr, FieldChar>();
        static readonly Dictionary<IntPtr, BattleUnit> battleUnits = new Dictionary<IntPtr, BattleUnit>();
        static readonly Dictionary<IntPtr, bool> charMaterial = new Dictionary<IntPtr, bool>();
        static readonly List<(Transform t, Vector3 pos, Vector3 scale)> stood = new List<(Transform, Vector3, Vector3)>();
        static int nextScan;
        static int battleQueue = 3000;
        static bool battleQueueChecked, battleSameQueue = true;

        internal static void Load(AssetBundle bundle)
        {
            foreach (var o in bundle.LoadAllAssets(Il2CppInterop.Runtime.Il2CppType.Of<Shader>()))
            {
                var s = o?.TryCast<Shader>();
                if (s != null && s.name == "Hidden/HD2D/Shadow") shadowShader = s;
            }
            if (shadowShader == null || !shadowShader.isSupported)
            {
                Plugin.L.LogWarning("no usable Hidden/HD2D/Shadow shader in hd2d.bundle: shadows are off");
                shadowShader = null;
            }
            else shadowShader.hideFlags = HideFlags.DontUnloadUnusedAsset;
            idGround = Shader.PropertyToID("_Ground");
            idShadowColor = Shader.PropertyToID("_ShadowColor");
            idContact = Shader.PropertyToID("_Contact");
            idMainTex = Shader.PropertyToID("_MainTex");
        }

        static GameObject Root()
        {
            if (root != null) return root;
            root = new GameObject("HD2D Shadows");
            Object.DontDestroyOnLoad(root);
            return root;
        }

        static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "HD2D contact quad", hideFlags = HideFlags.HideAndDontSave };
            quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            quad.RecalculateBounds();
            return quad;
        }

        static Material NewShadowMaterial(int queue, bool contact)
        {
            var m = new Material(shadowShader) { name = contact ? "HD2D contact shadow" : "HD2D cast shadow", hideFlags = HideFlags.HideAndDontSave };
            m.renderQueue = queue;
            m.SetFloat(idContact, contact ? 1 : 0);
            return m;
        }

        // ---- camera hooks (called for every camera) ----

        // The field cameras by name (v0.3 matched them by target texture; that held at boot but
        // not once a save was loaded, so nothing ran in play).
        static readonly Dictionary<IntPtr, bool> fieldCams = new Dictionary<IntPtr, bool>();
        static readonly Dictionary<IntPtr, int> fieldDone = new Dictionary<IntPtr, int>();
        static readonly Dictionary<IntPtr, int> battleDone = new Dictionary<IntPtr, int>();

        static bool IsFieldCamera(Camera cam)
        {
            var ptr = cam.Pointer;
            if (fieldCams.TryGetValue(ptr, out bool b)) return b;
            var n = cam.name ?? "";
            b = n == "CameraTileMap" || n == "CameraFieldMain";
            fieldCams[ptr] = b;
            return b;
        }

        // what happened in the field since the last status line (Report)
        internal static int StatRenders, StatRedirects, StatTilts, StatChars, StatStood, StatShadows, StatBattleShadows;

        // by name, or (v0.3's rule) an orthographic camera drawing into the game's field texture
        static bool FieldCamera(Camera cam)
        {
            if (IsFieldCamera(cam)) return true;
            var t = cam.targetTexture;
            var field = FrontRenderTarget.GetRenderTex(FrontRenderTarget.Type.kField);
            return t != null && field != null && t.Pointer == field.Pointer && cam.orthographic;
        }

        internal static void PreCull(Camera cam)
        {
            if (!Settings.Enabled || cam == null) return;
            if (FieldCamera(cam)) { TryField(cam, true); return; }
            if (IsBirdCamera(cam)) { WorldPreCull(cam); return; }
            TryBattle(cam);
        }

        // Unity takes a new target only if it hasn't bound the old one yet: check it did.
        // Also the fallback for a field camera whose onPreCull didn't come.
        internal static void PreRender(Camera cam)
        {
            if (Settings.Enabled && FieldCamera(cam) &&
                (!fieldDone.TryGetValue(cam.Pointer, out int f) || f != Time.frameCount))
            {
                Hd2dRenderer.Once("prerender:" + cam.name, () => $"[hd2d] '{cam.name}': no pre-cull call, applying at pre-render");
                TryField(cam, false);
            }
            if (!Settings.Enabled || battleDone.TryGetValue(cam.Pointer, out int bf) && bf == Time.frameCount) { }
            else TryBattle(cam);
            if (hiResChecked || !redirected.ContainsKey(cam.Pointer)) return;
            hiResChecked = true;
            var active = RenderTexture.active;
            var at = cam.activeTexture;
            bool ok = (active != null && active.Pointer == hiRes.Pointer) || (at != null && at.Pointer == hiRes.Pointer);
            if (!ok) hiResBroken = true;
            Plugin.L.LogInfo($"[hd2d] field at {hiRes.width}x{hiRes.height}: " +
                             (ok ? "rendering into it" : $"Unity kept the game's target ('{active?.name}' / '{at?.name}'), so the field stays at the game's size"));
        }

        static void TryField(Camera cam, bool preCull)
        {
            var p = Settings.Profiles[(int)Mode.Field];
            if (p == null || !p.Enabled) return;
            var field = FrontRenderTarget.GetRenderTex(FrontRenderTarget.Type.kField);
            var target = cam.targetTexture;
            bool fieldTarget = target != null && field != null && target.Pointer == field.Pointer;
            bool fieldShown = Hd2dRenderer.CurrentMode == Mode.Field;
            Hd2dRenderer.Once($"fieldcam:{cam.name}:{target?.name}:{target?.width}:{fieldShown}", () =>
                $"[hd2d] field camera '{cam.name}': ortho {cam.orthographic}, size {cam.orthographicSize}, target " +
                (target == null ? "none (screen or set by buffers)" : $"'{target.name}' {target.width}x{target.height}") +
                $", game's field texture {(field == null ? "none" : $"'{field.name}' {field.width}x{field.height}")}, " +
                $"same: {fieldTarget}, field on screen: {fieldShown}, mask 0x{cam.cullingMask:x}");
            // towns and dungeons only: on the world map the field cameras keep drawing the (hidden)
            // field texture, and v0.3.2 put shadows there that the world map camera then also saw
            var mode = Hd2dRenderer.CurrentMode;
            if (!cam.orthographic || !(fieldShown || (fieldTarget && mode == Mode.None))) return;
            fieldDone[cam.Pointer] = Time.frameCount;
            StatRenders++;
            // the higher resolution needs the target swapped before Unity binds it: pre-cull only
            FieldPreCull(cam, field, p, preCull && fieldTarget);
        }

        static void TryBattle(Camera cam)
        {
            var b = Settings.Profiles[(int)Mode.Battle];
            if (b == null || !b.Enabled || battleUnits.Count == 0) return;
            var target = cam.targetTexture;
            var battle = FrontRenderTarget.GetRenderTex(FrontRenderTarget.Type.kBattle);
            if (target == null || battle == null || target.Pointer != battle.Pointer) return;
            if (battleDone.TryGetValue(cam.Pointer, out int f) && f == Time.frameCount) return;
            battleDone[cam.Pointer] = Time.frameCount;
            BattleSync(cam, b);
        }

        static int reports;
        internal static void Report(Mode mode)
        {
            if (reports >= 8) return;
            if (StatRenders + StatBattleShadows + StatBirdTilts + StatWorldShadows == 0 && mode != Mode.Field) return;
            reports++;
            Plugin.L.LogInfo($"[hd2d] status ({mode}): field renders {StatRenders}, at high resolution {StatRedirects}, tilted {StatTilts}; " +
                             $"characters seen {StatChars}, stood up {StatStood}, with shadows {StatShadows}; battle unit shadows {StatBattleShadows}; " +
                             $"world map tilted {StatBirdTilts}, world map player shadows {StatWorldShadows}; " +
                             $"field characters known {fieldChars.Count}, battle units known {battleUnits.Count}");
            StatBirdTilts = StatWorldShadows = 0;
            if (mode == Mode.Field && StatRenders == 0)
            {
                // no field camera recognised while the field is on screen: list what renders
                var field = FrontRenderTarget.GetRenderTex(FrontRenderTarget.Type.kField);
                var list = new List<string>();
                foreach (var c in Camera.allCameras)
                {
                    var t = c.targetTexture;
                    list.Add($"{c.name}(d{c.depth}, ortho {c.orthographic}, -> {(t == null ? "screen" : $"{t.name} {t.width}x{t.height}{(field != null && t.Pointer == field.Pointer ? " =field" : "")}")})");
                }
                Plugin.L.LogInfo($"[hd2d] no field camera found; cameras now: {string.Join(" ", list)}; game's field texture " +
                                 (field == null ? "none" : $"'{field.name}' {field.width}x{field.height}"));
            }
            StatRenders = StatRedirects = StatTilts = StatChars = StatStood = StatShadows = StatBattleShadows = 0;
        }

        // ---- world map (the bird view) ----
        //
        // On foot the world map is drawn in two steps (level0 + the v0.4.0 log): CameraBirdViewRT,
        // orthographic like the town camera, draws the flat map with the party leader into a
        // texture; CameraBirdView (perspective, under Cameras/BirdView/CameraParent) draws that
        // texture onto the 3D ground mesh (shader Unlit/FogTexShader), with the skybox and puppets.
        // v0.4.0 asked BirdViewController.RotateCamera for the airship's tilt, but never found the
        // controller, and put the leader's shadow in the 3D scene, where the leader isn't.
        // Now: the 3D camera (and the screen-effect camera parented to it) pivots around the ground
        // point at the centre of the screen, only while it looks straight down (on foot; the
        // airship tilts it itself), and goes back at the end of the frame. The leader's shadow
        // is drawn by the flat camera, into the map texture, so it lies on the tilted ground.

        static readonly Dictionary<IntPtr, int> birdCams = new Dictionary<IntPtr, int>();   // 1 3D, 2 flat map
        static readonly Dictionary<IntPtr, bool> fogMaterial = new Dictionary<IntPtr, bool>();
        static readonly Dictionary<IntPtr, bool> playerMaterial = new Dictionary<IntPtr, bool>();
        static MeshRenderer ground;
        static FieldMap worldFieldMap;
        static FieldChar worldPlayer;
        static IntPtr worldPlayerPtr;
        static int nextGroundLookup, nextWorldScan, birdTiltFrame = -1;
        static Transform birdCamT;
        static Vector3 birdCamPos;
        static Quaternion birdCamRot;
        internal static int StatBirdTilts, StatWorldShadows;
        internal static float WorldFocusY = -1f;
        internal static int WorldFocusFrame = -1;

        static int BirdKind(Camera cam)
        {
            var ptr = cam.Pointer;
            if (birdCams.TryGetValue(ptr, out int k)) return k;
            var n = cam.name ?? "";
            k = n == "CameraBirdView" || n == "CameraBirdViewScreenEffectRT" ? 1 : n == "CameraBirdViewRT" ? 2 : 0;
            birdCams[ptr] = k;
            return k;
        }

        static bool IsBirdCamera(Camera cam) => BirdKind(cam) != 0;

        static void WorldPreCull(Camera cam)
        {
            if (Hd2dRenderer.CurrentMode != Mode.WorldMap) return;
            var p = Settings.Profiles[(int)Mode.WorldMap];
            if (p == null || !p.Enabled) return;
            if (BirdKind(cam) == 2) { WorldPlayer(cam, p); return; }
            if (birdTiltFrame == Time.frameCount) return;   // the 3D camera and its child: once
            RestoreBird();
            if (p.GroundTiltAngle < 0.5f) return;
            // the 3D camera itself (the screen-effect camera is its child)
            var main = cam.name == "CameraBirdView" ? cam.transform : cam.transform.parent;
            if (main == null) return;
            var g = Ground(cam);
            if (g == null)
            {
                Hd2dRenderer.Once("noground", () => "[hd2d] world map: no ground mesh (Unlit/FogTexShader) found, no tilt");
                return;
            }
            var b = g.bounds;
            var ext = b.extents;
            var normal = ext.y <= ext.x && ext.y <= ext.z ? Vector3.up : ext.x <= ext.z ? Vector3.right : Vector3.forward;
            var fwd = main.forward;
            if (Vector3.Dot(normal, -fwd) < 0) normal = -normal;
            // the game already tilts the camera on foot (20 degrees in the v0.4.1 log); the setting is
            // the total, so add the difference, and only on foot (the airship tilts it itself)
            float off = Vector3.Angle(-fwd, normal);
            if (off < footAngle) footAngle = off;
            var bvc = BirdController();
            bool onFoot;
            string how;
            if (bvc != null)
            {
                onFoot = bvc.GetCurrentMode() == BirdViewController.ViewMode.None && !bvc.IsDuringTakeoffAndLanding;
                how = $"controller: {bvc.GetCurrentMode()}{(bvc.IsDuringTakeoffAndLanding ? ", taking off/landing" : "")}";
            }
            else
            {
                onFoot = off <= footAngle + 3f;
                how = $"no controller; on-foot angle {footAngle:0}";
            }
            float extra = p.GroundTiltAngle - off;
            if (!onFoot || extra < 0.5f)
            {
                Hd2dRenderer.Once($"birdskip:{onFoot}:{extra < 0.5f}", () =>
                    $"[hd2d] world map: camera at {off:0} degrees, {(onFoot ? $"already at or past the {p.GroundTiltAngle:0} asked for" : "not on foot")} ({how}): left as is");
                return;
            }
            var pos = main.position;
            float height = Vector3.Dot(pos - b.center, normal);
            float cosA = Vector3.Dot(-fwd, normal);
            if (height <= 0.01f || cosA <= 0.05f) return;
            float d = height / cosA;
            var focus = pos + fwd * d;
            var tilt = Quaternion.AngleAxis(-extra, main.right);
            birdCamT = main;
            birdCamPos = pos;
            birdCamRot = main.rotation;
            main.position = focus + tilt * (pos - focus);
            main.rotation = tilt * main.rotation;
            birdTiltFrame = Time.frameCount;
            StatBirdTilts++;
            var vp = cam.WorldToViewportPoint(focus);
            if (vp.z > 0) { WorldFocusY = vp.y; WorldFocusFrame = Time.frameCount; }
            float angle = p.GroundTiltAngle;
            Hd2dRenderer.Once("birdtilt", () =>
                $"[hd2d] world map tilt: game's {off:0} + {extra:0} = {angle:0} degrees ({how}); camera at {pos} looking {fwd}, " +
                $"ground '{g.name}' {b.center} size {b.size}, normal {normal}, height {height:0.#}, distance {d:0.#}, " +
                $"now at {main.position} looking {main.forward}, fov {cam.fieldOfView:0.#}; centre on screen at {vp.y:0.##}");
        }

        static float footAngle = float.MaxValue;
        static BirdViewController birdController;
        static int nextControllerLookup;

        // FindObjectOfType skips inactive objects (v0.4.0 never found it): look at all of them,
        // keeping one that belongs to a loaded scene
        static BirdViewController BirdController()
        {
            if (birdController != null) return birdController;
            if (Time.frameCount < nextControllerLookup) return null;
            nextControllerLookup = Time.frameCount + 300;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<BirdViewController>()))
                {
                    var c = o?.TryCast<BirdViewController>();
                    if (c != null && c.gameObject.scene.IsValid()) { birdController = c; break; }
                }
            }
            catch (Exception e) { Hd2dRenderer.Once("birdctl", () => $"[hd2d] world map controller lookup: {e.Message}"); }
            var found = birdController;
            Hd2dRenderer.Once("birdctl:" + (found != null), () =>
                found != null ? $"[hd2d] world map controller: '{found.name}' (active {found.isActiveAndEnabled})"
                              : "[hd2d] world map controller not found: telling flying from walking by the camera angle");
            return birdController;
        }

        // put the 3D camera back (end of frame, or the next frame's first bird view render)
        internal static void RestoreBird()
        {
            if (birdCamT == null) return;
            birdCamT.position = birdCamPos;
            birdCamT.rotation = birdCamRot;
            birdCamT = null;
        }

        static MeshRenderer Ground(Camera cam)
        {
            if (ground != null && ground.gameObject.activeInHierarchy) return ground;
            if (Time.frameCount < nextGroundLookup) return null;
            nextGroundLookup = Time.frameCount + 60;
            MeshRenderer best = null;
            float area = 0;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
            {
                var m = r?.sharedMaterial;
                if (m == null || (cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                if (!fogMaterial.TryGetValue(m.Pointer, out bool fog))
                    fogMaterial[m.Pointer] = fog = (m.shader?.name ?? "") == "Unlit/FogTexShader";
                if (!fog) continue;
                var s = r.bounds.size;
                float a = Math.Max(s.x * s.y, Math.Max(s.x * s.z, s.y * s.z));
                if (a > area) { area = a; best = r; }
            }
            ground = best;
            return ground;
        }

        static bool IsPlayerSprite(SpriteRenderer sr)
        {
            var m = sr?.sharedMaterial;
            if (m == null) return false;
            if (playerMaterial.TryGetValue(m.Pointer, out bool b)) return b;
            var n = m.shader?.name ?? "";
            b = (n.Contains("FieldCharaEntity") || n.Contains("FieldSpriteEntityShader")) && !n.Contains("Shadow");
            playerMaterial[m.Pointer] = b;
            return b;
        }

        // the leader's shadow, drawn by the flat map camera into the map texture
        static void WorldPlayer(Camera cam, Profile p)
        {
            if (Time.frameCount >= nextWorldScan)
            {
                nextWorldScan = Time.frameCount + 30;
                if (worldFieldMap == null) worldFieldMap = Object.FindObjectOfType<FieldMap>();
                var player = worldFieldMap?.fieldController?.player;
                if (player == null) { worldPlayer?.Hide(); return; }
                if (worldPlayer == null || player.Pointer != worldPlayerPtr)
                {
                    worldPlayer?.Destroy();
                    worldPlayer = new FieldChar(player.transform);
                    worldPlayerPtr = player.Pointer;
                }
                foreach (var sr in player.GetComponentsInChildren<SpriteRenderer>())
                    if (IsPlayerSprite(sr)) worldPlayer.Add(sr);
            }
            var c = worldPlayer;
            if (c == null) return;
            if (!c.Measure()) { c.Hide(); return; }
            if ((cam.cullingMask & (1 << c.Layer)) == 0) return;
            var vp = cam.WorldToViewportPoint(new Vector3(c.CentreX, c.Feet, c.Z));
            Hd2dRenderer.Once("worldplayer", () =>
                $"[hd2d] world map player: {c.Describe()}, z {c.Z:0.##}, in the map texture at {vp.x:0.##}, {vp.y:0.##}");
            if (shadowShader != null && (p.CastShadow > 0 || p.ContactShadow > 0)) { c.SyncShadow(p); StatWorldShadows++; }
            else c.Hide();
        }

        internal static void HideWorld() => worldPlayer?.Hide();

        internal static void PostRender(Camera cam)
        {
            var ptr = cam.Pointer;
            if (IsBirdCamera(cam)) return;   // the tilted 3D camera goes back at the end of the frame
            if (stood.Count > 0) RestoreStandUp();
            if (tilted.Remove(ptr)) cam.ResetProjectionMatrix();
            if (redirected.TryGetValue(ptr, out var field))
            {
                redirected.Remove(ptr);
                cam.targetTexture = field;
                if (!hiResBroken)
                {
                    // keep the game's own copy current (menu backgrounds, save screenshots, transitions)
                    var prev = RenderTexture.active;
                    Graphics.Blit(hiRes, field);
                    RenderTexture.active = prev;
                    HiResFrame = Time.frameCount;
                }
            }
        }

        static void FieldPreCull(Camera cam, RenderTexture field, Profile p, bool canRedirect)
        {
            int scale = hiResBroken || !canRedirect || field == null ? 1 : Hd2dMath.ResolutionScale(p.Resolution, Screen.height, field.height);
            if (scale > 1)
            {
                StatRedirects++;
                int w = field.width * scale, h = field.height * scale;
                if (hiRes == null || hiRes.width != w || hiRes.height != h || !hiRes.IsCreated())
                {
                    if (hiRes != null) { hiRes.Release(); Object.Destroy(hiRes); }
                    hiRes = new RenderTexture(w, h, 24, field.format)
                    {
                        name = "HD2D Field", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    hiRes.Create();
                }
                cam.targetTexture = hiRes;
                redirected[cam.Pointer] = field;
            }
            if (p.Tilt > 0.01f)
            {
                float aspect = field != null ? (float)field.width / field.height : cam.aspect;
                var m = Hd2dMath.Keystone(p.Tilt, p.Perspective, cam.orthographicSize, aspect, cam.nearClipPlane, cam.farClipPlane);
                cam.projectionMatrix = ToMatrix(m);
                tilted.Add(cam.Pointer);
                StatTilts++;
                lastKeystone = m;
                lastSize = cam.orthographicSize;
                lastKeystoneFrame = Time.frameCount;
            }
            FieldCharacters(cam, p);
        }

        static Matrix4x4 ToMatrix(float[,] m)
        {
            var r = new Matrix4x4();
            r.m00 = m[0, 0]; r.m01 = m[0, 1]; r.m02 = m[0, 2]; r.m03 = m[0, 3];
            r.m10 = m[1, 0]; r.m11 = m[1, 1]; r.m12 = m[1, 2]; r.m13 = m[1, 3];
            r.m20 = m[2, 0]; r.m21 = m[2, 1]; r.m22 = m[2, 2]; r.m23 = m[2, 3];
            r.m30 = m[3, 0]; r.m31 = m[3, 1]; r.m32 = m[3, 2]; r.m33 = m[3, 3];
            return r;
        }

        // ---- the scan (runner, every half second) ----

        internal static void Scan(Mode mode)
        {
            if (Time.frameCount < nextScan) return;
            nextScan = Time.frameCount + 30;
            try
            {
                if (!Settings.Enabled) { HideAll(); return; }
                if (mode == Mode.Field) ScanField(); else HideField();
                if (mode != Mode.WorldMap) HideWorld();
                else if (++layerScan % 10 == 1) AnalyzeLayers();   // the world map's own tile layers
                if (mode == Mode.Battle) ScanBattle(); else HideBattle();
            }
            catch (Exception e) { Hd2dRenderer.Once("scan:" + e.GetType().Name, () => $"[hd2d] character scan: {e}"); }
        }

        static void HideAll() { HideField(); HideBattle(); }

        static bool IsCharMaterial(Material m)
        {
            if (m == null) return false;
            if (charMaterial.TryGetValue(m.Pointer, out bool b)) return b;
            var s = m.shader;
            b = s != null && (s.name ?? "").Contains("FieldCharaEntity");
            charMaterial[m.Pointer] = b;
            return b;
        }

        static void ScanField()
        {
            var seen = new HashSet<IntPtr>();
            foreach (var sr in Object.FindObjectsOfType<SpriteRenderer>())
            {
                if (sr == null || !IsCharMaterial(sr.sharedMaterial)) continue;
                var parent = sr.transform.parent;
                if (parent == null) continue;
                var key = parent.Pointer;
                if (!fieldChars.TryGetValue(key, out var c)) fieldChars[key] = c = new FieldChar(parent);
                c.Add(sr);
                seen.Add(key);
            }
            foreach (var key in new List<IntPtr>(fieldChars.Keys))
                if (!seen.Contains(key)) { fieldChars[key].Destroy(); fieldChars.Remove(key); }
            if (fieldChars.Count > 0)
                Hd2dRenderer.Once("fieldchars", () =>
                {
                    foreach (var c in fieldChars.Values) if (c.Measure()) return $"[hd2d] field characters: {fieldChars.Count} ({c.Describe()})";
                    return $"[hd2d] field characters: {fieldChars.Count} (none visible yet)";
                });
            if (++layerScan % 10 == 1) AnalyzeLayers();
        }

        // ---- field tile layers: where the shadows go in the draw order ----
        //
        // v0.3.1 put field shadows at AlphaTest+2 like the game's own shadow sprites, and they
        // never showed: the floor is drawn in several tile layers, and the ones drawn after the
        // characters' step (characters stay on top of them by depth) painted over shadows that
        // write no depth. So the shadows now go right after the last tile layer behind the
        // character (larger z: the camera looks along +z) and before the ones in front of it.

        struct TileLayer { public float Z; public int Queue, SortingLayer, Order; public string Name; }
        static readonly List<TileLayer> tileLayers = new List<TileLayer>();
        static readonly Dictionary<int, (int queue, int sortingLayer, int order, float zOffset)> slots =
            new Dictionary<int, (int, int, int, float)>();
        static int layerScan;
        static string lastLayerSignature;

        static void AnalyzeLayers()
        {
            tileLayers.Clear();
            slots.Clear();
            foreach (var m in Object.FindObjectsOfType<MapLayerMesh>())
            {
                var r = m?.meshRenderer;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var mat = r.sharedMaterial;
                if (mat == null) continue;
                var data = m.layerData;
                tileLayers.Add(new TileLayer
                {
                    Z = r.transform.position.z, Queue = mat.renderQueue, SortingLayer = r.sortingLayerID, Order = r.sortingOrder,
                    Name = $"{data?.LayerName}#{data?.Layer} zOrder {data?.ZOrder} {(mat.shader?.name ?? "").Replace("Last/", "")}",
                });
            }
            tileLayers.Sort((a, b) => b.Z.CompareTo(a.Z));   // far to near
            var groups = new List<string>();
            foreach (var l in tileLayers)
            {
                var s = $"z {l.Z:0.##} q {l.Queue} order {l.SortingLayer}/{l.Order} {l.Name}";
                if (!groups.Contains(s)) groups.Add(s);
            }
            var sig = string.Join("; ", groups);
            if (sig == lastLayerSignature || groups.Count == 0) return;
            lastLayerSignature = sig;
            Plugin.L.LogInfo($"[hd2d] map tile layers (far to near): {(groups.Count > 30 ? string.Join("; ", groups.GetRange(0, 30)) + "; ..." : sig)}");
            LogCoverage();
        }

        static (int queue, int sortingLayer, int order, float zOffset) ShadowSlot(float charZ)
        {
            int key = (int)Math.Round(charZ * 100);
            if (slots.TryGetValue(key, out var s)) return s;
            int queue = FieldShadowQueue, sortingLayer = 0, order = 0;
            float nearestFloor = float.MaxValue;
            foreach (var l in tileLayers)
                if (l.Z > charZ + 1e-3f)
                {
                    nearestFloor = Math.Min(nearestFloor, l.Z);
                    if (l.Queue > queue) { queue = l.Queue; sortingLayer = l.SortingLayer; order = l.Order + 1; }
                    else if (l.Queue == queue && queue != FieldShadowQueue && l.Order + 1 > order) { sortingLayer = l.SortingLayer; order = l.Order + 1; }
                }
            // a hair farther than the character, so its own pixels (if it writes depth) stay on top
            float zOffset = nearestFloor < float.MaxValue ? Math.Min(0.01f, (nearestFloor - charZ) * 0.5f) : 0f;
            int clash = 0;
            foreach (var l in tileLayers)
                if (l.Z < charZ - 1e-3f && l.Queue == queue && l.Order <= order) clash++;
            s = (queue, sortingLayer, order, zOffset);
            slots[key] = s;
            var r = s;
            Hd2dRenderer.Once($"slot:{key}:{lastLayerSignature?.GetHashCode()}", () =>
                $"[hd2d] field shadows for characters at z {charZ:0.##}: queue {r.queue}, sorting {r.sortingLayer}/{r.order}, " +
                $"z +{r.zOffset:0.###}" + (clash > 0 ? $" ({clash} upper layers share that slot: shadows may show on them)" : ""));
            return s;
        }

        // How much of the map the tilted view shows, against what the game builds around the
        // camera (it only builds the cells in view).
        static void LogCoverage()
        {
            try
            {
                var p = Settings.Profiles[(int)Mode.Field];
                string built = "";
                foreach (var r in Object.FindObjectsOfType<Last.Map.Renderer.MainViewMapRenderer>())
                    built += $"'{r.name}' view {r.viewSize}, cells {r.MapCellHorizontalLength}x{r.MapCellVerticalLength}, extra {r.InsideViewCountUpNum}; ";
                string tilt = "flat";
                if (p != null && p.Tilt > 0.01f)
                {
                    var m = Hd2dMath.Keystone(p.Tilt, p.Perspective, 90, 320f / 180, 0.3f, 1000);
                    float top = 1f / (m[1, 1] - m[3, 1]), bottom = -1f / (m[1, 1] + m[3, 1]);
                    float wTop = 1 + m[3, 1] * top;
                    tilt = $"tilt {p.Tilt}/{p.Perspective} shows y {bottom:0}..+{top:0} and x +-{160 * wTop:0} at the top (flat: +-90, +-160)";
                }
                Plugin.L.LogInfo($"[hd2d] field coverage: {built}{tilt}");
            }
            catch (Exception e) { Hd2dRenderer.Once("coverage", () => $"[hd2d] coverage: {e.Message}"); }
        }

        static void HideField() { foreach (var c in fieldChars.Values) c.Hide(); }

        static void ScanBattle()
        {
            var seen = new HashSet<IntPtr>();
            foreach (var e in Object.FindObjectsOfType<BattleSpriteEntity>())
            {
                var body = e?.CharacterSR;
                if (body == null) continue;
                var key = e.Pointer;
                if (!battleUnits.TryGetValue(key, out var u)) battleUnits[key] = u = new BattleUnit(e);
                seen.Add(key);
            }
            foreach (var key in new List<IntPtr>(battleUnits.Keys))
                if (!seen.Contains(key)) { battleUnits[key].Destroy(); battleUnits.Remove(key); }
            if (!battleQueueChecked && battleUnits.Count > 0) PickBattleQueue();
        }

        static void HideBattle()
        {
            foreach (var u in battleUnits.Values) u.Hide();
            battleQueueChecked = false;
        }

        // Battle shadows go after the backdrop and before the units. Units are queue 3000
        // (LastMaterial); the backdrop is the renderer showing the backdrop camera's texture.
        static void PickBattleQueue()
        {
            battleQueueChecked = true;
            int unitQueue = 3000;
            foreach (var u in battleUnits.Values) { var q = u.BodyQueue(); if (q > 0) { unitQueue = q; break; } }
            int backdropQueue = -1;
            string backdrop = "not found";
            var tex = Hd2dRenderer.BackdropTexture;
            if (tex != null)
                foreach (var r in Object.FindObjectsOfType<Renderer>())
                {
                    var m = r?.sharedMaterial;
                    if (m == null || m.mainTexture == null || m.mainTexture.Pointer != tex.Pointer) continue;
                    backdropQueue = m.renderQueue;
                    backdrop = $"'{r.name}' ({r.GetIl2CppType().Name}, shader {m.shader?.name}, queue {backdropQueue}, z {r.transform.position.z:0.#})";
                    break;
                }
            // v0.3 logged the backdrop ('TextureRender', LastSpriteMaster) at queue 3000 like the
            // units, so a queue below 3000 drew the shadows before the backdrop covered them. In
            // the same queue Unity sorts by sorting layer/order, then far to near: the shadows
            // take each unit's sorting order and sit a little farther from the camera than it.
            battleSameQueue = !(backdropQueue >= 0 && backdropQueue + 1 < unitQueue);
            battleQueue = battleSameQueue ? unitQueue : backdropQueue + 1;
            int q2 = battleQueue;
            bool same = battleSameQueue;
            foreach (var u in battleUnits.Values) u.SetQueue(q2);
            Hd2dRenderer.Once("battlequeue:" + backdrop, () =>
            {
                string sample = "";
                foreach (var u in battleUnits.Values) { sample = u.Describe(); break; }
                return $"[hd2d] battle: {battleUnits.Count} units ({sample}); backdrop {backdrop}; shadows at queue {q2}" +
                       (same ? " with the units' sorting, just behind each unit" : "") + $", units {unitQueue}";
            });
        }

        // ---- per render ----

        static void FieldCharacters(Camera cam, Profile p)
        {
            if (fieldChars.Count == 0) return;
            int mask = cam.cullingMask;
            float camY = cam.transform.position.y;
            bool shadows = shadowShader != null && (p.CastShadow > 0 || p.ContactShadow > 0);
            foreach (var c in fieldChars.Values)
            {
                if (!c.Measure()) { c.Hide(); continue; }
                if ((mask & (1 << c.Layer)) == 0) continue;   // not the camera that draws the characters
                StatChars++;
                if (shadows) { c.SyncShadow(p); StatShadows++; }
                else c.Shadow.Hide();
                if (p.StandUp && p.Tilt > 0.01f)
                {
                    StatStood++;
                    float s = Hd2dMath.StandUpScale(p.Tilt, p.Perspective, cam.orthographicSize, c.Feet - camY);
                    foreach (var t in c.Tops())
                    {
                        stood.Add((t, t.localPosition, t.localScale));
                        var pos = t.position;
                        pos.y = c.Feet + (pos.y - c.Feet) * s;
                        t.position = pos;
                        var ls = t.localScale;
                        ls.y *= s;
                        t.localScale = ls;
                    }
                }
            }
        }

        static void RestoreStandUp()
        {
            foreach (var (t, pos, scale) in stood)
            {
                if (t == null) continue;
                t.localPosition = pos;
                t.localScale = scale;
            }
            stood.Clear();
        }

        static void BattleSync(Camera cam, Profile p)
        {
            if (battleUnits.Count == 0) return;
            bool shadows = shadowShader != null && (p.CastShadow > 0 || p.ContactShadow > 0);
            var behind = battleSameQueue ? cam.transform.forward * 0.5f : Vector3.zero;
            foreach (var u in battleUnits.Values)
            {
                if (shadows && u.Measure()) { u.Sync(p, battleQueue, behind); StatBattleShadows++; }
                else u.Hide();
            }
        }

        // ---- shadows ----

        // A cast shadow per body part (sharing one material) and a contact ellipse.
        internal sealed class ShadowSet
        {
            readonly List<SpriteRenderer> sprites = new List<SpriteRenderer>();
            MeshFilter castMeshFilter;
            MeshRenderer castMesh;
            MeshRenderer contact;
            Material cast, spot;
            int queue = -1;

            void EnsureMaterials(int q)
            {
                if (cast == null) cast = NewShadowMaterial(q, false);
                if (spot == null) spot = NewShadowMaterial(q, true);
                if (queue != q) { cast.renderQueue = q; spot.renderQueue = q; queue = q; }
            }

            MeshRenderer Contact()
            {
                if (contact != null) return contact;
                var go = new GameObject("HD2D contact shadow");
                go.transform.SetParent(Root().transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Quad();
                contact = go.AddComponent<MeshRenderer>();
                contact.sharedMaterial = spot;
                return contact;
            }

            internal void SyncSprites(List<SpriteRenderer> parts, float feet, float height, float alpha, float centreX, float width,
                                      float z, int layer, Profile p, int q, int sortingLayer, int sortingOrder, float zOffset)
            {
                EnsureMaterials(q);
                while (sprites.Count < parts.Count)
                {
                    var go = new GameObject("HD2D cast shadow");
                    go.transform.SetParent(Root().transform, false);
                    var r = go.AddComponent<SpriteRenderer>();
                    r.sharedMaterial = cast;
                    sprites.Add(r);
                }
                bool castOn = p.CastShadow > 0 && p.ShadowLength > 0.001f;
                for (int i = 0; i < sprites.Count; i++)
                {
                    var r = sprites[i];
                    var src = i < parts.Count ? parts[i] : null;
                    bool on = castOn && src != null && src.enabled && src.sprite != null;
                    r.gameObject.SetActive(on);
                    if (!on) continue;
                    r.sprite = src.sprite;
                    r.flipX = src.flipX;
                    r.flipY = src.flipY;
                    Copy(src.transform, r.transform);
                    r.transform.position += new Vector3(0, 0, zOffset);
                    r.gameObject.layer = layer;
                    r.sortingLayerID = sortingLayer;
                    r.sortingOrder = sortingOrder;
                }
                cast.SetVector(idGround, new Vector4(feet, p.ShadowSlant, p.ShadowLength, Math.Max(height, 1e-3f)));
                cast.SetColor(idShadowColor, new Color(0, 0, 0, Clamp01(p.CastShadow) * alpha));
                var c = Contact();
                c.sortingLayerID = sortingLayer;
                c.sortingOrder = sortingOrder;
                PlaceContact(feet, centreX, width, z + zOffset, layer, p, alpha, 0, 0.85f);
            }

            internal void SyncMesh(Mesh mesh, Texture tex, Transform src, float ground, float height, float lift, float alpha,
                                   float centreX, float width, float z, int layer, Profile p, int q,
                                   int sortingLayer, int sortingOrder, Vector3 behind, float castStrength, float contactWidth)
            {
                EnsureMaterials(q);
                if (castMesh == null)
                {
                    var go = new GameObject("HD2D cast shadow");
                    go.transform.SetParent(Root().transform, false);
                    castMeshFilter = go.AddComponent<MeshFilter>();
                    castMesh = go.AddComponent<MeshRenderer>();
                    castMesh.sharedMaterial = cast;
                }
                castMesh.sortingLayerID = sortingLayer;
                castMesh.sortingOrder = sortingOrder;
                var c = Contact();
                c.sortingLayerID = sortingLayer;
                c.sortingOrder = sortingOrder;
                // high in the air (a Jump) the silhouette would stretch far across the floor
                float castLift = Clamp01(1f - lift / Math.Max(height * 0.8f, 1e-3f));
                bool on = castStrength > 0 && p.ShadowLength > 0.001f && mesh != null && castLift > 0;
                castMesh.gameObject.SetActive(on);
                if (on)
                {
                    castMeshFilter.sharedMesh = mesh;
                    if (tex != null) cast.SetTexture(idMainTex, tex);
                    Copy(src, castMesh.transform);
                    castMesh.transform.position += behind;
                    castMesh.gameObject.layer = layer;
                    cast.SetVector(idGround, new Vector4(ground, p.ShadowSlant, p.ShadowLength, Math.Max(height, 1e-3f)));
                    cast.SetColor(idShadowColor, new Color(0, 0, 0, Clamp01(castStrength) * alpha * castLift));
                }
                PlaceContact(ground, centreX, width, z, layer, p, alpha, lift / Math.Max(height, 1e-3f), contactWidth);
                if (contact != null) contact.transform.position += behind;
            }

            void PlaceContact(float feet, float centreX, float width, float z, int layer, Profile p, float alpha, float liftRatio,
                              float widthScale)
            {
                bool on = p.ContactShadow > 0 && width > 0;
                var c = Contact();
                c.gameObject.SetActive(on);
                if (!on) return;
                float w = Math.Max(width * widthScale, 0.02f);
                c.transform.position = new Vector3(centreX, feet, z);
                c.transform.rotation = Quaternion.identity;
                c.transform.localScale = new Vector3(w, w * 0.24f, 1);
                c.gameObject.layer = layer;
                spot.SetColor(idShadowColor, new Color(0, 0, 0, Clamp01(p.ContactShadow) * alpha / (1f + liftRatio * 6f)));
            }

            static void Copy(Transform from, Transform to)
            {
                to.position = from.position;
                to.rotation = from.rotation;
                to.localScale = from.lossyScale;
            }

            internal void Hide()
            {
                foreach (var r in sprites) if (r != null) r.gameObject.SetActive(false);
                if (castMesh != null) castMesh.gameObject.SetActive(false);
                if (contact != null) contact.gameObject.SetActive(false);
            }

            internal void SetQueue(int q) { if (cast != null) EnsureMaterials(q); }

            internal void Destroy()
            {
                foreach (var r in sprites) if (r != null) Object.Destroy(r.gameObject);
                sprites.Clear();
                if (castMesh != null) Object.Destroy(castMesh.gameObject);
                if (contact != null) Object.Destroy(contact.gameObject);
                if (cast != null) Object.Destroy(cast);
                if (spot != null) Object.Destroy(spot);
            }
        }

        // A field character: body and head sprites under one parent.
        sealed class FieldChar
        {
            readonly Transform parent;
            readonly List<SpriteRenderer> parts = new List<SpriteRenderer>();
            internal readonly ShadowSet Shadow = new ShadowSet();
            internal float Feet, Top, CentreX, Width, Z, Alpha;
            internal int Layer;

            internal FieldChar(Transform parent) { this.parent = parent; }

            internal void Add(SpriteRenderer sr)
            {
                foreach (var p in parts) if (p.Pointer == sr.Pointer) return;
                parts.Add(sr);
            }

            // body = the part whose bounds reach lowest; feet = its bottom
            internal bool Measure()
            {
                bool any = false;
                Feet = float.MaxValue; Top = float.MinValue;
                float minX = float.MaxValue, maxX = float.MinValue;
                Alpha = 0;
                foreach (var r in parts)
                {
                    if (r == null || !r.enabled || r.sprite == null || !r.gameObject.activeInHierarchy) continue;
                    var b = r.bounds;
                    if (b.min.y < Feet)
                    {
                        Feet = b.min.y;
                        Z = r.transform.position.z;
                        Layer = r.gameObject.layer;
                        minX = b.min.x; maxX = b.max.x;
                    }
                    Top = Math.Max(Top, b.max.y);
                    Alpha = Math.Max(Alpha, r.color.a);
                    any = true;
                }
                CentreX = (minX + maxX) * 0.5f;
                Width = maxX - minX;
                return any && Alpha > 0.01f;
            }

            internal IEnumerable<Transform> Tops()
            {
                foreach (var r in parts)
                {
                    if (r == null) continue;
                    bool nested = false;
                    foreach (var o in parts)
                        if (o != null && o.Pointer != r.Pointer && r.transform.IsChildOf(o.transform)) { nested = true; break; }
                    if (!nested) yield return r.transform;
                }
            }

            internal void SyncShadow(Profile p)
            {
                var (q, sortingLayer, order, zOffset) = ShadowSlot(Z);
                Shadow.SyncSprites(parts, Feet, Top - Feet, Alpha, CentreX, Width, Z, Layer, p, q, sortingLayer, order, zOffset);
            }

            internal void SyncShadowAt(Profile p, int q, int sortingLayer, int order, float zOffset) =>
                Shadow.SyncSprites(parts, Feet, Top - Feet, Alpha, CentreX, Width, Z, Layer, p, q, sortingLayer, order, zOffset);

            internal void Hide() => Shadow.Hide();
            internal void Destroy() => Shadow.Destroy();

            internal string Describe()
            {
                Measure();
                var names = new List<string>();
                foreach (var r in parts) if (r != null) names.Add($"{r.name} {r.sprite?.rect.width}x{r.sprite?.rect.height}");
                return $"e.g. '{parent.name}': {string.Join(", ", names)}, feet y {Feet:0.#}, height {Top - Feet:0.#}, layer {Layer}";
            }
        }

        // A battle unit: its body mesh, resting feet remembered so a Jump lifts it off its shadow.
        sealed class BattleUnit
        {
            readonly BattleSpriteEntity entity;
            readonly ShadowSet shadow = new ShadowSet();
            readonly bool enemy;
            float rest = float.MaxValue, feet, height, centreX, width, z;
            MeshRenderer mr;
            MeshFilter mf;

            internal BattleUnit(BattleSpriteEntity e)
            {
                entity = e;
                try { enemy = e.TryCast<BattleEnemyEntity>() != null; }
                catch { enemy = false; }
            }

            internal bool Measure()
            {
                var body = entity != null ? entity.CharacterSR : null;
                mr = body != null ? body.MeshRenderer : null;
                mf = body != null ? body.meshFilter : null;
                if (mr == null || mf == null || !mr.enabled || !mr.gameObject.activeInHierarchy || mf.sharedMesh == null) return false;
                var b = mr.bounds;
                feet = b.min.y;
                height = b.size.y;
                centreX = b.center.x;
                width = b.size.x;
                z = mr.transform.position.z;
                if (feet < rest) rest = feet;
                return height > 0;
            }

            internal void Sync(Profile p, int q, Vector3 behind)
            {
                float alpha = 1f;
                var m = mr.sharedMaterial;
                if (m != null && m.HasProperty("_Alpha")) alpha = Clamp01(m.GetFloat("_Alpha"));
                // enemies: pictures, often floating; their lowest pixel can be a tail (Holy Dragon),
                // so by default just a soft spot under the middle of the body
                shadow.SyncMesh(mf.sharedMesh, m?.mainTexture, mr.transform, rest, height, Math.Max(0, feet - rest), alpha,
                                centreX, width, z, mr.gameObject.layer, p, q, mr.sortingLayerID, mr.sortingOrder, behind,
                                enemy ? p.EnemyCastShadow : p.CastShadow, enemy ? 0.6f : 0.85f);
            }

            internal int BodyQueue() => entity?.CharacterSR?.MeshRenderer?.sharedMaterial?.renderQueue ?? -1;
            internal void SetQueue(int q) => shadow.SetQueue(q);
            internal void Hide() => shadow.Hide();
            internal void Destroy() => shadow.Destroy();

            internal string Describe()
            {
                Measure();
                var own = entity?.Shadow;
                string game = own == null ? "none" :
                    $"{(own.gameObject.activeInHierarchy ? "active" : "inactive")}, renderer {(own.MeshRenderer != null && own.MeshRenderer.enabled ? "on" : "off")}";
                return $"e.g. '{entity?.name}' body {width:0.#}x{height:0.#} feet y {feet:0.#}, layer {mr?.gameObject.layer}, " +
                       $"texture '{mr?.sharedMaterial?.mainTexture?.name}', game's own shadow {game}";
            }
        }

        static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    }
}

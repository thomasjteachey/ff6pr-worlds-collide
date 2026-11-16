using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace Hd2d
{
    public enum Mode { None, Field, WorldMap, Battle }

    // Limits a setting to some sections (e.g. the battle backdrop's floor and wall).
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class OnlyAttribute : Attribute
    {
        public readonly Mode[] Modes;
        public OnlyAttribute(params Mode[] modes) { Modes = modes; }
        public bool Allows(Mode m) => Array.IndexOf(Modes, m) >= 0;
    }

    // One look per kind of main-game image (field, world map, battle). Every public field
    // becomes a config key in its section ([Field], [WorldMap], [Battle]); the
    // [Description] is the config comment. No Unity types here, so it's testable offline.
    // Blur amounts are in blur-pyramid levels: 0 sharp, each level about twice as soft,
    // 4 the softest (measured at screen resolution, so the same number looks the same on
    // the 320x180 field and the 960x540 world map).
    public sealed class Profile
    {
        [Description("Apply the HD-2D look here.")]
        public bool Enabled = true;

        [Only(Mode.Field, Mode.WorldMap)]
        [Description("Keep the sharp band on the party leader (towns, dungeons). Off: the band stays at FocusCenter.")]
        public bool FollowPlayer = true;
        [Description("Height of the sharp band's centre (when not following the player): 0 = bottom of the screen, 1 = top.")]
        public float FocusCenter = 0.5f;
        [Only(Mode.Field, Mode.WorldMap)]
        [Description("Added to the player's height on screen when following (their position is at their feet).")]
        public float FocusOffset = 0.03f;
        [Description("Half the height of the sharp band, as a fraction of the screen height.")]
        public float FocusWidth = 0.12f;
        [Description("How far above the band the blur takes to reach full strength (fraction of screen height).")]
        public float FalloffTop = 0.28f;
        [Description("How far below the band the blur takes to reach full strength (fraction of screen height).")]
        public float FalloffBottom = 0.3f;
        [Description("Blur at the top of the screen, in levels: 0 sharp .. 4 softest.")]
        public float BlurTop = 3f;
        [Description("Blur at the bottom of the screen, in levels: 0 sharp .. 4 softest.")]
        public float BlurBottom = 2.4f;

        [Only(Mode.Battle)]
        [Description("Where the backdrop's floor meets its back wall or sky (screen height, 0 = bottom). Above it: the far wall.")]
        public float Horizon = 0.68f;
        [Only(Mode.Battle)]
        [Description("Far edge of the floor where the units stand: from here up to the horizon the floor blurs more and more.")]
        public float UnitsTop = 0.45f;
        [Only(Mode.Battle)]
        [Description("Near edge of the units' floor: below it the floor blurs towards the bottom of the screen.")]
        public float UnitsBottom = 0.22f;
        [Only(Mode.Battle)]
        [Description("Backdrop blur at the horizon and on the wall/sky, in levels (0..4). Characters are never blurred.")]
        public float FarBlur = 3.4f;
        [Only(Mode.Battle)]
        [Description("Backdrop blur at the bottom of the screen, in levels (0..4).")]
        public float NearBlur = 1.6f;
        [Only(Mode.Battle)]
        [Description("Haze over the far wall/sky, 0..1.")]
        public float WallHaze = 0.18f;
        [Only(Mode.Battle)]
        [Description("The wall's haze takes the backdrop's own colour (lightened). Off: HazeColor.")]
        public bool WallHazeAuto = true;

        [Only(Mode.Field)]
        [Description("Tilt the ground away from the camera, in degrees (0 = flat top-down, as the game draws it). " +
                     "The map recedes towards the top of the screen; characters stand up off it.")]
        public float Tilt = 30f;
        [Only(Mode.Field)]
        [Description("Perspective of the tilt, as a camera field of view in degrees: more = the far side narrows more.")]
        public float Perspective = 35f;
        [Only(Mode.Field)]
        [Description("Characters stand upright at full height on the tilted ground (off: they tilt with the map).")]
        public bool StandUp = true;
        [Only(Mode.Field)]
        [Description("Draw the field at this many times the game's 320x180 (0 = as many as the screen needs, 1 = the " +
                     "game's own size). Higher keeps the tilted pixel art and the shadows clean.")]
        public float Resolution = 0f;

        [Only(Mode.Field, Mode.WorldMap, Mode.Battle)]
        [Description("Strength of the shadow each character casts on the ground, 0..1 (0 = none).")]
        public float CastShadow = 0.55f;
        [Only(Mode.Field, Mode.WorldMap, Mode.Battle)]
        [Description("Strength of the soft dark spot right under each character's feet, 0..1.")]
        public float ContactShadow = 0.5f;
        [Only(Mode.Field, Mode.WorldMap, Mode.Battle)]
        [Description("Length of the cast shadow, per unit of the character's height (0.25 = a quarter as long as they're tall).")]
        public float ShadowLength = 0.25f;
        [Only(Mode.Field, Mode.WorldMap, Mode.Battle)]
        [Description("Slant of the cast shadow: sideways shift per unit of height (- = to the left, + = to the right).")]
        public float ShadowSlant = -0.25f;
        [Only(Mode.Battle)]
        [Description("Cast shadow strength for enemies, 0..1 (0 = only the soft spot under them). The shadow starts at " +
                     "the enemy's lowest pixel, which for a floating or flying enemy can be a tail or a wing tip.")]
        public float EnemyCastShadow = 0.55f;

        [Only(Mode.WorldMap)]
        [Description("How tilted the world map is while on foot, in degrees from straight down (the game itself uses " +
                     "about 20; at or below that, the game's view is left alone). The 3D camera pivots around the spot " +
                     "at the centre of the screen, so the map recedes towards the horizon.")]
        public float GroundTiltAngle = 55f;

        [Only(Mode.Field, Mode.WorldMap)]
        [Description("Haze towards the top of the screen, 0..1.")]
        public float Haze = 0.05f;
        [Only(Mode.Field, Mode.WorldMap)]
        [Description("Where the haze starts: 0 = bottom of the screen, 1 = top.")]
        public float HazeStart = 0.65f;
        [Description("Haze colour (r, g, b).")]
        public Rgb HazeColor = new Rgb(0.85f, 0.88f, 0.92f);

        [Description("Glow around bright things (sky, lamps, fire, magic). 0 = off.")]
        public float Bloom = 0.4f;
        [Description("Brightness (0..1, brightest colour channel) where the glow starts.")]
        public float BloomThreshold = 0.8f;
        [Description("Floating dust motes, 0..1.")]
        public float Motes = 0.2f;
        [Description("Colour of the dust (r, g, b).")]
        public Rgb MotesColor = new Rgb(1f, 0.95f, 0.8f);

        [Description("Colour added to the dark parts (r, g, b; negative values allowed).")]
        public Rgb Shadows = new Rgb(-0.016f, 0f, 0.04f);
        [Description("Colour added to the bright parts (r, g, b; negative values allowed).")]
        public Rgb Highlights = new Rgb(0.03f, 0.01f, -0.03f);
        [Description("Contrast: how much of a gentle S-curve to apply, 0..1.")]
        public float Contrast = 0.3f;
        [Description("Colour saturation. 1 = unchanged, 0 = grey.")]
        public float Saturation = 1.05f;
        [Description("Brightness multiplier.")]
        public float Exposure = 1f;
        [Description("Darkening towards the corners, 0..1.")]
        public float Vignette = 0.5f;

        public Profile Clone() => (Profile)MemberwiseClone();
    }

    public static class Defaults
    {
        // Bump when defaults change, and add the old ones to History: values a player
        // never touched then move to the new defaults; values they changed stay.
        public const int Version = 7;

        public static Profile For(Mode m)
        {
            var p = new Profile();
            switch (m)
            {
                case Mode.WorldMap:
                    // the world map keeps the party in the same spot on screen, and its sprites
                    // are drawn with their own projection, so the band stays put
                    p.FollowPlayer = false; p.FocusCenter = 0.42f; p.FocusWidth = 0.1f;
                    p.FalloffTop = 0.22f; p.FalloffBottom = 0.32f; p.BlurTop = 3.2f; p.BlurBottom = 2f;
                    p.Haze = 0.1f; p.HazeStart = 0.55f; p.Bloom = 0.35f; p.Motes = 0f;
                    break;
                case Mode.Battle:
                    // the blur goes on the backdrop only (see Horizon..WallHaze); the band below
                    // is a fallback for when the backdrop can't be reached on its own
                    p.FocusCenter = 0.48f; p.FocusWidth = 0.2f; p.FalloffTop = 0.15f; p.FalloffBottom = 0.25f;
                    p.BlurTop = 2.5f; p.BlurBottom = 1.2f;
                    p.Bloom = 0.45f; p.BloomThreshold = 0.75f; p.Motes = 0.45f;
                    p.Contrast = 0.35f; p.Saturation = 1.12f; p.Vignette = 0.55f;
                    break;
            }
            return p;
        }

        // Old defaults by key, for the version a config file was written with.
        public static Dictionary<string, object> History(Mode m, int version)
        {
            // 0.2.0 (version 2) and 0.3.x (version 3): later versions only added keys
            // 0.4.0 (version 5): GroundTilt (0..1) and StandUpOnMap are gone (removed from the file
            // as orphans); nothing else changed
            // 0.3.2 (version 4) had enemies' cast shadows off by default; the user wants them on
            // 0.4.1 (version 6): the world map tilt was 40 and never applied (the game's own 20-degree
            // on-foot tilt was taken for flying); the user asked for more
            if (version == 5 || version == 6)
                return m == Mode.WorldMap ? new Dictionary<string, object> { ["GroundTiltAngle"] = 40f } : new Dictionary<string, object>();
            if (version == 4) return m == Mode.Battle ? new Dictionary<string, object> { ["EnemyCastShadow"] = 0f } : new Dictionary<string, object>();
            if (version >= 2) return new Dictionary<string, object>();
            // 0.1.0 (version 1; files without DefaultsVersion)
            var d = new Dictionary<string, object>
            {
                ["Enabled"] = true, ["FollowPlayer"] = true, ["FocusCenter"] = 0.45f, ["FocusOffset"] = 0.03f,
                ["FocusWidth"] = 0.12f, ["FalloffTop"] = 0.25f, ["FalloffBottom"] = 0.25f,
                ["BlurTop"] = 1f, ["BlurBottom"] = 0.8f, ["BloomThreshold"] = 0.7f,
                ["Exposure"] = 1f, ["Contrast"] = 1.08f, ["Saturation"] = 1.1f,
                ["Shadows"] = new Rgb(0.03f, 0.02f, 0.06f), ["Highlights"] = new Rgb(1f, 0.98f, 0.95f),
                ["Haze"] = 0.12f, ["HazeStart"] = 0.55f, ["HazeColor"] = new Rgb(1f, 0.92f, 0.8f), ["Vignette"] = 0.45f,
            };
            if (m == Mode.WorldMap)
            {
                d["FocusWidth"] = 0.07f; d["FalloffTop"] = 0.2f; d["FalloffBottom"] = 0.3f;
                d["BlurTop"] = 1f; d["BlurBottom"] = 0.6f; d["Haze"] = 0.2f; d["HazeStart"] = 0.5f;
            }
            else if (m == Mode.Battle)
            {
                d["FollowPlayer"] = false; d["FocusCenter"] = 0.48f; d["FocusWidth"] = 0.26f;
                d["FalloffTop"] = 0.2f; d["FalloffBottom"] = 0.2f; d["BlurTop"] = 0.6f; d["BlurBottom"] = 0.3f;
                d["Haze"] = 0.08f; d["Vignette"] = 0.35f;
            }
            return d;
        }
    }

    public struct Rgb
    {
        public float R, G, B;
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", R, G, B);

        public bool Near(Rgb o, float eps = 1e-4f) =>
            Math.Abs(R - o.R) < eps && Math.Abs(G - o.G) < eps && Math.Abs(B - o.B) < eps;

        public static bool TryParse(string s, out Rgb c)
        {
            c = default;
            if (s == null) return false;
            var parts = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            c = new Rgb(v[0], v[1], v[2]);
            return true;
        }
    }

    // Numbers the renderer feeds the shader, derived from a profile.
    public static class Hd2dMath
    {
        // Size of the processed image: tall enough that the visible part maps about 1:1 to
        // the screen (so the game's composite doesn't rescale our blur and pixel art), never
        // smaller than the source, capped.
        public static (int w, int h) OutputSize(int srcW, int srcH, int screenH, float visibleV, int forcedH, int cap = 4096)
        {
            int h = forcedH > 0 ? forcedH : (int)Math.Round(screenH / Math.Max(visibleV, 0.1f));
            h = Math.Max(h, srcH);
            h = Math.Min(h, cap);
            int w = (int)Math.Round((double)srcW * h / srcH);
            if (w > cap) { h = (int)Math.Round((double)h * cap / w); w = cap; }
            return (Math.Max(w, 1), Math.Max(h, 1));
        }

        // Blur amounts are given in levels at screen resolution; an image `visibleH` texels
        // tall shown on a `screenH` screen needs that many fewer levels (each is a halving).
        public static float LevelOffset(int screenH, float visibleH) =>
            visibleH <= 0 ? 0f : Math.Max(0f, (float)Math.Log(screenH / (double)visibleH, 2));

        // A projected height on screen we trust for the focus band (else: FocusCenter).
        public static bool Plausible(float y) => y >= 0.05f && y <= 0.95f && !float.IsNaN(y);

        // The field's tilt as a projection (OpenGL convention, row-major m[row, col]) for an
        // orthographic camera of half-height `size`. A real tilted camera would mix map height
        // into depth and break the game's 2D layering (roofs drawn over characters by depth), so
        // instead: x and y are divided by w = 1 + k*y (rows further up shrink and narrow, like
        // a plane receding), y is foreshortened by cos(tilt), and depth stays the orthographic
        // one, scaled by 0.5 so it stays inside the clip range for every w used here. At any
        // pixel all geometry shares the same y, so depth order is exactly the game's.
        public static float[,] Keystone(float tiltDeg, float fovDeg, float size, float aspect, float near, float far)
        {
            double t = tiltDeg * Math.PI / 180, half = Math.Max(fovDeg, 1) * 0.5 * Math.PI / 180;
            var m = new float[4, 4];
            m[0, 0] = (float)(1 / (aspect * size));
            m[1, 1] = (float)(Math.Cos(t) / size);
            m[2, 2] = (float)(0.5 * -2 / (far - near));
            m[2, 3] = (float)(0.5 * -(far + near) / (far - near));
            m[3, 1] = KeystoneK(tiltDeg, fovDeg, size);
            m[3, 3] = 1;
            return m;
        }

        public static float KeystoneK(float tiltDeg, float fovDeg, float size)
        {
            double t = tiltDeg * Math.PI / 180, half = Math.Max(fovDeg, 1) * 0.5 * Math.PI / 180;
            return (float)(Math.Sin(t) * Math.Tan(half) / size);
        }

        // How much taller a character is drawn so it stands upright on the tilted ground: the
        // ground at its feet is foreshortened by cos(tilt) and shrunk by w there.
        public static float StandUpScale(float tiltDeg, float fovDeg, float size, float feetViewY)
        {
            double c = Math.Cos(tiltDeg * Math.PI / 180);
            double w = 1 + KeystoneK(tiltDeg, fovDeg, size) * feetViewY;
            return (float)(Math.Max(w, 0.2) / Math.Max(c, 0.2));
        }

        // Screen y (-1 bottom .. 1 top) of a point `viewY` above the camera's centre.
        public static float KeystoneScreenY(float[,] m, float viewY) => m[1, 1] * viewY / (1 + m[3, 1] * viewY);

        // Field drawn at `setting` times the game's size (0 = enough for the screen), 1..12.
        public static int ResolutionScale(float setting, int screenH, int fieldH)
        {
            int s = setting >= 1 ? (int)Math.Round(setting) : (int)Math.Round(screenH / (double)Math.Max(fieldH, 1));
            return Math.Max(1, Math.Min(12, s));
        }

        // Exponential smoothing towards a target, frame-rate independent.
        public static float Damp(float current, float target, float speed, float dt) =>
            current + (target - current) * (1f - (float)Math.Exp(-speed * dt));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Hd2d;

// Offline checks for the HD-2D plugin: config binding round-trips every setting per
// section, old config files upgrade without losing the player's own values, bad values
// fall back instead of breaking, and the numbers fed to the shader are sane.
static class Program
{
    static int failed, passed;
    static readonly Mode[] Modes = { Mode.Field, Mode.WorldMap, Mode.Battle };

    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else { failed++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hd2d-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ConfigBinding(dir);
            SectionsOnlyHaveTheirKeys(dir);
            ConfigEdits(dir);
            UpgradeFromV1(dir);
            UpgradeFromV2(dir);
            UpgradeFromV4(dir);
            FreshFileUpgradeIsNoOp(dir);
            Keystone();
            Colours();
            Math();
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static (ConfigFile cfg, Dictionary<Mode, ProfileBinding> b, ConfigEntry<int> ver) Open(string path)
    {
        var cfg = new ConfigFile(path, true);
        var ver = cfg.Bind("General", "DefaultsVersion", 0, "");
        var b = Modes.ToDictionary(m => m, m => new ProfileBinding(cfg, m.ToString(), m, Defaults.For(m)));
        return (cfg, b, ver);
    }

    // A fresh config reproduces each section's defaults exactly.
    static void ConfigBinding(string dir)
    {
        var (_, bindings, _) = Open(Path.Combine(dir, "fresh.cfg"));
        foreach (var m in Modes)
        {
            var defaults = Defaults.For(m);
            var p = bindings[m].Read();
            foreach (var f in ProfileBinding.FieldsFor(m))
                Check(Equals(f.GetValue(p), f.GetValue(defaults)), $"{m}.{f.Name} default {f.GetValue(defaults)} read back as {f.GetValue(p)}");
        }
        var text = File.ReadAllText(Path.Combine(dir, "fresh.cfg"));
        Check(text.Contains("[WorldMap]") && text.Contains("[Battle]") && text.Contains("[Field]"), "sections written");
        Check(text.Contains("Shadows = -0.016, 0, 0.04"), "colours written as r, g, b with negatives");
        Check(!text.Contains("0,8") && !text.Contains("3,4"), "numbers use '.' whatever the PC's locale");
    }

    static void SectionsOnlyHaveTheirKeys(string dir)
    {
        var (_, b, _) = Open(Path.Combine(dir, "keys.cfg"));
        var battle = b[Mode.Battle].Keys.ToList();
        var field = b[Mode.Field].Keys.ToList();
        foreach (var k in new[] { "Horizon", "UnitsTop", "UnitsBottom", "FarBlur", "NearBlur", "WallHaze", "WallHazeAuto" })
        {
            Check(battle.Contains(k), $"[Battle] has {k}");
            Check(!field.Contains(k) && !b[Mode.WorldMap].Keys.Contains(k), $"{k} only in [Battle]");
        }
        foreach (var k in new[] { "FollowPlayer", "FocusOffset", "Haze", "HazeStart" })
        {
            Check(field.Contains(k), $"[Field] has {k}");
            Check(!battle.Contains(k), $"{k} not in [Battle]");
        }
        foreach (var k in new[] { "Tilt", "Perspective", "StandUp", "Resolution" })
        {
            Check(field.Contains(k), $"[Field] has {k}");
            Check(!battle.Contains(k) && !b[Mode.WorldMap].Keys.Contains(k), $"{k} only in [Field]");
        }
        var world = b[Mode.WorldMap].Keys.ToList();
        foreach (var k in new[] { "CastShadow", "ContactShadow", "ShadowLength", "ShadowSlant" })
            Check(field.Contains(k) && battle.Contains(k) && world.Contains(k), $"[Field], [WorldMap] and [Battle] have {k}");
        Check(world.Contains("GroundTiltAngle") && !field.Contains("GroundTiltAngle") && !battle.Contains("GroundTiltAngle"),
              "GroundTiltAngle only in [WorldMap]");
        Check(!world.Contains("GroundTilt") && !world.Contains("StandUpOnMap"), "0.4.0's GroundTilt/StandUpOnMap are gone");
        Check(field.Contains("Shadows") && field.Contains("CastShadow"), "grade's Shadows colour and the cast shadow are separate keys");
        Check(battle.Contains("EnemyCastShadow") && !field.Contains("EnemyCastShadow"), "EnemyCastShadow only in [Battle]");
        Check(Defaults.For(Mode.Battle).EnemyCastShadow > 0, "enemies cast shadows by default (the user asked for them back)");
        Check(Defaults.For(Mode.WorldMap).GroundTiltAngle >= 30 && Defaults.For(Mode.WorldMap).GroundTiltAngle <= 60,
              "world map tilt is a clear angle in degrees (the user asked for more drama)");
        Check(Defaults.For(Mode.WorldMap).FollowPlayer == false, "world map band doesn't follow the projected player");
    }

    // Edited values come through; a broken colour keeps its default and warns.
    static void ConfigEdits(string dir)
    {
        var path = Path.Combine(dir, "edited.cfg");
        File.WriteAllText(path, "[Field]\nBlurTop = 1.5\nFollowPlayer = false\nHazeColor = 0.5, 0.6, 0.7\nMotesColor = purple\n");
        var cfg = new ConfigFile(path, true);
        var warnings = new List<string>();
        ProfileBinding.Warn = warnings.Add;
        var p = new ProfileBinding(cfg, "Field", Mode.Field, Defaults.For(Mode.Field)).Read();
        Check(p.BlurTop == 1.5f, "edited float");
        Check(!p.FollowPlayer, "edited bool");
        Check(p.HazeColor.Near(new Rgb(0.5f, 0.6f, 0.7f)), "edited colour");
        Check(p.MotesColor.Near(Defaults.For(Mode.Field).MotesColor), "bad colour keeps the default");
        Check(warnings.Count == 1 && warnings[0].Contains("MotesColor"), "bad colour warns once: " + string.Join(" | ", warnings));
        Check(p.BlurBottom == Defaults.For(Mode.Field).BlurBottom, "untouched keys keep defaults");
        ProfileBinding.Warn = _ => { };
    }

    // The exact file v0.1.0 wrote (all defaults) plus one value the player changed.
    static void UpgradeFromV1(string dir)
    {
        var path = Path.Combine(dir, "v1.cfg");
        File.WriteAllText(path, V1File().Replace("[WorldMap]\nEnabled = true\nFollowPlayer = true\nFocusCenter = 0.45",
                                                 "[WorldMap]\nEnabled = true\nFollowPlayer = true\nFocusCenter = 0.6"));
        var (cfg, b, ver) = Open(path);
        var (moved, retired) = ConfigUpgrade.Run(cfg, b.Select(kv => (kv.Key, kv.Value)), ver);
        Check(moved > 20, $"untouched values moved: {moved}");
        // 12 retired keys per section, plus battle's FollowPlayer/FocusOffset/Haze/HazeStart
        Check(retired == 3 * 12 + 4, $"retired keys removed: {retired}");
        Check(ver.Value == Defaults.Version, "version recorded");
        var field = b[Mode.Field].Read();
        var world = b[Mode.WorldMap].Read();
        var battle = b[Mode.Battle].Read();
        Check(field.BlurTop == Defaults.For(Mode.Field).BlurTop, $"old BlurTop 1 -> new default {field.BlurTop}");
        Check(field.Contrast == Defaults.For(Mode.Field).Contrast, $"old Contrast 1.08 -> new default {field.Contrast}");
        Check(field.Shadows.Near(Defaults.For(Mode.Field).Shadows), "old Shadows -> new default");
        Check(world.FocusCenter == 0.6f, "the player's own FocusCenter is kept");
        Check(world.FollowPlayer == Defaults.For(Mode.WorldMap).FollowPlayer, "world map FollowPlayer moved to the new default (off)");
        Check(battle.Vignette == Defaults.For(Mode.Battle).Vignette, "battle vignette moved");
        var text = File.ReadAllText(path);
        Check(!text.Contains("BloomIntensity") && !text.Contains("Temperature") && !text.Contains("VignetteRoundness"),
              "retired keys gone from the file");
        Check(text.Contains("DefaultsVersion = " + Defaults.Version), "version written");

        // a second run does nothing
        var again = ConfigUpgrade.Run(cfg, b.Select(kv => (kv.Key, kv.Value)), ver);
        Check(again == (0, 0), "upgrade runs once");
    }

    // A 0.2.0 file where the player tuned two values: 0.3.0 only adds keys, so both stay.
    static void UpgradeFromV2(string dir)
    {
        var path = Path.Combine(dir, "v2.cfg");
        File.WriteAllText(path, "[General]\nDefaultsVersion = 2\n[Field]\nBlurTop = 1.5\nBloom = 0.2\n[Battle]\nHorizon = 0.6\n");
        var (cfg, b, ver) = Open(path);
        var (moved, retired) = ConfigUpgrade.Run(cfg, b.Select(kv => (kv.Key, kv.Value)), ver);
        Check(moved == 0 && retired == 0, $"0.2 -> 0.3 moves nothing: {moved}/{retired}");
        Check(ver.Value == Defaults.Version, "version 3 recorded");
        var f = b[Mode.Field].Read();
        Check(f.BlurTop == 1.5f && f.Bloom == 0.2f, "tuned values kept");
        Check(f.Tilt == Defaults.For(Mode.Field).Tilt && f.CastShadow == Defaults.For(Mode.Field).CastShadow, "new keys get defaults");
        Check(b[Mode.Battle].Read().Horizon == 0.6f, "tuned battle horizon kept");
    }

    // 0.3.2 wrote EnemyCastShadow = 0 (its default then); 0.4 turns enemy shadows back on,
    // unless the player had set their own value.
    static void UpgradeFromV4(string dir)
    {
        var path = Path.Combine(dir, "v4.cfg");
        File.WriteAllText(path, "[General]\nDefaultsVersion = 4\n[Battle]\nEnemyCastShadow = 0\nCastShadow = 0.3\n");
        var (cfg, b, ver) = Open(path);
        var (moved, _) = ConfigUpgrade.Run(cfg, b.Select(kv => (kv.Key, kv.Value)), ver);
        var battle = b[Mode.Battle].Read();
        Check(moved == 1 && battle.EnemyCastShadow == Defaults.For(Mode.Battle).EnemyCastShadow, $"enemy shadows back on: {battle.EnemyCastShadow} ({moved} moved)");
        Check(battle.CastShadow == 0.3f, "the player's own CastShadow kept");

        var path5 = Path.Combine(dir, "v5.cfg");
        File.WriteAllText(path5, "[General]\nDefaultsVersion = 5\n[WorldMap]\nGroundTilt = 0.4\nStandUpOnMap = true\nBloom = 0.3\n");
        var (cfg5, b5, ver5) = Open(path5);
        var (moved5, retired5) = ConfigUpgrade.Run(cfg5, b5.Select(kv => (kv.Key, kv.Value)), ver5);
        var text5 = File.ReadAllText(path5);
        Check(retired5 == 2 && !text5.Contains("GroundTilt =") && !text5.Contains("StandUpOnMap"), $"0.4.0's world map keys removed ({retired5})");
        Check(b5[Mode.WorldMap].Read().Bloom == 0.3f && moved5 == 0, "0.4.0 -> 0.4.1 keeps the player's values");
        Check(text5.Contains($"GroundTiltAngle = {Defaults.For(Mode.WorldMap).GroundTiltAngle}"), "the tilt angle is written with its default");

        // 0.4.1 wrote GroundTiltAngle = 40, which never applied; 0.4.2 moves it to the steeper default
        var path6 = Path.Combine(dir, "v6.cfg");
        File.WriteAllText(path6, "[General]\nDefaultsVersion = 6\n[WorldMap]\nGroundTiltAngle = 40\n");
        var (cfg6, b6, ver6) = Open(path6);
        ConfigUpgrade.Run(cfg6, b6.Select(kv => (kv.Key, kv.Value)), ver6);
        Check(b6[Mode.WorldMap].Read().GroundTiltAngle == Defaults.For(Mode.WorldMap).GroundTiltAngle, "untouched 40 -> the new default");
        var path6b = Path.Combine(dir, "v6b.cfg");
        File.WriteAllText(path6b, "[General]\nDefaultsVersion = 6\n[WorldMap]\nGroundTiltAngle = 30\n");
        var (cfg6b, b6b, ver6b) = Open(path6b);
        ConfigUpgrade.Run(cfg6b, b6b.Select(kv => (kv.Key, kv.Value)), ver6b);
        Check(b6b[Mode.WorldMap].Read().GroundTiltAngle == 30f, "a chosen tilt is kept");

        var path2 = Path.Combine(dir, "v4b.cfg");
        File.WriteAllText(path2, "[General]\nDefaultsVersion = 4\n[Battle]\nEnemyCastShadow = 0.2\n");
        var (cfg2, b2, ver2) = Open(path2);
        ConfigUpgrade.Run(cfg2, b2.Select(kv => (kv.Key, kv.Value)), ver2);
        Check(b2[Mode.Battle].Read().EnemyCastShadow == 0.2f, "a chosen enemy shadow strength is kept");
    }

    // The tilt's projection: centre stays put, rows above shrink and narrow, depth ignores y.
    static void Keystone()
    {
        const float size = 90, aspect = 320f / 180;
        var m = Hd2dMath.Keystone(30, 35, size, aspect, 0.3f, 1000);
        Check(Near(Hd2dMath.KeystoneScreenY(m, 0), 0), "the camera's centre stays at the screen's centre");
        Check(Near(m[3, 1], Hd2dMath.KeystoneK(30, 35, size)) && m[3, 1] > 0, "w grows towards the top");
        Check(m[2, 1] == 0 && m[2, 0] == 0, "depth doesn't depend on x or y: the game's layering is kept");
        Check(m[3, 2] == 0 && m[0, 2] == 0 && m[1, 2] == 0, "x and y don't depend on depth");
        // what's on screen: about the same below the centre, more of the map above it
        float topY = Solve(m, 1), bottomY = Solve(m, -1);
        Check(topY > size * 1.2f && topY < size * 1.6f, $"map visible above the centre: {topY:0} (flat: {size})");
        Check(bottomY < -size * 0.9f && bottomY > -size * 1.05f, $"map visible below: {bottomY:0}");
        // depth stays inside the clip range for every row on screen
        foreach (var y in new[] { bottomY, 0f, topY })
        {
            float w = 1 + m[3, 1] * y;
            foreach (var z in new[] { -0.3f, -50f, -1000f })
            {
                float ndc = (m[2, 2] * z + m[2, 3]) / w;
                Check(ndc >= -1 && ndc <= 1, $"depth in range at y {y:0}, view z {z}: {ndc:0.###}");
            }
        }
        // flat when tilt is 0
        var flat = Hd2dMath.Keystone(0, 35, size, aspect, 0.3f, 1000);
        Check(Near(flat[1, 1], 1 / size) && flat[3, 1] == 0, "tilt 0 is the plain top-down view");

        // characters stand back up to full height
        Check(Near(Hd2dMath.StandUpScale(30, 35, size, 0), 1 / (float)System.Math.Cos(System.Math.PI / 6)), "stand-up at the centre undoes cos(tilt)");
        Check(Hd2dMath.StandUpScale(30, 35, size, 60) > Hd2dMath.StandUpScale(30, 35, size, 0), "further up the map: drawn smaller, so stretched more");
        Check(Near(Hd2dMath.StandUpScale(0, 35, size, 40), 1), "no tilt, no stretch");

        Check(Hd2dMath.ResolutionScale(0, 800, 180) == 4 && Hd2dMath.ResolutionScale(0, 1080, 180) == 6, "auto field resolution follows the screen");
        Check(Hd2dMath.ResolutionScale(1, 2160, 180) == 1 && Hd2dMath.ResolutionScale(3, 800, 180) == 3, "fixed field resolution");
        Check(Hd2dMath.ResolutionScale(0, 100000, 180) == 12, "capped");
    }

    // map height above the camera's centre that lands at screen y (-1..1)
    static float Solve(float[,] m, float screenY) => screenY / (m[1, 1] - screenY * m[3, 1]);

    static void FreshFileUpgradeIsNoOp(string dir)
    {
        var (cfg, b, ver) = Open(Path.Combine(dir, "fresh2.cfg"));
        ConfigUpgrade.Run(cfg, b.Select(kv => (kv.Key, kv.Value)), ver);
        foreach (var m in Modes)
        {
            var p = b[m].Read();
            var d = Defaults.For(m);
            foreach (var f in ProfileBinding.FieldsFor(m))
                Check(Equals(f.GetValue(p), f.GetValue(d)), $"fresh file keeps {m}.{f.Name}");
        }
    }

    static void Colours()
    {
        Check(Rgb.TryParse("1, 0.5, 0", out var c) && c.Near(new Rgb(1, 0.5f, 0)), "parse 'r, g, b'");
        Check(Rgb.TryParse("1 0.5 0", out c) && c.Near(new Rgb(1, 0.5f, 0)), "parse spaces");
        Check(Rgb.TryParse("-0.016, 0, 0.04", out c) && c.Near(new Rgb(-0.016f, 0, 0.04f)), "parse negatives");
        Check(!Rgb.TryParse("1, 0.5", out _), "two numbers rejected");
        Check(!Rgb.TryParse("", out _) && !Rgb.TryParse(null, out _), "empty rejected");
    }

    static void Math()
    {
        // output: visible part ~1:1 with the screen, never below source, capped, same aspect
        var o = Hd2dMath.OutputSize(320, 180, 800, 1f, 0);
        Check(o.h == 800 && o.w == 1422, $"320x180 on 800p -> {o.w}x{o.h} (as logged in game)");
        o = Hd2dMath.OutputSize(398, 224, 1080, 0.5f, 0);
        Check(o.h == 2160, $"half visible -> twice the screen: {o.h}");
        o = Hd2dMath.OutputSize(1920, 1080, 800, 1f, 0);
        Check(o.w == 1920 && o.h == 1080, "never smaller than the source");
        o = Hd2dMath.OutputSize(4000, 1000, 2160, 1f, 0);
        Check(o.w <= 4096 && o.h <= 4096, $"capped: {o.w}x{o.h}");
        o = Hd2dMath.OutputSize(398, 224, 1080, 1f, 720);
        Check(o.h == 720, "forced height");

        // blur levels are given at screen size; smaller images need fewer
        Check(Near(Hd2dMath.LevelOffset(800, 800), 0), "screen-size image: no offset");
        Check(Near(Hd2dMath.LevelOffset(800, 216), (float)System.Math.Log(800 / 216.0, 2)), "battle 216p on 800p");
        Check(Hd2dMath.LevelOffset(800, 1600) == 0, "bigger than the screen: no negative offset");
        Check(Hd2dMath.LevelOffset(800, 0) == 0, "no height: no offset");

        Check(Hd2dMath.Plausible(0.5f) && !Hd2dMath.Plausible(0.97f) && !Hd2dMath.Plausible(-0.1f) && !Hd2dMath.Plausible(float.NaN),
              "focus heights outside the screen are distrusted");
        Check(Near(Hd2dMath.Damp(0, 1, 6, 10), 1), "damping converges");
        Check(Near(Hd2dMath.Damp(0.3f, 0.3f, 6, 0.016f), 0.3f), "damping at rest stays");

        // defaults stay inside the shader's ranges
        foreach (var m in Modes)
        {
            var p = Defaults.For(m);
            Check(p.BlurTop >= 0 && p.BlurTop <= 4 && p.BlurBottom >= 0 && p.BlurBottom <= 4, $"{m} blur within 0..4 levels");
            Check(p.FarBlur <= 4 && p.NearBlur <= 4, $"{m} backdrop blur within 0..4 levels");
            Check(p.UnitsBottom < p.UnitsTop && p.UnitsTop < p.Horizon, $"{m} backdrop: units' floor below the horizon");
            Check(p.BloomThreshold < 1, $"{m} bloom threshold below 1");
        }
    }

    static bool Near(float a, float b) => System.Math.Abs(a - b) < 1e-4f;

    // ff6mod.hd2d.cfg as v0.1.0 wrote it on the user's PC (comments dropped).
    static string V1File()
    {
        string Section(string name, string focus) =>
            $"[{name}]\nEnabled = true\n{focus}BloomIntensity = 0.5\nBloomThreshold = 0.7\nBloomKnee = 0.5\nBloomRadius = 0.05\n" +
            "BloomSaturation = 0.8\nBloomTint = 1, 0.95, 0.85\nExposure = 1\nContrast = 1.08\nSaturation = 1.1\nTemperature = 0.08\n" +
            "Tint = 0\nShadows = 0.03, 0.02, 0.06\nHighlights = 1, 0.98, 0.95\n" +
            "VignetteInner = 0.45\nVignetteOuter = 1.05\nVignetteRoundness = 0.5\nVignetteColor = 0.1, 0.05, 0.12\n";
        return Section("Battle", "FollowPlayer = false\nFocusCenter = 0.48\nFocusOffset = 0.03\nFocusWidth = 0.26\nFalloffTop = 0.2\n" +
                                 "FalloffBottom = 0.2\nBlurTop = 0.6\nBlurBottom = 0.3\nBlurRadius = 0.012\nHaze = 0.08\nHazeStart = 0.55\n" +
                                 "HazeColor = 1, 0.92, 0.8\nVignette = 0.35\n") +
               Section("Field", "FollowPlayer = true\nFocusCenter = 0.45\nFocusOffset = 0.03\nFocusWidth = 0.12\nFalloffTop = 0.25\n" +
                                "FalloffBottom = 0.25\nBlurTop = 1\nBlurBottom = 0.8\nBlurRadius = 0.012\nHaze = 0.12\nHazeStart = 0.55\n" +
                                "HazeColor = 1, 0.92, 0.8\nVignette = 0.45\n") +
               "[General]\nEnabled = true\nToggleKey = F7\nReloadKey = F6\nSharpUpscale = true\nOutputHeight = 0\n" +
               "[Testing]\nSelfTestQuitSeconds = 0\n" +
               Section("WorldMap", "FollowPlayer = true\nFocusCenter = 0.45\nFocusOffset = 0.03\nFocusWidth = 0.07\nFalloffTop = 0.2\n" +
                                   "FalloffBottom = 0.3\nBlurTop = 1\nBlurBottom = 0.6\nBlurRadius = 0.016\nHaze = 0.2\nHazeStart = 0.5\n" +
                                   "HazeColor = 1, 0.92, 0.8\nVignette = 0.45\n");
    }
}

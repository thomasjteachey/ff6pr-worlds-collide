using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppSystem.Input;
using UnityEngine;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;

namespace WorldsCollide.PR
{
    // Gamepad/keyboard for the seed screen through the game's own input (its key config, so
    // A/B/D-pad and Enter/Esc/arrows work as in the menus), and the game's input switched off
    // while the screen is open so the title menu underneath doesn't react too.
    static class WcInput
    {
        internal static bool Blocking;
        static bool reading;

        public static void Patch(Harmony h)
        {
            var prefix = new HarmonyMethod(typeof(WcInput).GetMethod(nameof(Block), BindingFlags.NonPublic | BindingFlags.Static));
            int n = 0;
            foreach (var name in new[] { "GetKey", "GetKeyDown", "GetKeyUp", "AnyKey", "AnyKeyDown", "AnyKeyUp" })
                foreach (var m in typeof(InputListener).GetMethods().Where(m => m.Name == name && m.ReturnType == typeof(bool)))
                {
                    h.Patch(m, prefix: prefix);
                    n++;
                }
            Plugin.L.LogInfo($"[wc] input: {n} InputListener methods hooked");
        }

        static bool Block(ref bool __result)
        {
            WcPerf.InputCalls++;
            if (!Blocking || reading) return true;
            __result = false;
            return false;
        }

        static bool Read(Func<InputListener, bool> f)
        {
            var il = InputListener.Instance;
            if (il == null) return false;
            reading = true;
            try { return f(il); }
            catch (Exception) { return false; }
            finally { reading = false; }
        }

        // Once the game's own check has reported a gamepad press, the direct read is off: both
        // seeing one press on different frames would count it twice.
        static bool gameSeesPad;

        // One press, one step: our per-frame code can run before the game's own input update in a
        // frame, so the direct read and the game's check can see the same press one frame apart
        // (the screen's double presses on the Deck, 2026-11-05). A second "down" of the same key
        // within 3 frames (~50 ms) is the same press.
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> lastDown = new System.Collections.Generic.Dictionary<IntPtr, int>();

        public static bool Down(Key k, Device d = Device.None)
        {
            if (k == null || !DownRaw(k, d)) return false;
            int f = Time.frameCount;
            if (lastDown.TryGetValue(k.Pointer, out var last) && f != last && f - last <= 3) return false;
            if (f != last) lastDown[k.Pointer] = f;
            return true;
        }

        static bool DownRaw(Key k, Device d)
        {
            if (Read(il => il.GetKeyDown(k, d, false, true)))
            {
                if (!gameSeesPad && d != Device.KeyBoard && Read(il => il.GetKeyDown(k, Device.GamePad, false, true)))
                {
                    gameSeesPad = true;
                    Plugin.L.LogInfo("[wc] input: the game reads the gamepad itself; direct pad read off");
                }
                return true;
            }
            return d != Device.KeyBoard && !gameSeesPad && Pad(k, true);
        }

        public static bool Held(Key k, Device d = Device.None) =>
            Read(il => il.GetKey(k, d, true)) || (d != Device.KeyBoard && !gameSeesPad && Pad(k, false));

        // The gamepad straight from Unity's Input System as well: the game maps pads through
        // InputSystemManager actions (KeyConfig.SetGamePadKey), and on the Steam Deck the screen
        // didn't react to the pad through the game's own check. Xbox layout: A confirms, B cancels.
        static bool padFailed;

        static bool Pad(Key k, bool down)
        {
            if (padFailed || k == null) return false;
            try
            {
                var g = UnityEngine.InputSystem.Gamepad.current;
                if (g == null) return false;
                var p = k.Pointer;
                UnityEngine.InputSystem.Controls.ButtonControl a = null, b = null;
                if (p == Key.Up.Pointer) { a = g.dpad.up; b = g.leftStick.up; }
                else if (p == Key.Down.Pointer) { a = g.dpad.down; b = g.leftStick.down; }
                else if (p == Key.Left.Pointer) { a = g.dpad.left; b = g.leftStick.left; }
                else if (p == Key.Right.Pointer) { a = g.dpad.right; b = g.leftStick.right; }
                else if (p == Key.Action.Pointer) a = g.buttonSouth;
                else if (p == Key.Cancel.Pointer) a = g.buttonEast;
                else if (p == Key.PageUp.Pointer || p == Key.SwitchLeft.Pointer) a = g.leftShoulder;
                else if (p == Key.PageDown.Pointer || p == Key.SwitchRight.Pointer) a = g.rightShoulder;
                else if (p == Key.Select.Pointer) a = g.selectButton;
                bool On(UnityEngine.InputSystem.Controls.ButtonControl c) => c != null && (down ? c.wasPressedThisFrame : c.isPressed);
                return On(a) || On(b);
            }
            catch (Exception e)
            {
                padFailed = true;
                Plugin.L.LogWarning($"[wc] input: gamepad read failed, game input only: {e.Message}");
                return false;
            }
        }

        // headless check: the Input System types resolve (no pad attached)
        internal static string PadCheck()
        {
            try { return $"gamepad read ok (pad now: {(UnityEngine.InputSystem.Gamepad.current == null ? "none" : UnityEngine.InputSystem.Gamepad.current.name)})"; }
            catch (Exception e) { return "gamepad read FAILED: " + e.Message; }
        }

        // key repeat for held directions: first step at once, then after 0.35 s every 0.07 s;
        // after 1.5 s held, steps count 10
        static readonly float[] since = new float[4];
        static readonly float[] next = new float[4];

        public static int Repeat(int i, Key k, Device d = Device.None)
        {
            float now = Time.unscaledTime;
            if (Down(k, d)) { since[i] = now; next[i] = now + 0.35f; return 1; }
            if (!Held(k, d)) { since[i] = 0; return 0; }
            if (since[i] > 0 && now >= next[i])
            {
                next[i] = now + 0.07f;
                return now - since[i] > 1.5f ? 10 : 1;
            }
            return 0;
        }
    }
}

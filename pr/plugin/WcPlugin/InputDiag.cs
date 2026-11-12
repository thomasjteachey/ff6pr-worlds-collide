using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppSystem.Input;
using UnityEngine;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;

namespace WorldsCollide.PR
{
    // Press log, to find where the Pixel Remaster's double presses on the Steam Deck come from
    // (user, 2026-11-05; one gamepad, "Xbox Controller" XInput, so not two devices). For each
    // frame with input it logs ("[press] f<frame>"): raw gamepad button changes (Input System),
    // keyboard keys pressed, the game's own per-action states (InputSystemManager: 1 = down,
    // 3 = up) and every game key InputListener.GetKeyDown reported (with the device type asked).
    // One LB press should give one "down" everywhere; a second "down" shows which layer adds it.
    // Config [Input] LogPresses; at most 600 lines a session.
    static class InputDiag
    {
        internal static bool Enabled = true;
        static int lines;
        const int MaxLines = 600;
        static int frame = -1;
        static readonly List<string> now = new List<string>();
        static readonly HashSet<string> seenThisFrame = new HashSet<string>();
        static readonly Dictionary<string, bool> padWas = new Dictionary<string, bool>();
        static readonly Dictionary<int, int> stateWas = new Dictionary<int, int>();
        static bool loggedSetup;

        public static void Patch(Harmony h)
        {
            if (!Enabled) return;
            var m = AccessTools.Method(typeof(InputListener), "GetKeyDown", new[] { typeof(Key), typeof(Device), typeof(bool), typeof(bool) });
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(InputDiag), nameof(GetKeyDown_Postfix)));
        }

        static void Note(string what)
        {
            if (Time.frameCount != frame) Flush();
            if (seenThisFrame.Add(what)) now.Add(what);
        }

        static void Flush()
        {
            if (now.Count > 0 && lines < MaxLines)
            {
                lines++;
                Plugin.L.LogInfo($"[press] f{frame} t{Time.unscaledTime:0.000}: {string.Join(" | ", now)}");
            }
            now.Clear();
            seenThisFrame.Clear();
            frame = Time.frameCount;
        }

        public static void GetKeyDown_Postfix(Key key, Device type, bool __result)
        {
            if (!__result || !Enabled || lines >= MaxLines) return;
            try { Note($"game key {key?.ToString() ?? "?"} down (asked {type})"); } catch (Exception) { }
        }

        public static void Update()
        {
            if (!Enabled || lines >= MaxLines) return;
            try
            {
                if (!loggedSetup)
                {
                    loggedSetup = true;
                    var st = UnityEngine.InputSystem.InputSystem.settings;
                    Plugin.L.LogInfo($"[press] input update mode {st?.updateMode}, polling {UnityEngine.InputSystem.InputSystem.pollingFrequency} Hz, " +
                                     $"target fps {Application.targetFrameRate}, vsync {QualitySettings.vSyncCount}, fixed dt {Time.fixedDeltaTime}");
                }
                var g = UnityEngine.InputSystem.Gamepad.current;
                if (g != null)
                {
                    void Btn(string name, UnityEngine.InputSystem.Controls.ButtonControl c)
                    {
                        if (c == null) return;
                        bool p = c.isPressed;
                        if (padWas.TryGetValue(name, out var was) && was != p) Note($"pad {name} {(p ? "pressed" : "released")}");
                        padWas[name] = p;
                    }
                    Btn("A", g.buttonSouth); Btn("B", g.buttonEast); Btn("X", g.buttonWest); Btn("Y", g.buttonNorth);
                    Btn("LB", g.leftShoulder); Btn("RB", g.rightShoulder); Btn("LT", g.leftTrigger); Btn("RT", g.rightTrigger);
                    Btn("Up", g.dpad.up); Btn("Down", g.dpad.down); Btn("Left", g.dpad.left); Btn("Right", g.dpad.right);
                    Btn("Start", g.startButton); Btn("Select", g.selectButton);
                }
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.anyKey.wasPressedThisFrame)
                {
                    var keys = kb.allKeys;
                    for (int i = 0; i < keys.Count; i++)
                        if (keys[i] != null && keys[i].wasPressedThisFrame) Note($"keyboard {keys[i].keyCode}");
                }
                var ism = InputSystemManager.Instance;
                if (ism != null)
                    for (int t = 0; t <= 15; t++)
                    {
                        var type = (InputSystemManager.InputActionType)t;
                        int s = ism.GetKeyDown(type) ? 1 : ism.GetKeyUp(type) ? 3 : ism.GetKey(type) ? 2 : 0;
                        if (stateWas.TryGetValue(t, out var was) && was != s && (s == 1 || s == 3)) Note($"action {type} {(s == 1 ? "down" : "up")}");
                        stateWas[t] = s;
                    }
                if (Time.frameCount != frame) Flush();
            }
            catch (Exception e) { Enabled = false; Plugin.L.LogWarning($"[press] log off: {e.Message}"); }
        }
    }
}

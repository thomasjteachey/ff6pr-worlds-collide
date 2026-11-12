using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using Dev = UnityEngine.InputSystem.InputDevice;

namespace WorldsCollide.PR
{
    // One gamepad only. On the Steam Deck (Proton) every controller press reached the Pixel
    // Remaster twice (user report, 2026-11-05: "double presses on controller in general"): the game
    // reads pads through Unity's Input System actions bound to any gamepad, and under Proton the
    // same physical controller can show up as more than one device (Steam Input's virtual XInput
    // pad plus the raw HID device). Every device the Input System has is logged once at start and
    // whenever the list changes ("[pad]"); when there is more than one gamepad, one is kept
    // (Steam's XInput pad if there is one, else the first) and the others are disabled, so each
    // press arrives once. Config [Input] SingleGamepad turns this off. Lives in this plugin
    // because it loads in every mod mode.
    static class PadFix
    {
        internal static bool Enabled = true;
        static string lastList = "";
        static float nextCheck;
        static readonly HashSet<long> disabledByUs = new HashSet<long>();

        static string Describe(Dev d) =>
            $"#{d.deviceId} '{d.displayName}' layout {d.layout} iface '{d.description.interfaceName}' product '{d.description.product}'" +
            $"{(d.enabled ? "" : " (disabled)")}";

        public static void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextCheck) return;
            nextCheck = now + 2f;
            try { Check(); }
            catch (Exception e) { WcRunner.WarnOnce("padfix", e); }
        }

        // returns a summary (for the log and the headless test)
        internal static string Check()
        {
            var devices = InputSystem.devices;
            var all = new List<Dev>();
            for (int i = 0; i < devices.Count; i++) if (devices[i] != null) all.Add(devices[i]);
            var list = string.Join("; ", all.Select(Describe));
            if (list != lastList)
            {
                lastList = list;
                Plugin.L.LogInfo($"[pad] input devices ({all.Count}): {(all.Count == 0 ? "none" : list)}");
            }
            if (!Enabled) return list;
            var pads = all.Where(d => d.TryCast<Gamepad>() != null && (d.enabled || disabledByUs.Contains(d.deviceId))).ToList();
            if (pads.Count < 2) return list;
            var keep = pads.FirstOrDefault(d => (d.layout ?? "").IndexOf("XInput", StringComparison.OrdinalIgnoreCase) >= 0) ?? pads[0];
            foreach (var d in pads)
            {
                if (d.deviceId == keep.deviceId)
                {
                    if (!d.enabled) { InputSystem.EnableDevice(d); disabledByUs.Remove(d.deviceId); }
                    continue;
                }
                if (!d.enabled) continue;
                InputSystem.DisableDevice(d);
                disabledByUs.Add(d.deviceId);
                Plugin.L.LogInfo($"[pad] {pads.Count} gamepads: kept {Describe(keep)}, disabled {Describe(d)} (one press = one input)");
            }
            return list;
        }
    }
}

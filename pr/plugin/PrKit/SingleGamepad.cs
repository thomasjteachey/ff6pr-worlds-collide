using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using Dev = UnityEngine.InputSystem.InputDevice;

namespace PrKit
{
    // One controller, one input. On the Steam Deck (Proton) every press reached the Pixel Remaster
    // twice (the user, 2026-11-05: "double presses on controller in general"): the game reads pads
    // through Unity's Input System bound to any gamepad, and under Proton one physical controller
    // can show up as several devices (Steam Input's virtual XInput pad plus the raw HID device).
    // When there's more than one gamepad, one is kept (an XInput one if there is one, else the first)
    // and the others are disabled. Every device is logged once at start and whenever the list
    // changes ([pad]), switch on or off: the log is how a double-press report gets diagnosed.
    // Config [Input] SingleGamepad (default on) decides whether extra pads are disabled.
    // From WcPlugin's PadFix (2026-11-05); in any mod:  host.Add(new SingleGamepad());
    public sealed class SingleGamepad : Feature
    {
        // no host switch: it always runs (the device log); its own setting gates the disabling
        public SingleGamepad() { }

        ConfigEntry<bool> on;
        public bool On => on?.Value ?? true;

        string lastList = "";
        float nextCheck;
        readonly HashSet<long> disabledByUs = new HashSet<long>();

        public override void Bind() =>
            on = Host.Config.Bind("Input", "SingleGamepad", true,
                "When the game sees the same controller as more than one gamepad (Steam Deck / Proton: every press " +
                "counted twice), keep one and disable the others. Every mod mode.");

        public override void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextCheck) return;
            nextCheck = now + 2f;
            Scan();
        }

        static string Describe(Dev d) =>
            $"#{d.deviceId} '{d.displayName}' layout {d.layout} iface '{d.description.interfaceName}' product '{d.description.product}'" +
            $"{(d.enabled ? "" : " (disabled)")}";

        // the device list (for the log and headless checks); disables extra pads when On
        public string Scan()
        {
            var devices = InputSystem.devices;
            var all = new List<Dev>();
            for (int i = 0; i < devices.Count; i++) if (devices[i] != null) all.Add(devices[i]);
            var list = string.Join("; ", all.Select(Describe));
            if (list != lastList)
            {
                lastList = list;
                Kit.Info($"[pad] input devices ({all.Count}): {(all.Count == 0 ? "none" : list)}");
            }
            if (!On) return list;
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
                Kit.Info($"[pad] {pads.Count} gamepads: kept {Describe(keep)}, disabled {Describe(d)} (one press = one input)");
            }
            return list;
        }
    }
}

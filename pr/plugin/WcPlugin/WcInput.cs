using HarmonyLib;
using Il2CppSystem.Input;
using PrKit.Seeds;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;

namespace WorldsCollide.PR
{
    // Gamepad/keyboard for the seed screen through the game's own input, the game's input off while
    // the screen is open: the kit's PadInput (PrKit/Seeds, WC's own code until 2026-11-20).
    static class WcInput
    {
        internal static bool Blocking { get => PadInput.Blocking; set => PadInput.Blocking = value; }

        public static void Patch(Harmony h)
        {
            PadInput.Tag = "[wc]";
            PadInput.OnCall = () => WcPerf.InputCalls++;
            PadInput.Patch(h);
        }

        public static bool Down(Key k, Device d = Device.None) => PadInput.Down(k, d);
        public static bool Held(Key k, Device d = Device.None) => PadInput.Held(k, d);
        public static int Repeat(int i, Key k, Device d = Device.None) => PadInput.Repeat(i, k, d);
        internal static string PadCheck() => PadInput.PadCheck();
    }
}

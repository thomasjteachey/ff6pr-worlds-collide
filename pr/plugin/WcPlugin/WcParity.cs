using PrKit.Ff6;

namespace WorldsCollide.PR
{
    // The SNES's battle rules WC plays by, the kit's since 2026-11-20 (PrKit/Ff6: SnesAtb, SnesStatusTicks
    // with SnesStatusClock, SnesAutobattle; written for WC 2026-11-11..12, Beyond Chaos uses them too):
    // on in a WC game with a seed, WC's test flag for their extra logs, "[wc]" in the log, WcPerf's timing.
    static class WcParity
    {
        internal static void Setup()
        {
            System.Func<bool> on = () => Plugin.WcMode && WcSeeds.Active != null;
            System.Func<bool> testing = () => WcTestState.Running;
            SnesAtb.On = on; SnesAtb.Testing = testing; SnesAtb.Tag = "[wc]";
            SnesAtb.PerfNow = WcPerf.Now; SnesAtb.PerfLap = (slot, name, since) => WcPerf.Lap(slot, name, since);
            SnesStatusTicks.On = on; SnesStatusTicks.Testing = testing; SnesStatusTicks.Tag = "[wc]";
            SnesStatusTicks.PerfNow = WcPerf.Now; SnesStatusTicks.PerfLap = (slot, name, since) => WcPerf.Lap(slot, name, since);
            SnesAutobattle.On = on; SnesAutobattle.Testing = testing; SnesAutobattle.Tag = "[wc]";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace BraveNewWorld.Rules
{
    // Test setup applied by the plugin's F8 hotkey (config [Testing] TestKit), so the
    // esper system can be tried on any save: save afterwards to keep it.
    public static class TestKit
    {
        public const int El = 10, ElBank = 10, SpBank = 20, Gil = 500000;

        // Raise (never lower) a character's progress to EL 10 with 10 ELs to spend and
        // EP one short of the next EL, so the next battle with an esper equipped levels up.
        public static EsperProgress Progress(BnwData d, EsperProgress p, int characterStatusId)
        {
            var q = p.Clone();
            if (characterStatusId < 1 || characterStatusId > d.Rules.MaxElCharacterStatusId) return q;
            q.El = Math.Max(q.El, Math.Min(El, d.Rules.MaxEl));
            q.ElBank = Math.Max(q.ElBank, ElBank);
            int need = EsperRules.NextElAt(d, q.El);
            if (need > 0) q.Ep = Math.Max(q.Ep, need - 1);
            q.SpBank = Math.Max(q.SpBank, Math.Min(SpBank, d.Rules.MaxSpBank));
            return q;
        }

        // Espers to hand out: every BNW esper somebody can use (skips the unused slot).
        public static IEnumerable<Esper> Espers(BnwData d) => d.Espers.Where(e => e.UserStatusIds.Length > 0);
    }
}

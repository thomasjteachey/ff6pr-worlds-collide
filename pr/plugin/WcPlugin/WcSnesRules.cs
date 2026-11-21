using HarmonyLib;
using PrKit.Ff6;   // SnesUmaro, SnesHitRates: SNES engine behaviour the PR changed

namespace WorldsCollide.PR
{
    // SNES behaviour the Pixel Remaster changed, put back for Worlds Collide (WC runs on the SNES
    // engine, so its seeds and flags assume it). The list and the decisions are in
    // pr/SNES_PR_DISCREPANCIES.md (the user, 2026-11-20: discrepancies BNW's port finds go to WC):
    //  * Umaro (SnesUmaro): vanilla's attack odds per relic set (C2/5269: 158/95 Fight/Tackle;
    //    94/63/95 with Rage Ring or Blizzard Orb; 62/63/63/63 with both) instead of the PR's
    //    50/50, 25/37.5/37.5, 13/29/29/29; a thrown Mog a sure critical (C2/16CF), which the PR lost;
    //  * the physical hit rate's x1.25 statuses without Charm (SnesHitRates: the PR added it; the
    //    SNES list is Blind/Zombie/Muddle/Slow/Rerise);
    //  * the Offering's x0.5 on every physical hit by a character, and the Genji Glove's -25% as the SNES
    //    has it, vanilla's own bug: when NOT holding two weapons (SnesRelicPenalties; the PR: both on
    //    Attack only, the Genji Glove's while dual wielding). Config [Battle] GenjiGlove picks SNES / PR / None;
    //  * Suplex misses the monsters that can't be suplexed (SnesSuplex: the SNES's monster byte $13 bit 2; the PR's
    //    Suplex never misses for it). WC never changes the bit (its data/enemy.py only reads it back), so vanilla's list,
    //    with the PR's Coliseum copies of those monsters (ids 500+: SnesSuplexRules.VanillaWithCopies).
    // WcTest "check=snes_rules" plays them in a real battle.
    static class WcSnesRules
    {
        internal static string HitRates;
        internal static GenjiRule Genji = GenjiRule.SnesBug;   // Plugin.cs: config [Battle] GenjiGlove

        public static void Patch(Harmony h)
        {
            SnesUmaro.Apply(h, new SnesUmaroMod { Name = "WC", Rows = SnesUmaro.VanillaRows, MogToss = true });
            HitRates = SnesHitRates.Fix("WC", SnesHitRates.NotOnSnes, null);
            if (HitRates != null) Plugin.L.LogInfo(HitRates);
            SnesRelicPenalties.Apply(h, new SnesRelicPenaltiesMod
            {
                Name = "WC", Offering = true, Genji = Genji, Enabled = () => Plugin.WcMode && WcSeeds.Active != null,
            });
            SnesSuplex.Apply(h, new SnesSuplexMod
            {
                Name = "WC", NoSuplex = SnesSuplexRules.VanillaWithCopies, Enabled = () => Plugin.WcMode && WcSeeds.Active != null,
            });
        }
    }
}

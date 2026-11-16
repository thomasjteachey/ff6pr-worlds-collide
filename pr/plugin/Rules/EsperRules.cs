using System;
using System.Collections.Generic;
using System.Linq;

namespace BraveNewWorld.Rules
{
    // One character's esper progress. Saved in the game's scenario variables as
    // three ints (see Pack/Unpack), so it travels with the save file.
    public struct EsperProgress
    {
        public int Ep, El, ElBank, SpBank;
        public StatBonus Spent;  // running total of EL bonuses applied

        public static EsperProgress Empty => new EsperProgress { Spent = new StatBonus() };

        public EsperProgress Clone()
        {
            var c = this;
            var s = Spent ?? new StatBonus();
            c.Spent = new StatBonus { Hp = s.Hp, Mp = s.Mp, Power = s.Power, Agility = s.Agility, Vitality = s.Vitality, Magic = s.Magic };
            return c;
        }

        // word 0: EP (16 bits) | EL (5) | EL bank (5) | SP bank (5)
        // word 1: spent HP (11 bits) | spent MP (10)
        // word 2: spent Vigor | Speed | Stamina | Magic (6 bits each)
        public int[] Pack()
        {
            var s = Spent ?? new StatBonus();
            return new[]
            {
                Clamp(Ep, 0xFFFF) | Clamp(El, 31) << 16 | Clamp(ElBank, 31) << 21 | Clamp(SpBank, 31) << 26,
                Clamp(s.Hp, 0x7FF) | Clamp(s.Mp, 0x3FF) << 11,
                Clamp(s.Power, 63) | Clamp(s.Agility, 63) << 6 | Clamp(s.Vitality, 63) << 12 | Clamp(s.Magic, 63) << 18,
            };
        }

        public static EsperProgress Unpack(int w0, int w1, int w2) => new EsperProgress
        {
            Ep = w0 & 0xFFFF, El = (w0 >> 16) & 31, ElBank = (w0 >> 21) & 31, SpBank = (w0 >> 26) & 31,
            Spent = new StatBonus
            {
                Hp = w1 & 0x7FF, Mp = (w1 >> 11) & 0x3FF,
                Power = w2 & 63, Agility = (w2 >> 6) & 63, Vitality = (w2 >> 12) & 63, Magic = (w2 >> 18) & 63,
            },
        };

        static int Clamp(int v, int max) => Math.Max(0, Math.Min(max, v));
    }

    // Base (unboosted) stats, used to respect BNW's stat cap of 128 when spending ELs.
    public struct BaseStats
    {
        public int Power, Agility, Vitality, Magic;
    }

    public static class EsperRules
    {
        // An esper the data doesn't know about (none should exist) stays equippable.
        public static bool CanEquip(BnwData d, int characterStatusId, int contentId)
        {
            var e = d.EsperByContent(contentId);
            return e == null || e.UserStatusIds.Contains(characterStatusId);
        }

        // BNW Calc_EP: (XP / 8) * SP. The SNES keeps only 16 bits; we saturate instead.
        public static int EpGain(int xp, int sp)
        {
            if (xp <= 0 || sp <= 0) return 0;
            long ep = (long)(xp / 8) * sp;
            return (int)Math.Min(ep, int.MaxValue);
        }

        // EP needed for the next EL, or -1 at max EL.
        public static int NextElAt(BnwData d, int el) =>
            el < d.Rules.MaxEl && el < d.Rules.EpChart.Length ? d.Rules.EpChart[el] : -1;

        // BNW Add_EP + Show_EL, run for each character that got XP. Returns ELs gained.
        public static int AfterBattle(BnwData d, ref EsperProgress p, int characterStatusId, bool hasEsper, int xp, int sp)
        {
            if (!hasEsper) return 0;
            var r = d.Rules;
            p.SpBank = Math.Min(r.MaxSpBank, p.SpBank + Math.Max(0, sp));
            p.Ep = (int)Math.Min(r.MaxEp, (long)p.Ep + EpGain(xp, sp));
            if (characterStatusId < 1 || characterStatusId > r.MaxElCharacterStatusId) return 0;
            int gained = 0;
            while (true)
            {
                int need = NextElAt(d, p.El);
                if (need < 0 || p.Ep < need) break;
                p.El++;
                p.ElBank++;
                gained++;
            }
            return gained;
        }

        // BNW Apply_Bonus: spend one banked EL on an esper's bonus. Stats stop at the cap
        // (base + already spent), like AddEL's "CMP #$80". Returns what was actually added,
        // or null if there was nothing to spend.
        public static StatBonus SpendEl(BnwData d, ref EsperProgress p, Esper esper, BaseStats baseStats)
        {
            if (p.ElBank <= 0 || esper == null) return null;
            p.ElBank--;
            p.Spent ??= new StatBonus();
            var b = d.ElBonusOf(esper);
            int cap = d.Rules.StatCap;
            var added = new StatBonus
            {
                Hp = b.Hp,
                Mp = b.Mp,
                Power = Room(b.Power, baseStats.Power + p.Spent.Power, cap),
                Agility = Room(b.Agility, baseStats.Agility + p.Spent.Agility, cap),
                Vitality = Room(b.Vitality, baseStats.Vitality + p.Spent.Vitality, cap),
                Magic = Room(b.Magic, baseStats.Magic + p.Spent.Magic, cap),
            };
            p.Spent.Hp += added.Hp;
            p.Spent.Mp += added.Mp;
            p.Spent.Power += added.Power;
            p.Spent.Agility += added.Agility;
            p.Spent.Vitality += added.Vitality;
            p.Spent.Magic += added.Magic;
            return added;
        }

        static int Room(int add, int current, int cap) => Math.Max(0, Math.Min(add, cap - current));

        public enum SpendResult { Spent, NoElToSpend, CantUseEsper, NoEsperLevels }

        // The esper menu's "spend an EL" action (BNW Pressed_A on the bonus row): the
        // character must be one who gains ELs, be allowed to use this esper (it need not
        // be equipped) and have a banked EL.
        public static SpendResult TrySpend(BnwData d, ref EsperProgress p, int characterStatusId, Esper esper,
            BaseStats baseStats, out StatBonus added)
        {
            added = null;
            if (characterStatusId < 1 || characterStatusId > d.Rules.MaxElCharacterStatusId) return SpendResult.NoEsperLevels;
            if (esper == null || !esper.UserStatusIds.Contains(characterStatusId)) return SpendResult.CantUseEsper;
            if (p.ElBank <= 0) return SpendResult.NoElToSpend;
            added = SpendEl(d, ref p, esper, baseStats);
            return SpendResult.Spent;
        }

        // --- SP bank ------------------------------------------------------------------

        public enum LearnResult { Learned, NotEnoughSp, AlreadyKnown, CantUseEsper, NoSpBank, NotThisEspersSpell }

        // BNW Pressed_A on a spell row: a character who banks SP (Terra..Gau), allowed to
        // use this esper (it need not be equipped), pays the spell's full SP cost from the
        // bank, whatever progress battles already made, and learns it at once.
        public static LearnResult TryLearn(BnwData d, ref EsperProgress p, int characterStatusId, Esper esper,
            int abilityId, bool known, out int cost)
        {
            cost = esper?.SpellByAbility(abilityId)?.Sp ?? 0;
            if (!BanksSp(d, characterStatusId)) return LearnResult.NoSpBank;
            if (esper == null || !esper.UserStatusIds.Contains(characterStatusId)) return LearnResult.CantUseEsper;
            if (cost <= 0) return LearnResult.NotThisEspersSpell;
            if (known) return LearnResult.AlreadyKnown;
            if (p.SpBank < cost) return LearnResult.NotEnoughSp;
            p.SpBank -= cost;
            return LearnResult.Learned;
        }

        public static bool BanksSp(BnwData d, int characterStatusId) =>
            characterStatusId >= 1 && characterStatusId <= d.Rules.MaxElCharacterStatusId;

        // "20/30", for the esper screen's "SP" readout (BNW: "SP 20/30").
        public static string SpBankText(BnwData d, EsperProgress p) => $"{p.SpBank}/{d.Rules.MaxSpBank}";

        // Second line of the description box on a spell row; empty when there's nothing to say.
        public static string LearnHint(BnwData d, EsperProgress p, int characterStatusId, Esper esper,
            EsperSpell spell, bool known, string characterName)
        {
            if (!BanksSp(d, characterStatusId) || spell == null || known) return "";
            if (esper == null || !esper.UserStatusIds.Contains(characterStatusId))
                return $"{characterName} can't use {esper?.Name ?? "this esper"}.";
            var bank = SpBankText(d, p);
            return p.SpBank >= spell.Sp
                ? $"Confirm: learn {spell.Name} now for {spell.Sp} SP (SP {bank})."
                : $"Learn now for {spell.Sp} SP: not enough banked (SP {bank}).";
        }

        // Description-box text on the esper's name row (BNW: the cursor stop that equips).
        public static string NameHint(BnwData d, EsperProgress p, int characterStatusId, Esper esper, bool equipped)
        {
            var name = esper?.Name ?? "this esper";
            var action = equipped ? $"Confirm: remove {name}." : $"Confirm: equip {name}.";
            if (!BanksSp(d, characterStatusId) || esper == null || esper.Spells.Length == 0) return action;
            return $"{action} Pick a spell below to learn it with banked SP (SP {SpBankText(d, p)}).";
        }

        // One short line for the esper screen, e.g. "EL 12  2 to spend [Q]" or
        // "EL 12  next EL in 1,234 EP". Empty for characters without ELs (Gogo, Umaro, guests).
        public static string StatusLine(BnwData d, EsperProgress p, int characterStatusId, string keyLabel) =>
            StatusLines(d, p, characterStatusId, keyLabel).FirstOrDefault() ?? "";

        // Description-box text while the cursor is on the bonus row. Also carries the
        // character's EL and EP to the next one (BNW shows those beside LV and on the status screen).
        public static string SpendHint(BnwData d, EsperProgress p, int characterStatusId, Esper esper, string characterName)
        {
            if (characterStatusId < 1 || characterStatusId > d.Rules.MaxElCharacterStatusId)
                return $"{characterName} doesn't gain Esper Levels.";
            var level = LevelText(d, p);
            if (esper == null || !esper.UserStatusIds.Contains(characterStatusId))
                return $"{characterName} can't use {esper?.Name ?? "this esper"}. {level}";
            var bonus = d.ElBonusOf(esper);
            if (p.ElBank > 0)
                return $"Confirm: spend 1 EL on {esper.Name} for {bonus}. {level}";
            return $"No unspent ELs. {esper.Name} gives {bonus} per EL. {level}";
        }

        // BNW shows EL (beside LV, and "Next EL" on the status screen) only once espers have
        // been found, and never for Gogo, Umaro or guests.
        public static bool ShowsEl(BnwData d, int characterStatusId, bool ownsAnEsper) =>
            ownsAnEsper && characterStatusId >= 1 && characterStatusId <= d.Rules.MaxElCharacterStatusId;

        // "Next EL" value: EP still needed, 0 at max EL (as BNW's status screen).
        public static int EpToNextEl(BnwData d, EsperProgress p)
        {
            int need = NextElAt(d, p.El);
            return need < 0 ? 0 : Math.Max(0, need - p.Ep);
        }

        // "EL 10, next EL in 1,234 EP." / "EL 25 (max)."
        public static string LevelText(BnwData d, EsperProgress p)
        {
            int need = NextElAt(d, p.El);
            return need < 0 ? $"EL {p.El} (max)." : $"EL {p.El}, next EL in {need - p.Ep:N0} EP.";
        }

        // Label of the bonus row, as BNW's esper screen: "Unspent EL: 8" (longest first, so
        // the screen can fall back to a shorter form if it won't fit). Empty for characters
        // without ELs (Gogo, Umaro, guests).
        public static List<string> StatusLines(BnwData d, EsperProgress p, int characterStatusId, string keyLabel)
        {
            var lines = new List<string>();
            if (characterStatusId < 1 || characterStatusId > d.Rules.MaxElCharacterStatusId) return lines;
            if (!string.IsNullOrEmpty(keyLabel) && p.ElBank > 0) lines.Add($"Unspent EL: {p.ElBank} [{keyLabel}]");
            lines.Add($"Unspent EL: {p.ElBank}");
            lines.Add($"EL: {p.ElBank}");
            return lines;
        }

        // --- equip bonus helpers (applied on top of the PR's computed values) ---------

        public static int WithPercent(int value, int pct, int max) =>
            pct == 0 ? value : (int)Math.Min(max, (long)value * (100 + pct) / 100);

        public static int WithAdd(int value, int add, int max) => add == 0 ? value : Math.Min(max, value + add);

        // PR resistance types: 1 weak, 2 half, 3 null, 4 absorb. Half cancels a weakness
        // and never downgrades null/absorb.
        public const int Weak = 1, Half = 2;

        public static void AddHalves(IDictionary<int, int> resist, IEnumerable<int> attributes)
        {
            foreach (var a in attributes)
            {
                if (!resist.TryGetValue(a, out var t)) resist[a] = Half;
                else if (t == Weak) resist.Remove(a);
            }
        }

        public static void AddProtections(IDictionary<int, int> resist, IEnumerable<int> conditions, int value = 1)
        {
            foreach (var c in conditions)
                if (!resist.ContainsKey(c)) resist[c] = value;
        }

        // Text for the esper screen's "Magicite Bonus" line.
        public static string BonusLine(BnwData d, Esper e)
        {
            var el = d.ElBonusOf(e);
            var equip = Equip(e.Equip, e.EquipBonusText);
            var line = el.IsEmpty ? "" : $"EL: {el}";
            if (equip.Length > 0) line += (line.Length > 0 ? "   " : "") + "Equip: " + equip;
            return line;
        }

        static string Equip(EquipBonus q, string text)
        {
            var s = (text ?? "").Replace("\n", " ").Trim();
            if (s.StartsWith("Equip ", StringComparison.Ordinal)) s = s.Substring(6);
            return s;
        }
    }
}

"""Monster battle dialogue (the SNES AI's "message xx", F3 xx yy) -> PR battle lines.

The SNES keeps monster speech in one table: C1/9846 reads it with LDA $CFDFE0,X (256 pointers
into bank CF, text in the menu/battle encoding: $FF space, $00 end, $01 new line, $05 pause,
$02 nn = character nn's name; BNW adds $D3 as an opening quote and icons $D0/$D1/$D6 (the
D-pad arrows of its Blitz tutorial) and $D5 (a music note: Guardian's song)). The pointer is
read from that instruction, so a mod that moves the table still works.

The PR shows battle lines from story_mes_en.txt keys (speaker in story_cha_en.txt, empty for
monsters), one key per line, given in an act's sValues (vanilla Cactuar: Act [696] s[M109_01]
"It's bundling up something..."; Ultros: FirstAttackAct with three keys shown in turn). Names
are tags with the Japanese codename: "Ah, if it isn't (MASH)!".
"""
import ff6snes as S

CODE_AT = 0x019846  # C1/9846: BF lo hi bank = LDA long,X
NAME_TAGS = ["(TINA)", "(ROCK)", "(KAIEN)", "(SHADOW)", "(EDGAR)", "(MASH)", "(CELES)", "(STRAGUS)",
             "(RELM)", "(SETZER)", "(MOG)", "(GAU)", "(GOGO)", "(UMARO)"]
ICONS = {0xD0: "←", 0xD1: "→", 0xD6: "A", 0xD5: "♪", 0xD3: '"'}  # $D6 ends Pummel's input: the confirm button


def table(rom):
    """File offset of the dialogue pointer table, or None if the code isn't the usual LDA."""
    d = rom.data
    if d[CODE_AT] != 0xBF:
        return None
    lo, hi, bank = d[CODE_AT + 1], d[CODE_AT + 2], d[CODE_AT + 3]
    return (bank & 0x3F) << 16 | hi << 8 | lo


def lines(rom, index):
    """Message `index` -> its lines as PR text (quotes, pauses and padding removed)."""
    base = table(rom)
    if base is None or not 0 <= index < 256:
        return []
    d = rom.data
    ptr = d[base + 2 * index] | d[base + 2 * index + 1] << 8
    k = (base & 0xFF0000) | ptr
    out, cur, n = [], [], 0
    while k < len(d) and d[k] != 0x00 and n < 600:
        c = d[k]
        if c == 0x01:
            out.append("".join(cur))
            cur = []
        elif c == 0x02:
            k += 1
            cur.append(NAME_TAGS[d[k]] if d[k] < len(NAME_TAGS) else "")
        elif c == 0x05 or c == 0xFF:
            cur.append(" ")
        elif c == 0xC7:
            cur.append("...")
        elif c in ICONS:
            cur.append(ICONS[c])
        elif c in S._NAME:
            cur.append(S._NAME[c])
        k += 1
        n += 1
    out.append("".join(cur))
    clean = []
    for line in out:
        line = " ".join(line.split())
        if len(line) >= 2 and line[0] == '"' and line[-1] == '"':
            line = line[1:-1].strip()
        if line:
            clean.append(line)
    return clean


# The PR's own lines for the vanilla messages WC's scripts show (WC keeps them unchanged), so a
# seed shows the game's text in the player's language and the ROM-free generator needs no SNES
# dialogue: SNES message index -> story_mes keys, shown in turn (a multi-line SNES box can be
# several PR lines). Matched by monster and wording (vanilla PR scripts and battle scripts).
PR_KEYS = {
    0: ["E0401_00_402_b_01"],                        # System error!
    1: ["E0547_01_425_b_05", "E0547_01_425_b_06"],   # Ultros's form has changed! / Beware his tri-elemental attack!
    5: ["E0011_01_323_b_07"],                        # Gruuu......
    31: ["E0405_03_179_b_09"],                       # The crane shook the deck!
    34: ["E0565_01_119_b_01", "E0565_01_119_b_02"],  # Air Force launched a Speck. / A Speck absorbs magic!
    35: ["E0239_00_001_b_02"],                       # Ooh...I'm hungry!
    36: ["E0241_00_001_b_01", "E0241_00_001_b_02", "E0241_00_001_b_03"],  # Gau joins
    38: ["E0405_03_179_b_08"],                       # Unleashed electric energy!
    44: ["E0405_03_179_b_01"],                       # Electrified LV 1
    45: ["E0405_03_179_b_02"],                       # Electrified LV 2
    49: ["E0405_03_179_b_03"],                       # Electrified LV 3
    46: ["E0371_01_165_b_01"],                       # No use!
    47: ["E0371_01_165_b_01"],                       # No use!
    48: ["E0371_01_165_b_02"],                       # Won't let you pass!!
    50: ["E0405_03_179_b_07"],                       # Unleashed thermal energy!
    51: ["E0405_03_179_b_04"],                       # Heat source LV 1
    70: ["E0405_03_179_b_05"],                       # Heat source LV 2
    71: ["E0405_03_179_b_06"],                       # Heat source LV 3
    56: ["E0565_01_119_b_07"],                       # Count 6!
    57: ["E0565_01_119_b_08"],                       # Count 5!
    58: ["E0565_01_119_b_09"],                       # Count 4!
    59: ["E0565_01_119_b_10"],                       # Count 3!
    60: ["E0565_01_119_b_11"],                       # Count 2!
    69: ["E0565_01_119_b_12"],                       # Count 1!
    68: ["M102_01"],                                 # Alarm's ringing!
    73: ["E0790_00_999_b_04", "E0790_00_999_b_05", "E0790_00_999_b_06", "E0790_00_999_b_07", "E0790_00_999_b_08"],  # Yeti powers up
    75: ["E0460_01_606_b_09", "E0460_01_606_b_10"],  # Look! / The, the door's opening!
    90: ["E0744_01_492_b_01"],                       # Effect of "Rflect" vanished
    91: ["E0901_01_529_b_01"],                       # Larry ran away
    99: ["M103_01"],                                 # KatanaSoul's power up!
    100: ["E0901_01_529_b_02"],                      # Larry came back
    101: ["E0992_00_566_b_08"],                      # Vast energy focused
    103: ["E0992_00_566_b_06", "E0565_01_119_b_10"],  # Wave Cannon! Count down!! / Count 3!
    104: ["E0992_00_566_b_01"],                      # Ran basic program!
    115: ["E0981_00_551_b_01"],                      # Gold Dragon begins to store energy!
    118: ["E0809_00_498_b_01"],                      # Remove "Rflect"
    124: ["E0744_01_492_b_02"],                      # Enemy's coming from behind!
    125: ["E0744_01_492_b_03"],                      # Another monster appeared!
    126: ["E0992_00_566_b_03"],                      # Ran Ultros's battle program!
    127: ["E0992_00_566_b_04"],                      # Ran battle program! (Dadaluma's)
    128: ["E0992_00_566_b_05"],                      # Ran Air Force's battle program!
    129: ["E0992_00_566_b_07"],                      # Ran Atma's battle program!
    130: ["E0992_00_566_b_02"],                      # Included battle program!
    131: ["E0977_00_554_b_08"],                      # Unknown light surrounded Atma!
    134: ["M109_01"],                                # Bundling up something
    135: ["M109_02"],                                # Work load up 10 times!
    136: ["E0996_00_567_b_01"],                      # Doom's aura is shaking!
    138: ["E0744_01_492_b_04"],                      # 4 attacks!!
    140: ["E1004_00_571_b_04"],                      # The end comes...beyond chaos.
}
# lines the PR has no counterpart for (short, shown as given, the same in every language)
OWN_TEXT = {
    8: ["Wh...whew!!"],
    93: ["Go! Guys!!", "Ha, ha, ha!", "Give up?"],
    94: ["Ha, ha, ha!", "Give up?"],
}


class Messages:
    """Message index -> PR keys, made on first use; .texts holds the new story_mes lines."""
    def __init__(self, rom, prefix="BNW_BTL"):
        self.rom, self.prefix = rom, prefix
        self.keys, self.texts = {}, {}

    def __call__(self, index):
        if index not in self.keys:
            ks = []
            for n, line in enumerate(lines(self.rom, index), 1):
                key = f"{self.prefix}_{index:03d}_{n:02d}"
                self.texts[key] = line
                ks.append(key)
            self.keys[index] = ks
        return self.keys[index]

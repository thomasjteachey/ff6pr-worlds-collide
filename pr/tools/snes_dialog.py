"""SNES FF6 (US layout) dialogue decoder: dialogue i -> text.

Pointers at CC/E602 (3084 x 2 bytes; dialogues from the index stored at CC/E600 on are in bank
CE, the rest in CD), text in the dialogue encoding (BNW's asm/dialog.tbl: $20.. letters and
digits), $80-$FF = two-character pairs from the table at C0/DFA0, $00 end, $01 new line, $13 new
page ("\\n\\n"), $02-$0F character names, $10 pause, $11/$14/$16 + one argument (waits, tab), $12
(no argument), $19-$1C gil/item/spell placeholders ("*"). Used by dialog_align.py.

  .venv/Scripts/python tools/snes_dialog.py      (prints samples)
"""
import ff6snes as S
import prdata as P

CH = {}
for _c in range(26):
    CH[0x20 + _c] = chr(65 + _c)
    CH[0x3A + _c] = chr(97 + _c)
for _c in range(10):
    CH[0x54 + _c] = str(_c)
CH.update({0x5E: "!", 0x5F: "?", 0x60: "/", 0x61: ":", 0x62: '"', 0x63: "'", 0x64: "-", 0x65: ".", 0x66: ",",
           0x67: "...", 0x68: ";", 0x69: "#", 0x6A: "+", 0x6B: "(", 0x6C: ")", 0x6D: "%", 0x6E: "~", 0x7F: " "})
NAMES = ["Terra", "Locke", "Cyan", "Shadow", "Edgar", "Sabin", "Celes", "Strago", "Relm", "Setzer", "Mog", "Gau",
         "Gogo", "Umaro"]
COUNT = 3084


def dte(rom):
    d, base = rom.data, 0x00DFA0
    return {0x80 + i: CH.get(d[base + 2 * i], "") + CH.get(d[base + 2 * i + 1], "") for i in range(128)}


def dialogs(rom):
    d = rom.data
    split = d[0x0CE600] | d[0x0CE601] << 8
    pairs, out = dte(rom), []
    for i in range(COUNT):
        p = d[0x0CE602 + 2 * i] | d[0x0CE602 + 2 * i + 1] << 8
        k = (0x0D0000 if i < split else 0x0E0000) + p
        s, n = [], 0
        while n < 2000:
            c = d[k]
            k += 1
            n += 1
            if c == 0x00:
                break
            if c == 0x01:
                s.append("\n")
            elif 0x02 <= c <= 0x0F:
                s.append(NAMES[c - 2] if c - 2 < len(NAMES) else "")
            elif c == 0x10:
                s.append(" ")
            elif c == 0x13:
                s.append("\n\n")
            elif c in (0x11, 0x14, 0x16):
                k += 1
            elif c in (0x19, 0x1A, 0x1B, 0x1C):
                s.append("*")
            elif c in pairs:
                s.append(pairs[c])
            elif c in CH:
                s.append(CH[c])
        out.append("".join(s))
    return out


if __name__ == "__main__":
    ds = dialogs(S.Rom(P.VANILLA_ROM))
    for i in (0, 2, 3, 343, 1000, 3083):
        print(i, repr(ds[i][:160]))

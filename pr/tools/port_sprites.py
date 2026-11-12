"""Enemy sprites the mod moved: point the PR monster at the asset that looks right.

The PR draws each monster from a pre-drawn image (monster.monster_asset_id -> monster_asset),
so a mod's recolors and new art can't be ported as data. What can: a monster the mod draws
with ANOTHER vanilla monster's shape (SNES graphics entry D2/7000 -> tiles at E9/7000) gets that
monster's PR asset (right shape, vanilla colors). Compared by content (the first 96 bytes of
tiles), since mods move graphics around (BNW shifted most pointers by 64 bytes):
tools/sprite_compare.py. BNW (2026-11-05): 141 monsters look like vanilla, ~220 are vanilla
shapes in new colors (kept), 23 new art; shape swaps: Banshee (26) = Critic, Europa (96) =
Fortis, Kefka (375, an empty vanilla slot showing the Guard placeholder) = Kefka.

profile.sprite_keep: monsters whose own PR art is kept although the mod reuses another shape
(BNW: Silver/Purple/Gold-D are Doom Dragon recolors; the PR's Ice/Storm/Gold Dragon art keeps
them distinct). profile.sprite_standins {monster: monster}: a stand-in for new art on a slot
that shows the placeholder (BNW: Kaiser 311, new in BNW, in vanilla's unused CzarDragon slot
-> Doom Dragon's art).
"""
import ff6snes as S
import prdata as P

INFO, GFX = 0x127000, 0x297000  # D2/7000 graphics entries (5 bytes), E9/7000 tiles
N = 384


def shape(rom, i):
    e = rom.b(INFO + 5 * i, 5)
    ptr = e[0] | e[1] << 8
    return bytes(rom.b(GFX + (ptr & 0x7FFF) * 8, 96))


def port(van, mod, tables, log, profile=None):
    mon = tables.setdefault("monster", P.Table("monster"))
    vmon = P.Table("monster")
    keep = set(getattr(profile, "sprite_keep", None) or ())
    standins = getattr(profile, "sprite_standins", None) or {}
    changed = set()
    vshape = [shape(van, i) for i in range(N)]
    first = {}
    for j, sh in enumerate(vshape):
        first.setdefault(sh, j)
    names = {e.id: e.name.strip() for e in S.enemies(mod)}
    notes = []

    def asset(i):
        r = vmon.rows.get(i + 1)
        return r["monster_asset_id"] if r else None

    def set_asset(i, a, why):
        row = mon.rows.get(i + 1)
        if row is None or a is None or row["monster_asset_id"] == a:
            return
        row["monster_asset_id"] = a
        changed.add(i + 1)
        notes.append(f"{i} {names.get(i) or '?'} -> {why}")

    for i in range(N):
        if i in keep:
            continue
        sh = shape(mod, i)
        if sh == vshape[i]:
            continue
        j = first.get(sh)
        if j is None or asset(j) == asset(i):
            continue
        set_asset(i, asset(j), f"{j}'s shape")
    placeholder = asset(0)  # empty vanilla slots show the Guard (asset of monster 0)
    for i, j in standins.items():
        if asset(i) == placeholder:
            set_asset(i, asset(j), f"{j}'s art (stand-in)")
    log(f"sprites: {len(changed)} monsters on another asset ({'; '.join(notes) or 'none'})")
    return {"monster": changed}

"""Port BNW random encounters (formations, packs, where packs appear) to the PR.

PR model (learned from vanilla):
  * monster_party = a formation (vanilla PR party f+1 ~ SNES formation f).
  * monster_set = a pack: 4 parties at rates 80/80/80/16 (SNES 5/16 x3, 1/16).
  * World map: the PR re-zoned the maps; areas 164-188 (WoB) / 264-297 (WoR)
    each use one set (401-425 / 451-484) that equals one SNES pack. Dungeon maps
    use map.monster_set_id. So PR locations are tied to vanilla packs, not to
    SNES zone numbers.
Port: match each PR set to its vanilla SNES pack (by enemy line-ups), find the
SNES places that used that pack, look up which pack BNW uses at those places
(majority), and refill the PR set with BNW's formations. BNW formations get new
party rows (2000 + SNES formation id) so event/boss battles, which the PR's
scripts call by party id, keep their original parties.
A PR party also holds its battle's background and music; the SNES takes the background from
the map. So a formation gets one party per look of the places it is fought (the set's own
vanilla background and music): the first at 2000 + f, the next at 4000 + 600 k + f (a WC seed
fought a factory formation in the Crumbling House on the factory's background with the battle
music the house doesn't play: the user's playtest, 2026-11-06).
"""
from collections import Counter, defaultdict

import prdata as P

FORMATIONS, PACKS = 0x0F6200, 0x0F4800
ZONES = {"WoB": 0x0F5400, "WoR": 0x0F5500}  # 64 zones x 4 terrain packs
MAP_PACKS, MAPS = 0x0F5600, 416
NEW_PARTY_BASE = 2000
VARIANT_BASE, VARIANT_STRIDE = 4000, 600    # past vanilla's parties (to 3410)
LOOK = ("battle_background_asset_id", "battle_bgm_asset_id")


def party_ids(f, rows):
    """The parties this porter made for SNES formation f, present in `rows` (first one first)."""
    out = [NEW_PARTY_BASE + f] if NEW_PARTY_BASE + f in rows else []
    k = 0
    while VARIANT_BASE + VARIANT_STRIDE * k + f in rows:
        out.append(VARIANT_BASE + VARIANT_STRIDE * k + f)
        k += 1
    return out


def next_party_id(f, rows, vanilla_rows):
    """The id formation f's next party takes (party_ids' order; port_chests' box copies share it)."""
    made = party_ids(f, rows)
    pid = NEW_PARTY_BASE + f if not made else VARIANT_BASE + VARIANT_STRIDE * (len(made) - 1) + f
    assert pid not in vanilla_rows and pid < 20000, f"party {pid}: vanilla's, or past the per-look range"
    return pid

# PR formation cells form a 3x3 grid: 1-3 front column, 4-6 middle, 7-9 back;
# within a column top/middle/bottom. Approximate SNES cell centres (x, y in 8px tiles):
CELL_XY = {1: (10, 6), 2: (10, 9), 3: (10, 12), 4: (7.5, 6), 5: (7.5, 9), 6: (7.5, 12),
           7: (5, 6), 8: (5, 9), 9: (5, 12)}


def formation(rom, f):
    r = rom.data[FORMATIONS + 15 * f:FORMATIONS + 15 * f + 15]
    return [((r[2 + s] | ((r[14] >> s & 1) << 8)), r[8 + s] >> 4, r[8 + s] & 15) for s in range(6) if r[1] >> s & 1]


def pack(rom, p):
    d = rom.data
    return [int.from_bytes(d[PACKS + 8 * p + 2 * i:PACKS + 8 * p + 2 * i + 2], "little") & 0x3FF for i in range(4)]


MAP_PROPS = 0x2D8F00  # 33 bytes per map; byte 5 bit 7 = random battles enabled


def locations(rom):
    """{place: pack id} for world zone/terrain slots and dungeon maps that have random battles."""
    out = {}
    for world, base in ZONES.items():
        for z in range(64):
            for t in range(4):
                out[(world, z, t)] = rom.data[base + 4 * z + t]
    for m in range(MAPS - 1):
        if rom.data[MAP_PROPS + 33 * m + 5] & 0x80:
            out[("map", m)] = rom.data[MAP_PACKS + m]
    return out


def party_members(row):
    return tuple(sorted(int(row[f"monster{k}"]) for k in range(1, 10) if row[f"monster{k}"] != "0"))


class EncounterPorter:
    def __init__(self, van, bnw, tables, log):
        self.van, self.bnw, self.log = van, bnw, log
        self.mp = tables.setdefault("monster_party", P.Table("monster_party"))
        self.ms = tables.setdefault("monster_set", P.Table("monster_set"))
        self.vmp = P.Table("monster_party")
        self.changed = {"monster_party": set(), "monster_set": set()}
        self.layouts = {}
        for r in self.vmp:  # vanilla layouts by line-up
            members = party_members(r)
            if members and members not in self.layouts:
                self.layouts[members] = r

    # --- matching -----------------------------------------------------------------
    def pack_fingerprint(self, rom, p):
        return tuple(sorted(tuple(sorted(e + 1 for e, _, _ in formation(rom, f))) for f in pack(rom, p)))

    def set_to_pack(self):
        by_fp = defaultdict(list)
        for p in range(256):
            by_fp[self.pack_fingerprint(self.van, p)].append(p)
        out = {}
        for r in self.ms:
            fp = tuple(sorted(party_members(self.vmp[int(r[f"monster_set{k}"])]) if int(r[f"monster_set{k}"]) in self.vmp.rows
                              else () for k in range(1, 5)))
            if any(fp) and len(by_fp.get(fp, [])) == 1:
                out[int(r["id"])] = by_fp[fp][0]
        return out

    # --- new parties for BNW formations -----------------------------------------------
    def party_for(self, f, template_row):
        """The party fighting formation f with the template's look (the set's background and
        music): one per formation and look."""
        look = tuple(template_row[c] for c in LOOK)
        for pid in party_ids(f, self.mp.rows):
            if tuple(self.mp.rows[pid][c] for c in LOOK) == look:
                return pid
        made = party_ids(f, self.mp.rows)
        pid = next_party_id(f, self.mp.rows, self.vmp.rows)
        if made:
            self.stats["parties for another look"] += 1
        row = self.party_row(f, template_row, pid)
        self.mp.rows[pid] = row
        self.changed["monster_party"].add(pid)
        self.stats["new parties"] += 1
        self.stats["layout copied" if self.last_layout else "layout from grid"] += 1
        return pid

    def party_row(self, f, template_row, pid):
        """A party row for formation f as the mod has it: the template's other columns (look,
        battle script, escape...), the formation's monsters on a vanilla party's hand-tuned layout
        when one has the same line-up, else each in the free grid cell nearest its SNES position;
        the formation's magic points (get_value). port_events builds its event parties with it."""
        enemies = formation(self.bnw, f)
        members = tuple(sorted(e + 1 for e, _, _ in enemies))
        row = dict(template_row, id=str(pid))
        layout = self.layouts.get(members)
        self.last_layout = layout
        for k in range(1, 10):
            for col, val in (("", "0"), ("_x_position", "0"), ("_y_position", "0"), ("_group", "1")):
                row[f"monster{k}{col}"] = val
        if layout:  # a vanilla formation with the same line-up: copy its hand-tuned layout
            for k in range(1, 10):
                for col in ("", "_x_position", "_y_position", "_group"):
                    row[f"monster{k}{col}"] = layout[f"monster{k}{col}"]
        else:  # place each enemy in the free grid cell nearest its SNES position
            free = set(CELL_XY)
            for e, x, y in sorted(enemies, key=lambda t: (-t[1], t[2])):
                if not free:
                    break
                cell = min(free, key=lambda c: (CELL_XY[c][0] - x) ** 2 + (CELL_XY[c][1] - y) ** 2)
                free.discard(cell)
                row[f"monster{cell}"] = str(e + 1)
        ap = self.bnw.data[0x1FB400 + f] if f < 512 else 0
        row["get_value"] = str(ap)
        return row

    def port(self):
        self.stats = Counter()
        vloc, bloc = locations(self.van), locations(self.bnw)
        places = defaultdict(list)
        for place, p in vloc.items():
            places[p].append(place)
        for sid, p in self.set_to_pack().items():
            bnw_packs = Counter(bloc[pl] for pl in places.get(p, []) if pl in bloc)
            if not bnw_packs:
                self.stats["set unused on SNES maps"] += 1
                continue
            p2, votes = bnw_packs.most_common(1)[0]
            if votes * 2 < sum(bnw_packs.values()):
                self.stats["no clear BNW pack (kept)"] += 1
                continue
            new_forms = pack(self.bnw, p2)
            if any(not formation(self.bnw, f) for f in new_forms):
                self.stats["BNW pack has an empty formation (kept)"] += 1  # likely "no encounters"; never send an empty battle
                continue
            if [x for x in new_forms] == pack(self.van, p) and all(formation(self.bnw, f) == formation(self.van, f) for f in new_forms):
                self.stats["unchanged"] += 1
                continue
            srow = self.ms[sid]
            template = self.vmp[int(srow["monster_set1"])] if int(srow["monster_set1"]) in self.vmp.rows else next(iter(self.vmp))
            for k, f in enumerate(new_forms, 1):
                srow[f"monster_set{k}"] = str(self.party_for(f, template))
            self.changed["monster_set"].add(sid)
            self.stats["sets refilled"] += 1
        self.log(f"encounters: {dict(self.stats)}")
        return self.changed

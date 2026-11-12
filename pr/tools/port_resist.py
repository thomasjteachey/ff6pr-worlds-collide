"""Port elemental properties, status immunities/protections and auto-statuses
for enemies and equipment. A PR group is only rebuilt when BNW's SNES bytes
differ from vanilla's, so the PR's own quirks survive everywhere else."""
import ff6snes as S
import groups as G
import idmap as M
import prdata as P

ELEM_ATTR = [1, 2, 3, 6, 8, 7, 4, 5]  # SNES element bit -> PR attribute id (fire ice bolt poison wind holy earth water)
ABSORB, NULL, HALF, WEAK = "4", "3", "2", "1"

# SNES status bit (0-15: status bytes 1-2) -> PR condition ids blocked/immune
PROTECT = {0: [9, 89], 1: [15], 2: [10, 88], 4: [33], 5: [13], 6: [11], 7: [5, 50, 68, 72, 82],
           8: [14], 10: [19], 11: [6], 12: [18, 91], 13: [12], 14: [16, 90], 15: [7]}
ENEMY_IMMUNE = {0: [9], 1: [15], 2: [10, 85], 4: [33], 5: [13], 6: [11], 7: [5, 50, 68, 72, 82],
                8: [14], 10: [33], 11: [6], 12: [18], 13: [12], 14: [16], 15: [7],
                17: [20], 18: [25], 19: [21], 20: [26], 21: [22], 22: [23], 23: [24]}
# Equipment auto-status byte (8) bit -> condition id (learned from vanilla)
AUTO_STATUS = {0: [17], 1: [20], 3: [21], 5: [22], 6: [23], 7: [92]}
# Enemy initial statuses (learned): bits of bytes 27..30
ENEMY_INITIAL = {0: [89], 2: [88], 4: [33], 12: [91], 14: [90], 15: [7], 16: [96], 19: [95],
                 20: [26], 21: [94], 22: [93], 23: [92], 25: [113]}


def elem_set(pairs):
    """pairs: [(byte, type)] -> {(attr, type)}"""
    out = set()
    for byte, kind in pairs:
        for b in range(8):
            if byte >> b & 1:
                out.add((ELEM_ATTR[b], kind))
    return out


def cond_set(mask, table, nbits):
    return {(c, "1") for b in range(nbits) if mask >> b & 1 for c in table.get(b, [])}


class ResistPorter:
    def __init__(self, van, bnw, tables, log):
        self.van, self.bnw, self.tables, self.log = van, bnw, tables, log
        self.ag = G.GroupTable("attribute_group", "attribute_id", ["type_id"],
                               tables.setdefault("attribute_group", P.Table("attribute_group")))
        self.cg = G.GroupTable("condition_group", "condition_id", ["type_id"],
                               tables.setdefault("condition_group", P.Table("condition_group")))
        self.changed = {"monster": set(), "weapon": set(), "armor": set()}

    def setv(self, table, rid, col, val):
        row = self.tables[table][rid]
        if row[col] != str(val):
            row[col] = str(val)
            self.changed[table].add(rid)

    def enemies(self):
        n = {"elements": 0, "immunity": 0, "initial": 0}
        for ev, eb in zip(S.enemies(self.van), S.enemies(self.bnw)):
            if S.blank_enemy(self.bnw, ev.id) and not S.blank_enemy(self.van, ev.id):
                continue                  # a slot BNW emptied keeps the PR's monster
            v, b, rid = ev.raw, eb.raw, ev.id + 1
            if v[23:26] != b[23:26]:
                gid = self.ag.find_or_create(elem_set([(b[23], ABSORB), (b[24], NULL), (b[25], WEAK)]))
                self.setv("monster", rid, "resistance_attribute", gid)
                n["elements"] += 1
            if v[20:23] != b[20:23]:
                gid = self.cg.find_or_create(cond_set(b[20] | b[21] << 8 | b[22] << 16, ENEMY_IMMUNE, 24))
                self.setv("monster", rid, "resistance_condition", gid)
                n["immunity"] += 1
            if v[27:30] != b[27:30]:  # byte 30 is special flags (Runic, Float...), not statuses
                gid = self.cg.find_or_create(cond_set(b[27] | b[28] << 8 | b[29] << 16, ENEMY_INITIAL, 24))
                self.setv("monster", rid, "initial_condition", gid)
                n["initial"] += 1
        self.log(f"enemy resistances: {n}")

    def equipment(self):
        n = {"elements": 0, "protect": 0, "auto": 0, "weapon_element": 0}
        for iv, ib in zip(S.items(self.van), S.items(self.bnw)):
            m = M.item(ib.id)
            if not m or m[0] == "item" or iv.type != ib.type:
                continue
            table, rid = m
            v, b = iv.raw, ib.raw
            if table == "weapon":
                if v[15] != b[15]:
                    attrs = [ELEM_ATTR[x] for x in range(8) if b[15] >> x & 1]
                    self.setv("weapon", rid, "attribute_id", attrs[0] if attrs else 0)
                    if len(attrs) > 1:
                        self.log(f"  weapon {ib.name}: multi-element {attrs}, kept first")
                    n["weapon_element"] += 1
                continue
            if (v[15], v[22], v[23], v[24]) != (b[15], b[22], b[23], b[24]):
                gid = self.ag.find_or_create(elem_set([(b[15], HALF), (b[22], ABSORB), (b[23], NULL), (b[24], WEAK)]))
                self.setv("armor", rid, "resistance_attribute", gid)
                n["elements"] += 1
            if v[6:8] != b[6:8]:
                gid = self.cg.find_or_create(cond_set(b[6] | b[7] << 8, PROTECT, 16))
                self.setv("armor", rid, "resistance_condition", gid)
                n["protect"] += 1
            if v[8] != b[8]:
                gid = self.cg.find_or_create(cond_set(b[8], AUTO_STATUS, 8))
                self.setv("armor", rid, "wear_condition_group_id", gid)
                n["auto"] += 1
        self.log(f"equipment resistances: {n}")

    def port(self):
        self.enemies()
        self.equipment()
        out = dict(self.changed)
        out["attribute_group"] = self.ag.new_rows
        out["condition_group"] = self.cg.new_rows
        self.log(f"  new group rows: attribute {len(self.ag.new_rows)}, condition {len(self.cg.new_rows)}")
        return out

"""Learn and build PR membership groups (attribute_group / condition_group) from SNES bitmasks.

PR stores "set of things" fields (element resistances, status immunities, auto
statuses...) as a group id pointing at rows (group_id, member, type). For each
SNES bit we learn which members it stands for by comparing vanilla records that
have the bit set against those that don't.
"""
from collections import Counter, defaultdict

import prdata as P


def learn_bits(pairs, nbits=32, min_share=0.8, max_noise=0.2):
    """pairs: [(snes_mask:int, frozenset(members))]. Returns {bit: frozenset(members)}."""
    with_bit, without_bit = defaultdict(Counter), defaultdict(Counter)
    n_with, n_without = Counter(), Counter()
    for mask, members in pairs:
        for b in range(nbits):
            if mask >> b & 1:
                n_with[b] += 1
                with_bit[b].update(members)
            else:
                n_without[b] += 1
                without_bit[b].update(members)
    out = {}
    for b in range(nbits):
        if not n_with[b]:
            continue
        chosen = {m for m, c in with_bit[b].items()
                  if c / n_with[b] >= min_share and (without_bit[b][m] / n_without[b] if n_without[b] else 0) <= max_noise}
        if chosen:
            out[b] = frozenset(chosen)
    return out


class GroupTable:
    """attribute_group (member col attribute_id) or condition_group (condition_id)."""

    def __init__(self, name, member_col, extra_cols, table=None):
        self.name, self.member_col, self.extra_cols = name, member_col, extra_cols
        self.t = table if table is not None else P.Table(name)
        self.groups = defaultdict(set)
        for r in self.t:
            self.groups[int(r["group_id"])].add(self.key(r))
        self.by_members = {}
        for g, ms in self.groups.items():
            self.by_members.setdefault(frozenset(ms), g)
        self.next_group = max(self.groups) + 1
        self.next_row = max(self.t.rows) + 1
        self.new_rows = set()

    def key(self, r):
        return (int(r[self.member_col]),) + tuple(r[c] for c in self.extra_cols)

    def members(self, gid):
        return frozenset(self.groups.get(gid, ()))

    def find_or_create(self, members, template_row=None):
        members = frozenset(members)
        if not members:
            return 0
        if members in self.by_members:
            return self.by_members[members]
        gid = self.next_group
        self.next_group += 1
        tmpl = template_row or next(iter(self.t))
        for m in sorted(members):
            rid = self.next_row
            self.next_row += 1
            row = {c: tmpl[c] for c in self.t.columns}
            row["id"], row["group_id"], row[self.member_col] = str(rid), str(gid), str(m[0])
            for c, v in zip(self.extra_cols, m[1:]):
                row[c] = v
            self.t.rows[rid] = row
            self.new_rows.add(rid)
        self.groups[gid] = set(members)
        self.by_members[members] = gid
        return gid

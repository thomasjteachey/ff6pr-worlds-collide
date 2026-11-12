"""Learn SNES-field -> PR-column value translations from vanilla pairs.

For every (SNES field, PR column) pair, collect what PR value each SNES value
became in vanilla. A pair is usable when the relation is (almost) a function:
each SNES value maps to one PR value in >= `purity` of the records. Applying
the table to BNW then converts its values the same way the PR's developers did.
"""
from collections import Counter, defaultdict


class FieldMap:
    def __init__(self, snes_field, pr_col, table, purity, coverage):
        self.snes_field = snes_field
        self.pr_col = pr_col
        self.table = table          # snes value -> pr value (string)
        self.purity = purity
        self.coverage = coverage    # records seen

    def convert(self, value):
        return self.table.get(value)


def learn(pairs, snes_fields, pr_cols, min_purity=0.95, identity_only=False):
    """pairs: list of (snes_record_dict, pr_row_dict). Returns {pr_col: FieldMap} picking the best SNES field."""
    best = {}
    for col in pr_cols:
        for f in snes_fields:
            rel = defaultdict(Counter)
            for s, p in pairs:
                rel[s[f]][p[col]] += 1
            total = sum(sum(c.values()) for c in rel.values())
            if not total:
                continue
            hits = sum(c.most_common(1)[0][1] for c in rel.values())
            purity = hits / total
            if purity < min_purity:
                continue
            table = {k: c.most_common(1)[0][0] for k, c in rel.items()}
            is_identity = all(str(k) == v for k, v in table.items())
            if identity_only and not is_identity:
                continue
            outputs = len(set(table.values()))
            if outputs < 2:
                continue  # PR column doesn't vary with this field: nothing to learn
            # Prefer the field that explains the most PR variety with the fewest inputs.
            score = (round(purity, 2), outputs, is_identity, -len(table))
            fm = FieldMap(f, col, table, purity, total)
            if col not in best or score > best[col][0]:
                best[col] = (score, fm)
    return {c: fm for c, (score, fm) in best.items()}

"""Dialogue alignment, sequence version (2026-11-05): SNES speaker turns -> PR story keys.

dialog_align.py's anchors locate each PR scene's window of SNES turns; an in-order DP aligns the
scene's lines to the window (pairs kept with word overlap >= 0.12, plus equal-size gaps between
kept pairs); scenes without anchors are seeded by their best pair (>= 0.35); a speaker check
(PR story_cha speaker vs the SNES "NAME:" prefix) adds or removes 0.2. Vanilla: 2418 of 4293 PR
lines, speakers agree on 95% where both name one. Off-by-one lines inside a scene remain (the PR
splits and merges speaker turns), so the port doesn't use it yet. PORTING.md, Plan item 6.

  .venv/Scripts/python tools/dialog_align_seq.py
"""
import collections
import random

import ff6snes as S
import prdata as P
import dialog_align as D

def group(k):
    if k[0] == 'E': return D.scene(k)
    if k[0] == 'N': return "_".join(k.split("_")[:2])
    return k.split("_")[0]

def dp(keys, turns, sim):
    n, m = len(keys), len(turns)
    S_ = [[0.0] * (m + 1) for _ in range(n + 1)]
    B = [[None] * (m + 1) for _ in range(n + 1)]
    for a in range(n + 1):
        for b in range(m + 1):
            if a == 0 and b == 0: continue
            best, bk = -1, None
            if a > 0 and S_[a-1][b] > best: best, bk = S_[a-1][b], 'k'
            if b > 0 and S_[a][b-1] > best: best, bk = S_[a][b-1], 't'
            if a > 0 and b > 0:
                s = sim(turns[b-1], keys[a-1])
                if s >= 0.05 and S_[a-1][b-1] + s > best: best, bk = S_[a-1][b-1] + s, 'm'
            S_[a][b], B[a][b] = best, bk
    a, b, out = n, m, []
    while a > 0 or b > 0:
        bk = B[a][b]
        if bk == 'm': out.append((keys[a-1], turns[b-1])); a -= 1; b -= 1
        elif bk == 'k': a -= 1
        else: b -= 1
    return out[::-1]


ALIASES = {"vicks": "biggs", "trooper": "soldier", "old man": "aged man", "kefka": "kefka", "gestahl": "gestahl"}
def norm_spk(s):
    s = s.strip()
    for k, v in D.TAGS.items(): s = s.replace(k, v)
    s = s.lower().strip()
    return ALIASES.get(s, s)
def speakers():
    out = {}
    for line in open(f"{P.VANILLA}/message/Assets/GameAssets/Serial/Data/Message/story_cha_en.txt", encoding="utf-8").read().splitlines():
        if "\t" in line:
            k, v = line.split("\t", 1)
            if v.strip(): out[k] = norm_spk(v)
    return out
def run(rom):
    base, segs, pr, sim0 = D.align(rom)
    spk_k = speakers()
    text = {(i, j): s for i, j, s in segs}
    def spk_t(t):
        m = D.SPEAKER.match(text[t]); return norm_spk(m.group(1)) if m else None
    def sim(t, k):
        s = sim0(t, k); a, b = spk_t(t), spk_k.get(k)
        if a and b: s += 0.2 if a == b else -0.2
        return s
    order = [(i, j) for i, j, _ in segs]; idx = {t: n for n, t in enumerate(order)}
    kg = collections.defaultdict(list)
    for k in sorted(pr, key=D.keyorder): kg[group(k)].append(k)
    turn_of = {k: t for t, k in base.items()}
    # candidate turns per PR line: turns sharing a rare word (for seeding anchorless groups)
    tw = collections.defaultdict(set)
    df = collections.Counter()
    tt = {t: set(D.toks(text[t])) for t in text}
    for t, ws in tt.items():
        for w in ws: tw[w].add(t); df[w] += 1
    def cand(k):
        ws = sorted(set(D.toks(pr[k])), key=lambda w: df.get(w, 10**9))[:4]
        out = set()
        for w in ws:
            if df.get(w, 0) <= 40: out |= tw[w]
        return out
    result, taken = {}, set()
    for g, ks in kg.items():
        anchors = [idx[turn_of[k]] for k in ks if k in turn_of]
        if not anchors:
            # seed: the best pair of any of the group's lines (raw word overlap + speaker), if strong
            best = (0.0, None)
            for k in ks:
                for t in cand(k):
                    sc = sim(t, k)
                    if sc > best[0]: best = (sc, t)
            if best[0] < 0.35: continue
            anchors = [idx[best[1]]]
        lo = max(0, min(anchors) - len(ks) - 2); hi = min(len(order), max(anchors) + len(ks) + 3)
        pairs = dp(ks, order[lo:hi], sim)
        good = [(k, t) for k, t in pairs if sim(t, k) >= 0.12 or turn_of.get(k) == t]
        acc = dict(good); kpos = {k: n for n, k in enumerate(ks)}
        gl = sorted(good, key=lambda p: kpos[p[0]])
        for (k1, t1), (k2, t2) in zip(gl, gl[1:]):
            gk = ks[kpos[k1] + 1:kpos[k2]]; gt = order[idx[t1] + 1:idx[t2]]
            if gk and len(gk) == len(gt): acc.update(zip(gk, gt))
        for k, t in acc.items():
            if t not in taken: result[k] = t; taken.add(t)
    both = [(k, t) for k, t in result.items() if spk_t(t) and spk_k.get(k)]
    agree = sum(1 for k, t in both if spk_t(t) == spk_k[k])
    return result, segs, pr, sim, both, agree, spk_t, spk_k
if __name__ == "__main__":
    res, segs, pr, sim, both, agree, spk_t, spk_k = run(S.Rom(P.VANILLA_ROM))
    print('PR lines mapped', len(res), 'of', len(pr), '| speaker known on both sides', len(both), 'agree', agree, f'({100*agree/max(1,len(both)):.0f}%)')
    dis = collections.Counter((spk_t(t), spk_k[k]) for k, t in both if spk_t(t) != spk_k[k])
    print('disagreements', dis.most_common(15))

"""Dialogue alignment, measurement only (2026-11-05): vanilla SNES dialogue -> PR story lines.

The PR keys its story text by event (story_mes_en: E<event>_<scene>_<num>_<variant>_<line>; N...
for townsfolk), one key per speaker turn, in a new translation; SNES dialogues are numbered and
can hold several speakers. To put a mod's rewritten dialogue (BNW: 99% of lines) into the PR,
each SNES dialogue must be tied to its PR key(s). This prototype: split SNES dialogues into
speaker turns (a page starting "NAME:"), match by TF-IDF word overlap (PR name tags such as
(TINA) read as the English name), keep confident unique matches as anchors, and fill gaps
between two anchors in the same PR scene in order when the counts agree.
Result (vanilla): 3344 SNES turns, 4293 PR lines; 1032 anchors + 217 gap fills = 1249 turns
mapped, 1244 PR lines covered (29%); 63 gap fills share few words (the PR reworded). Not used by
the port: too low to swap text without wrong lines.

  .venv/Scripts/python tools/dialog_align.py
"""
import collections
import math
import random
import re

import ff6snes as S
import prdata as P
import snes_dialog

TAGS = {"(TINA)": "terra", "(ROCK)": "locke", "(KAIEN)": "cyan", "(SHADOW)": "shadow", "(EDGAR)": "edgar",
        "(MASH)": "sabin", "(CELES)": "celes", "(STRAGUS)": "strago", "(RELM)": "relm", "(SETZER)": "setzer",
        "(MOG)": "mog", "(GAU)": "gau", "(GOGO)": "gogo", "(UMARO)": "umaro", "(KINGAKU)": "kefka"}
ALIAS = {"vicks": "biggs", "espers": "esper"}
STOP = set("the a an and or of to in on is it i you he she we they me my your his her our their be was were are "
           "am at for with that this what so but not no do don't it's i'm you're as by from up out all just".split())
SPEAKER = re.compile(r"^\s*([A-Z][A-Za-z.'\- ]{1,14}):")


def toks(s):
    for k, v in TAGS.items():
        s = s.replace(k, " " + v + " ")
    s = re.sub(r"<[^>]*>", " ", s).lower()
    return [ALIAS.get(x, x) for x in re.findall(r"[a-z][a-z']+|\d+", s) if x not in STOP]


def pr_lines():
    out = {}
    path = f"{P.VANILLA}/message/Assets/GameAssets/Serial/Data/Message/story_mes_en.txt"
    for line in open(path, encoding="utf-8").read().splitlines():
        if "\t" in line:
            k, v = line.split("\t", 1)
            if v.strip():
                out[k] = v
    return out


def tfidf(docs):
    df = collections.Counter()
    for t in docs.values():
        df.update(set(t))
    n, vec = len(docs), {}
    for k, t in docs.items():
        v = {w: (1 + math.log(f)) * math.log(n / df[w]) for w, f in collections.Counter(t).items()}
        norm = math.sqrt(sum(x * x for x in v.values())) or 1
        vec[k] = {w: x / norm for w, x in v.items()}
    return vec


def segments(text):
    segs = []
    for page in text.split("\n\n"):
        if not page.strip():
            continue
        if not segs or SPEAKER.match(page):
            segs.append(page)
        else:
            segs[-1] += "\n\n" + page
    return segs


def keyorder(k):
    parts = [k[0], k[1:].split("_")[0]] + k.split("_")[1:]
    return tuple((0, int(p), "") if p.isdigit() else (1, 0, p) for p in parts)


def scene(k):
    return "_".join(k.split("_")[:3])


def align(rom):
    """{(dialogue, turn): PR key}, the turns, the PR lines, a similarity function."""
    segs = [(i, j, s) for i, t in enumerate(snes_dialog.dialogs(rom)) for j, s in enumerate(segments(t))]
    pr = pr_lines()
    keys = sorted(pr, key=keyorder)
    pos = {k: n for n, k in enumerate(keys)}
    docs = {("s", i, j): toks(s) for i, j, s in segs}
    docs.update({("p", k): toks(v) for k, v in pr.items()})
    vec = tfidf(docs)
    inv = collections.defaultdict(list)
    for k, v in vec.items():
        if k[0] == "p":
            for w, x in v.items():
                inv[w].append((k[1], x))

    def sim(turn, key):
        va, vb = vec[("s",) + turn], vec[("p", key)]
        return sum(x * vb.get(w, 0) for w, x in va.items())

    best = {}
    for i, j, _ in segs:
        sc = collections.Counter()
        for w, x in vec[("s", i, j)].items():
            for k, y in inv.get(w, ()):
                sc[k] += x * y
        top = sc.most_common(2)
        if top:
            best[(i, j)] = (top[0][0], top[0][1], top[1][1] if len(top) > 1 else 0)
    anchors = {t: b[0] for t, b in best.items() if b[1] >= 0.45 and b[1] - b[2] >= 0.12}
    used = collections.Counter(anchors.values())
    anchors = {t: k for t, k in anchors.items() if used[k] == 1}
    order = [(i, j) for i, j, _ in segs]
    idx = {t: n for n, t in enumerate(order)}
    ranked = sorted(anchors.items(), key=lambda t: idx[t[0]])
    filled = {}
    for (s1, k1), (s2, k2) in zip(ranked, ranked[1:]):
        if scene(k1) != scene(k2) or pos[k2] <= pos[k1]:
            continue
        gap_s, gap_k = order[idx[s1] + 1:idx[s2]], keys[pos[k1] + 1:pos[k2]]
        if gap_s and len(gap_s) == len(gap_k):
            filled.update(zip(gap_s, gap_k))
    align.anchors, align.filled = len(anchors), len(filled)
    return {**anchors, **filled}, segs, pr, sim


if __name__ == "__main__":
    mapping, segs, pr, sim = align(S.Rom(P.VANILLA_ROM))
    print(f"turns {len(segs)}, PR lines {len(pr)}; anchors {align.anchors}, gap fills {align.filled}; "
          f"PR lines covered {len(set(mapping.values()))}")
    text = {(i, j): s for i, j, s in segs}
    random.seed(5)
    for t in random.sample(sorted(mapping), 6):
        k = mapping[t]
        print("---", t, round(sim(t, k), 2), "\n  SNES:", text[t][:100].replace("\n", " "), "\n  PR  :", k, pr[k][:100])

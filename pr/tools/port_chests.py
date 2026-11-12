"""Port BNW chest contents onto the PR's map chest entities.

PR chests are entities in map entity_default.json (action_id 6) with content_id
(content.csv id; 1 = Gil with content_num = amount) and a PR-renumbered flag_id. The PR keeps
some places twice (Figaro Castle 20051/20371, South Figaro 20071/20361...): the copies of one
chest share its flag_id, so a chest is a flag and every entity with it gets the contents.

Matching (match_flags, 2026-11-05): for each SNES map and PR map file, the offset under which
the most of its item/gil chests sit exactly on PR chests with the same contents; every SNES
chest of the map (empty and monster-in-a-box ones too) then takes the flag at its spot, the
strongest alignments first, one chest per flag (a SNES map can align with several files: the
PR splits some maps). The older content-only match (match) paired chests across maps that
share common items (map 226's Running Shoes took map 378's chest) and filled one copy only;
it is kept as a fallback for chests the layout can't place (one chest alone in a file), when
its PR group is one the layout match tied to the same SNES map. Every matched vanilla content
chest lands on PR contents equal to vanilla's.
BNW's changed chests are written into copies of those JSON files under the mod, keyed via the
game's AssetsPath.json; a vanilla monster-in-a-box that BNW made an item chest loses its battle
script (script_id 0, like every plain chest).
A box fights the mod's formation as the mod has it (seed_party): BNW renumbers formations (its
351 is vanilla's 465), so a formation's number alone names vanilla's fight there. An item chest
the mod made a box gets a box script of its own (new_box: the PR's box script, its own entry in
the map's script list). A chest the mod moved (the same event bit, its old spot empty in the
mod) is its old spot's PR chest (moved_chests).
"""
import glob
import json
import os
from collections import Counter, defaultdict

import ff6snes as S
import idmap as M
import prdata as P

PTRS, DATA, MAPS = 0x2D82F4, 0x2D8634, 416
MAP_PROPS = 0x2D8F00   # 33 bytes per SNES map; byte 0x17: BG1 width (bits 6-7), height (4-5): 16 << n tiles
ITEM, GOLD, EMPTY, MONSTER = 0x40, 0x80, 0x08, 0x20


def chest_type(byte):
    # BNW leaves old bits set (e.g. 0x48 = refilled empty chest), so classify by priority.
    for t in (ITEM, GOLD, MONSTER, EMPTY):
        if byte & t:
            return t
    return 0


def snes_chests(rom):
    d = rom.data
    ptr = [int.from_bytes(d[PTRS + 2 * m:PTRS + 2 * m + 2], "little") for m in range(MAPS + 1)]
    out = {}
    for m in range(MAPS - 1):  # the last pointer is the table end
        for c in range((ptr[m + 1] - ptr[m]) // 5):
            r = d[DATA + ptr[m] + 5 * c:DATA + ptr[m] + 5 * c + 5]
            # keyed by place: chests can share an event bit (vanilla) or get new ones (WC re-bits them)
            out[(m, r[0], r[1])] = {"map": m, "x": r[0], "y": r[1], "type": chest_type(r[3]), "contents": r[4],
                                    "bit": r[2] | (r[3] & 1) << 8, "id": ptr[m] // 5 + c}
            size = d[MAP_PROPS + 33 * m + 0x17]
            out[(m, r[0], r[1])]["size"] = (16 << (size >> 6 & 3), 16 << (size >> 4 & 3))   # the map's, in tiles
            if chest_type(r[3]) == MONSTER:   # a monster-in-a-box: its pack's first formation (miab_party)
                out[(m, r[0], r[1])]["formation"] = int.from_bytes(d[PACK2 + 4 * r[4]:PACK2 + 4 * r[4] + 2], "little") & 0x7FFF
    return out


_wc_areas = None


def wc_areas():
    """WC's chest areas (its data/area_chests.py: area name -> SNES chest ids in the chest
    table's order): {chest id: area}; {} without WC's sources (WC's checkout in the project,
    wc/ in the generator bundle)."""
    global _wc_areas
    if _wc_areas is None:
        _wc_areas = {}
        for d in (os.environ.get("FF6_WC_DIR"), os.path.join(P.ROOT, "wc"), os.path.join(P.ROOT, "..", "mods", "worlds-collide")):
            path = d and os.path.join(d, "data", "area_chests.py")
            if path and os.path.exists(path):
                import importlib.util
                spec = importlib.util.spec_from_file_location("wc_area_chests", path)
                mod = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(mod)
                _wc_areas = {i: area for area, ids in mod.area_chests.items() for i in ids}
                break
    return _wc_areas


# WC's chest areas (data/area_chests.py) -> the PR's areas (area.csv area_name, English): where
# a SNES chest may land. An area missing here: anywhere (as before WC's areas were used).
WC_PR_AREAS = {
    "Narshe School": ["Narshe"], "Narshe Inside WOB": ["Narshe"],
    "Narshe Mines WOB": ["Narshe Mines"], "Narshe Mines WOR": ["Narshe Mines"],
    "Figaro Castle": ["Figaro Castle"], "Figaro Castle Basement": ["Figaro Castle"],
    "South Figaro Cave WOB": ["South Figaro Cave"], "South Figaro Cave WOR": ["South Figaro Cave"],
    "South Figaro Outside WOB": ["South Figaro"], "South Figaro Outside WOR": ["South Figaro"],
    "South Figaro Inside/Basement": ["South Figaro"], "Duncan's House WOB": ["Duncan's Cabin"],
    "Mt. Kolts": ["Mt. Kolts"], "Returner's Hideout": ["The Returners' Hideout"],
    "Imperial Camp": ["Imperial Camp"], "Doma": ["Doma Castle", "Dreamscape - Doma Castle"],
    "Cyan's Dream Phantom Train": ["Dreamscape - Phantom Train", "Dreamscape"], "Phantom Train": ["Phantom Train"],
    "Mobliz Inside": ["Mobliz"], "Mobliz Bookshelf Room WOR": ["Mobliz"], "Mobliz Outside WOR": ["Mobliz"],
    "Serpent Trench": ["Serpent Trench", "Entrance to Serpent Trench"], "Nikeah": ["Nikeah"],
    "Kohlingen": ["Kohlingen"], "Coliseum Owner's House WOB": ["Dragon's Neck Cabin", "Coliseum"],
    "Zozo": ["Zozo"], "Mt. Zozo": ["Mt. Zozo"], "Owzer's Mansion": ["Owzer's Mansion", "Jidoor"],
    "Owzer's Basement": ["Owzer's Mansion"], "Albrook Outside": ["Albrook"], "Albrook Inside": ["Albrook"],
    "Albrook Dock": ["Albrook"], "Maranda": ["Maranda"], "Tzen Collapsing House": ["Crumbling House", "Tzen"],
    "Magitek Factory": ["Magitek Factory", "Magitek Research Facility"], "Thamasa Outside": ["Thamasa"],
    "Thamasa Strago's House": ["Thamasa"], "Thamasa Burning House": ["Burning Home", "Thamasa"],
    "Esper Mountain": ["Esper Mountain", "Esper Cave"], "Imperial Base": ["Imperial Observation Post"],
    "Cave To Sealed Gate": ["Cave to the Sealed Gate"], "Floating Continent": ["Floating Continent"],
    "Daryl's Tomb": ["Darill's Tomb"], "Veldt Cave WOR": ["Cave on the Veldt"],
    "Ancient Cave": ["Cave to the Ancient Castle", "Ancient Castle"], "Phoenix Cave": ["Phoenix Cave"],
    "Fanatic's Tower": ["Cultists' Tower"], "Zone Eater": ["Zone Eater's Belly"], "Umaro's Cave": ["Yeti's Cave"],
    "Kefka's Tower": ["Kefka's Tower"],
}
# WC's duplicate chests (data/chests.py duplicates: the second of each pair, a copy of the first
# on a map the SNES keeps twice: Mt. Kolts, Cyan's dream Doma, Kefka's Tower)
WC_DUPLICATES = {70, 71, 97, 98, 99, 100, 101, 102, 103, 203, 284, 285}
# A mod's chest on another map at a chest's old spot: the PR chest of where it was. WC moves the
# five chests of Thamasa's unused pre-Leo map (343) onto the town's two maps (340 WoB, 344 WoR,
# the same spots); the PR keeps them in its one Thamasa (Map_20270), matched to 343's
MOVED_MAPS = {340: 343, 344: 343}
_area_ids = None


def pr_areas_of(wc_area):
    """The PR area ids a WC chest area may be in (None: unknown, anywhere)."""
    global _area_ids
    if _area_ids is None:
        sysm = P.messages("system", "en")
        _area_ids = defaultdict(set)
        for r in P.Table("area"):
            _area_ids[P.strip_tags(sysm.get(r["area_name"], ""))].add(r["id"])
    names = WC_PR_AREAS.get(wc_area)
    return None if names is None else set().union(*(_area_ids.get(n, set()) for n in names))


_pr_areas = None


def pr_area(file):
    """The PR map's area (map.csv area_id) of a map file (…/Map_20011_11/entity_default.json)."""
    global _pr_areas
    if _pr_areas is None:
        _pr_areas = {r["map_name"]: r["area_id"] for r in P.Table("map")}
    return _pr_areas.get(os.path.basename(os.path.dirname(file)))


# the message a chest shows when opened (system text T0001_*): the PR picks it by the chest's
# message_key, not its contents, so a chest WC turns from gil into an item (or back) needs the
# other key ("Obtained (NUM) gil!" on an item chest showed the raw (NUM): the user's playtest)
MSG_ITEM, MSG_GIL, MSG_MONSTER, MSG_EMPTY = "T0001_01_01", "T0001_03_01", "T0001_04_01", "T0001_05_01"


def chest_message(cid, num):
    """Obtained (ITEM_N)! / Obtained (NUM) gil! / It's empty."""
    if cid == 1:
        return MSG_GIL if num > 0 else MSG_EMPTY
    return MSG_ITEM


def content_key(chest):
    if chest["type"] == ITEM:
        return ("content", M.item_content(chest["contents"]))
    if chest["type"] == GOLD:
        return ("gil", chest["contents"] * 100)
    if chest["type"] == EMPTY:
        return ("gil", 0)   # the PR's empty chests hold 0 gil
    return None


_pr_chests = None


def pr_chests():
    """Every PR chest entity (vanilla files; read once per run: the scan over every map folder
    is the slowest part of a seed's port, and a run asks for it several times)."""
    global _pr_chests
    if _pr_chests is not None:
        return _pr_chests
    _pr_chests = _scan_chests()
    return _pr_chests


def _scan_chests():
    import vcache
    found = vcache.cached("chests_v4", _scan_chests_now)
    for c in found:
        c["file"] = os.path.join(P.VANILLA, c["file"])
        c["key"] = tuple(c["key"])
    return found


def _scan_chests_now():
    # a map's chests: its default entities, and an event's entity layer (ev_*.json, set by its
    # scripts): the PR keeps some rooms' WoB and WoR chests there, each version its own file
    # sharing the flags (the Narshe mines' ev_e_0001 / ev_e_0001_1, South Figaro's cave, the
    # Imperial Camp's Star Pendant: 13 chests no port reached before, test_wc_xref)
    out = []
    files = glob.glob(os.path.join(P.VANILLA, "map_*", "**", "entity_default.json"), recursive=True) \
        + glob.glob(os.path.join(P.VANILLA, "map_*", "**", "ev_*.json"), recursive=True)
    for f in sorted(files):
        text = open(f, encoding="utf-8").read()
        if '"flag_id"' not in text or '"content_id"' not in text:
            continue
        for layer in json.loads(text).get("layers", []):
            for o in layer.get("objects", []) or []:
                p = {x["name"]: x["value"] for x in o.get("properties", [])}
                if p.get("action_id") == 6 and "content_id" in p:
                    key = ("gil", p.get("content_num")) if p["content_id"] == 1 else ("content", p["content_id"])
                    group = os.path.relpath(f, P.VANILLA).replace("\\", "/").split("/")[0]
                    out.append({"file": os.path.relpath(f, P.VANILLA), "group": group, "obj": o["id"], "key": key, "x": o.get("x", 0), "y": o.get("y", 0),
                                "script": p.get("script_id", 0)})
    out += [dict(e, file=os.path.relpath(e["file"], P.VANILLA)) for e in empty_chests()]
    return out


def scripted_key(ms):
    """A scripted chest's contents as the matcher keys them (content_key), or None: "Empty!" is
    0 gil, else the one GetItem after the chests' own "Obtained" line."""
    msgs = [m["operands"]["sValues"][0] for m in ms if m["mnemonic"] == "Msg"]
    if EMPTY_LINE in msgs:
        return ("gil", 0)
    gets = [m["operands"]["iValues"][:2] for m in ms if m["mnemonic"] == "GetItem"]
    if MSG_ITEM in msgs and len(gets) == 1:
        cid, num = gets[0]
        return ("gil", num) if cid == 1 else ("content", cid)
    return None


def empty_chests():
    """The PR's scripted chests: no chest entity but an event's layer (ev_e_N; its opened twin
    ..._1) whose trigger runs sc_e_N, which sets the chest's treasure flag and says "Empty!" (the
    SNES's empty chests: ev_e_no_treasure_N, South Figaro's basement, Albrook's two, Phoenix Cave's
    five; Narshe's Lone Wolf chest) or gives an item with the chests' own "Obtained" line (the
    Phantom Train's Tents on its luggage racks). As chests they are the matcher's (scripted_key); a
    seed rewrites the script ("empty": its path) with what it puts there (fill_empty). Paths are
    absolute."""
    out = []
    for f in sorted(glob.glob(os.path.join(P.VANILLA, "map_*", "**", "ev_e_*.json"), recursive=True)):
        if f.endswith("_1.json"):
            continue
        script = os.path.join(os.path.dirname(f), "sc_" + os.path.basename(f)[3:])
        if not os.path.exists(script):
            continue
        ms = json.load(open(script, encoding="utf-8"))["Mnemonics"]
        flag = next((m["operands"]["iValues"][0] + 256 * (int(m["operands"]["sValues"][0][-1]) - 1) for m in ms
                     if m["mnemonic"] == "SetFlag" and m["operands"]["sValues"][0].startswith("TreasureFlag")), None)
        key = scripted_key(ms) if flag is not None else None
        if key is None:
            continue
        group = os.path.relpath(f, P.VANILLA).replace("\\", "/").split("/")[0]
        for layer in json.load(open(f, encoding="utf-8")).get("layers", []):
            for o in layer.get("objects", []) or []:
                p = {x["name"]: x["value"] for x in o.get("properties", [])}
                if p.get("object_type") == 5:
                    out.append({"file": f, "group": group, "obj": o["id"], "key": key, "x": o.get("x", 0), "y": o.get("y", 0),
                                "script": 0, "empty": os.path.relpath(script, P.VANILLA), "flag": flag})
    return out


EMPTY_LINE = "E0432_00_016_a_01"   # the empty chest's "Empty!"


# A chest the PR dropped beside one it kept as a pick-up: its contents join that pick-up's (the
# Phantom Train's rack: SNES chests 109 and 110 side by side, the PR's one Tent there).
MERGED = {(153, 21, 7): (153, 20, 7)}


_contents = None


def gift_ops(cid, num):
    """A scripted chest's gift. A WC seed: the WC plugin's WC:item / WC:gil (they set WC_REWARD's
    line; headless-tested). Any other mod has no WC plugin: the game's own ops, as vanilla's story
    pick-ups give theirs (sc_e_1106: Tag + "Obtained (ITEM_N)!" + GetItem; sc_e_0453 gives gil with
    GetItem content 1, headless-tested: 293 gil)."""
    import pr_script as S
    global _contents
    if "WC_FLAGS" in os.environ:
        return [S.op("SysCall", s=[f"WC:item {cid} {num}" if cid != 1 else f"WC:gil {num}"]), S.op("Msg", s=["WC_REWARD"])]
    if cid == 1:
        return [S.op("SetGilTag", i=[num]), S.op("Msg", s=[MSG_GIL]), S.op("GetItem", i=[1, num])]
    if _contents is None:
        _contents = P.Table("content")
    return [S.op("Tag", s=["ITEM_N", _contents[cid]["mes_id_name"]]), S.op("Msg", s=[MSG_ITEM]), S.op("GetItem", i=[cid, num])]


def fill_empty(script_rel, gifts, out_dir):
    """A seed's contents in a scripted chest's script (empty_chests): gifts [(content id, num)],
    each given by the plugin's WC:item / WC:gil with WC_REWARD's line, as a check's reward does
    (the script's own GetItem gave nothing in the headless test). They take the place of an empty
    chest's "Empty!" (nothing to give: it stays) or of a pick-up's line and GetItem (nothing to
    give: "Empty!"). Writes the seed's copy; returns whether it changed."""
    gifts = [(cid, num) for cid, num in gifts if cid != 1 or num]
    import pr_script as S
    path = os.path.join(P.VANILLA, *script_rel.replace("\\", "/").split("/"))
    sc = json.load(open(path, encoding="utf-8"))
    ms = sc["Mnemonics"]
    empty = [i for i, m in enumerate(ms) if m["mnemonic"] == "Msg" and m["operands"]["sValues"][0] == EMPTY_LINE]
    if empty:
        if not gifts:
            return False
        at = last = empty[0]
    else:
        last = next(i for i, m in enumerate(ms) if m["mnemonic"] == "GetItem")
        at = max(i for i in range(last) if ms[i]["mnemonic"] == "Msg")
        if at and ms[at - 1]["mnemonic"] == "Tag":
            at -= 1                                              # the line's item name
    give = []
    for cid, num in gifts:
        give += gift_ops(cid, num)
    S.hook(sc, at, "wc_filled", (give or [S.op("Msg", s=[EMPTY_LINE])]) + [S.op("Jump", i=[last])])
    dest = os.path.join(out_dir, *script_rel.replace("\\", "/").split("/"))
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(sc, ensure_ascii=False, separators=(",", ":")))
    return True


def match(vanilla):
    entities = pr_chests()
    by_group = defaultdict(list)
    for e in entities:
        by_group[e["group"]].append(e)
    by_map = defaultdict(list)
    for bit, c in vanilla.items():
        if content_key(c):
            by_map[c["map"]].append((bit, c))
    pairs = {}
    used = set()
    for m, chests in by_map.items():
        want = Counter(content_key(c) for _, c in chests)
        best, score = None, 0
        for g, ents in by_group.items():
            have = Counter(e["key"] for e in ents if (e["file"], e["obj"]) not in used)
            s = sum((want & have).values())
            if s > score:
                best, score = g, s
        if not best or score * 2 < len(chests):
            continue
        pool = sorted((e for e in by_group[best] if (e["file"], e["obj"]) not in used), key=lambda e: (e["y"], e["x"]))
        for bit, c in sorted(chests, key=lambda bc: (bc[1]["y"], bc[1]["x"])):
            for e in pool:
                if e["key"] == content_key(c) and (e["file"], e["obj"]) not in used:
                    pairs[bit] = e
                    used.add((e["file"], e["obj"]))
                    break
    return pairs


def match_by_position(chests, pairs, vanilla):
    """Chests the content match can't pair (empty or monster-in-a-box in vanilla, or contents the
    PR changed): the PR entity at the same spot. SNES tiles are 16 px; each map's offset comes
    from the chests already paired there."""
    entities = pr_chests()
    used = {(e["file"], e["obj"]) for e in pairs.values()}
    offsets = defaultdict(list)
    groups = defaultdict(Counter)
    for bit, e in pairs.items():
        c = vanilla[bit]
        offsets[c["map"]].append((e["x"] - 16 * c["x"], e["y"] - 16 * c["y"]))
        groups[c["map"]][e["group"]] += 1
    files = defaultdict(set)
    for bit, e in pairs.items():
        files[vanilla[bit]["map"]].add(e["file"])
    out = {}
    for bit, c in chests.items():
        offs = offsets.get(c["map"])
        if bit in pairs or not offs:
            continue
        dxs, dys = sorted(o[0] for o in offs), sorted(o[1] for o in offs)
        if dxs[-1] - dxs[0] > 8 or dys[-1] - dys[0] > 8:
            continue  # the map's pairs disagree on the offset: don't guess
        x, y = 16 * c["x"] + dxs[len(dxs) // 2], 16 * c["y"] + dys[len(dys) // 2]
        near = sorted((abs(e["x"] - x) + abs(e["y"] - y), e["obj"], e) for e in entities
                      if e["file"] in files[c["map"]] and (e["file"], e["obj"]) not in used
                      and abs(e["x"] - x) <= 8 and abs(e["y"] - y) <= 8 and not e["script"])
        if near:
            out[bit] = near[0][2]
            used.add((near[0][2]["file"], near[0][2]["obj"]))
    return out


def _flags(entities):
    cache = {}
    for e in entities:
        if e["file"] not in cache:
            cache[e["file"]] = {o["id"]: {x["name"]: x["value"] for x in o.get("properties", [])}
                                for layer in json.load(open(e["file"], encoding="utf-8")).get("layers", [])
                                for o in (layer.get("objects", []) or [])}
        e["flag"] = cache[e["file"]].get(e["obj"], {}).get("flag_id", e.get("flag"))   # an empty chest's: its script's
    return entities


_pr_sizes = {}
SIZES_FILE = "pr_map_sizes.json"   # the chest folders' sizes, for the in-game generator (tools/wc_bundle.py:
                                   # it carries no tilemaps; without them the Phantom Train's Tents crossed)
_shipped_sizes = None


def pr_size(folder):
    """A PR map folder's size in tiles (its tilemap.json, else the shipped SIZES_FILE), or None."""
    global _shipped_sizes
    if folder not in _pr_sizes:
        try:
            doc = json.load(open(os.path.join(folder, "tilemap.json"), encoding="utf-8"))
            _pr_sizes[folder] = (int(doc["width"]), int(doc["height"]))
        except (OSError, ValueError, KeyError):
            if _shipped_sizes is None:
                path = os.path.join(os.path.dirname(os.path.abspath(__file__)), SIZES_FILE)
                _shipped_sizes = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}
            size = _shipped_sizes.get(os.path.relpath(folder, P.VANILLA).replace("\\", "/"))
            _pr_sizes[folder] = tuple(size) if size else None
    return _pr_sizes[folder]


def chest_folder_sizes():
    """{PR map folder (relative to pr/vanilla): [width, height]} for every folder holding a chest
    (what the bundle ships as SIZES_FILE)."""
    folders = {os.path.dirname(e["file"]) for e in pr_chests()}
    return {os.path.relpath(f, P.VANILLA).replace("\\", "/"): list(pr_size(f)) for f in sorted(folders) if pr_size(f)}


def match_flags(vanilla, tol=8, changed=None):
    """SNES chest bit -> [PR entities of its flag] (see the module notes). changed: the bits
    the mod changed, which win a chest the PR shares between two SNES maps."""
    ents = _flags(pr_chests())
    # a monster-in-a-box is a box in both: the PR's box entity holds 0 gil, as an empty chest does,
    # and one-chest maps had paired an empty SNES chest with Narshe's Lobo box (a seed's item for
    # that chest landed on the box)
    boxes = miab_scripts()
    for e in ents:
        e["battle"] = boxes.get(script_name(e["script"])) if e["script"] else None

    def fits(c, e):
        return (c["type"] == MONSTER) == bool(e["battle"])

    # a map's offset is voted over all its layers (a PR map folder): the PR keeps some chests in
    # layers of one chest each (the empty ones, empty_chests), which alone never outvote a wrong
    # offset (Phoenix Cave's two floors swapped two empties); a flag is still claimed per file
    by_dir = defaultdict(list)
    for e in ents:
        by_dir[os.path.dirname(e["file"])].append(e)
    by_map = defaultdict(list)
    for bit, c in vanilla.items():
        by_map[c["map"]].append((bit, c))
    cands = []
    for m, chests in by_map.items():
        keyed = [(bit, c) for bit, c in chests if content_key(c)]
        for d, fe in by_dir.items():
            offs = Counter()
            for bit, c in keyed:
                for e in fe:
                    if e["key"] == content_key(c):
                        offs[(e["x"] - 16 * c["x"], e["y"] - 16 * c["y"])] += 1
            for (dx, dy), n in offs.most_common(2):
                cands.append((n, m, d, dx, dy, len(keyed)))
    cands.sort(key=lambda t: -t[0])

    def size_off(m, d):
        # how far a PR folder's size is from the SNES map's: the weak alignments' tie-break (the
        # Phantom Train's 32x16 car and its 32x32 one each keep a Tent on the same spot)
        sz, pr = next((c.get("size") for _, c in by_map[m]), None), pr_size(d)
        return abs(sz[0] - pr[0]) + abs(sz[1] - pr[1]) if sz and pr else 0
    # Where a chest may land: WC names each SNES chest's area (wc_areas), the PR each map's
    # (area.csv), WC_PR_AREAS joins the two. Without it single-chest SNES maps from all over took
    # Narshe's and South Figaro's one-chest houses (common items: Elixir, Potion...) and
    # Narshe's own chests went unrandomized (test_wc_xref).
    areas = wc_areas()

    def allowed(bit):
        a = areas.get(vanilla[bit].get("id"))
        return pr_areas_of(a) if a else None

    def where_ok(bit, f):
        ok = allowed(bit)
        return ok is None or pr_area(f) in ok

    # A flag is claimed per PR file. The SNES keeps a place's two versions as two maps whose
    # chests share event bits (South Figaro outside: WoR map 74, WoB 75, 8 chests), and so does
    # the PR (map_20360 / map_20070, one flag per spot), each version with its own contents: each
    # SNES chest takes its flag's entities in the file it lines up with (one SNES chest per flag
    # had given both PR versions the WoR chest's contents and dropped the WoB chest's). A copy no
    # other SNES chest lines up with (Figaro Castle's two PR versions of one SNES map) goes to the
    # flag's first claimant, inside its area.
    claim, owner, groups, first = {}, {}, defaultdict(set), {}

    def take(bit, e, f):
        claim[bit], owner[(e["flag"], f)] = e["flag"], bit
        first.setdefault(e["flag"], bit)
        groups[vanilla[bit]["map"]].add(e["group"])

    for strong in (True, False):
        # the weak ones nearest first (the PR mostly keeps a room's coordinates, a tile off)
        for n, m, d, dx, dy, keyed in (cands if strong else sorted(cands, key=lambda t: (abs(t[3]) + abs(t[4]), size_off(t[1], t[2])))):
            if (n >= 2) != strong:
                continue
            for bit, c in by_map[m]:
                if not strong and c.get("id") in WC_DUPLICATES:
                    continue                       # WC keeps it a copy of another chest
                x, y = 16 * c["x"] + dx, 16 * c["y"] + dy
                near = [e for e in by_dir[d] if abs(e["x"] - x) <= tol and abs(e["y"] - y) <= tol and e["flag"] is not None
                        and fits(c, e)]
                if not near:
                    continue
                # a folder may hold a place's two versions on the same spots (the Narshe mines'
                # WoB and WoR layers): an entity no one claimed yet, the one whose contents agree
                pool = [e for e in near if (e["flag"], e["file"]) not in owner] or near
                e = min(pool, key=lambda e: (e["key"] != content_key(c), abs(e["x"] - x) + abs(e["y"] - y)))
                f = e["file"]
                if bit in claim or (e["flag"], f) in owner or not where_ok(bit, f):
                    continue
                # one chest lines up (a PR house of one chest, a one-chest SNES map): only that
                # chest (another one landing near an entity under its offset is chance), and for
                # a chest of a bigger SNES map only where its area is known
                if not strong and (e["key"] != content_key(c) or (keyed > 1 and allowed(bit) is None)):
                    continue
                take(bit, e, f)
    # A monster-in-a-box the layout can't place: the PR renumbered the boxes' flags and their maps
    # mostly hold no other chest to line up with (Umaro's Cave, Daryl's Tomb, Esper Mountain,
    # Kefka's Tower, Narshe's school). The PR box that fights the same party, else the only box
    # left in its area (Narshe's Lobo box: the PR made its own party for it, 516).
    for bit, c in sorted(vanilla.items(), key=lambda kv: kv[1]["id"]):
        if c["type"] != MONSTER or bit in claim:
            continue
        free = [e for e in ents if e["battle"] and e["flag"] is not None and (e["flag"], e["file"]) not in owner
                and where_ok(bit, e["file"])]
        party = M.party(c["formation"]) if "formation" in c else None
        pick = [e for e in free if party in e["battle"]]
        if not pick and allowed(bit) is not None:
            pick = free
        if len({e["flag"] for e in pick}) == 1:
            take(bit, pick[0], pick[0]["file"])
    # fallback: the content match, where it stays inside a group the layout tied to that map
    # and in its area
    fallback, taken = 0, 0
    flag_of = {(x["file"], x["obj"]): x["flag"] for x in ents}
    content_pairs = match(vanilla)
    for bit, e in content_pairs.items():
        m = vanilla[bit]["map"]
        fl = flag_of.get((e["file"], e["obj"]))
        if bit in claim or fl is None or (fl, e["file"]) in owner or (groups[m] and e["group"] not in groups[m]) \
                or not where_ok(bit, e["file"]) or e["battle"]:
            continue
        take(bit, dict(e, flag=fl), e["file"])
        fallback += 1
    # The PR merged some SNES map pairs (a place's two versions: maps 72/110) into one map with
    # one set of chests, so one PR chest stands for two SNES chests at the same spot. When the mod
    # changed one of them and not the one holding the flag, the changed one takes it.
    if changed:
        for bit, e in content_pairs.items():
            m = vanilla[bit]["map"]
            fl = flag_of.get((e["file"], e["obj"]))
            holder = owner.get((fl, e["file"]))
            # (two versions of one spot only: a content-only pair elsewhere had taken the chests
            # WC happened to leave as they were, Phoenix Cave's)
            if (bit in claim or bit not in changed or fl is None or holder is None or holder in changed or e["battle"]
                    or (vanilla[bit]["x"], vanilla[bit]["y"]) != (vanilla[holder]["x"], vanilla[holder]["y"])
                    or (groups[m] and e["group"] not in groups[m]) or not where_ok(bit, e["file"])):
                continue
            del claim[holder]
            for k in [k for k, b in owner.items() if b == holder]:
                del owner[k]
            claim[bit], owner[(fl, e["file"])] = fl, bit
            if first.get(fl) == holder:
                first[fl] = bit
            taken += 1
    instances = defaultdict(list)
    for e in ents:
        if e["flag"] is not None:
            instances[e["flag"]].append(e)
    match_flags.fallback, match_flags.taken = fallback, taken
    out = {}
    for bit, fl in claim.items():
        out[bit] = [e for e in instances[fl]
                    if owner.get((fl, e["file"])) == bit
                    or ((fl, e["file"]) not in owner and first.get(fl) == bit and where_ok(bit, e["file"]))]
    return out


PACK2 = 0xF5000   # SNES event battle groups: 2 formations x 2 bytes; a monster-in-a-box holds its index


def same_chest(v, b):
    """A chest as vanilla had it: the same contents, and a box the same battle (WC's -fer changes
    a pack's formations in place, the chest keeping its pack)."""
    return (v["type"], v["contents"]) == (b["type"], b["contents"]) and v.get("formation") == b.get("formation")


def box_scripts(flags, vanilla):
    """The PR scripts of the SNES's monster-in-a-box chests (flags: match_flags'), which the port
    decides (the plugin's event remap leaves them alone: it goes by party, and a box's party can be
    an event's too)."""
    return sorted({script_name(e["script"]) for k, c in vanilla.items() if c["type"] == MONSTER
                   for e in flags.get(k, []) if e["script"] and script_name(e["script"]) in miab_scripts()})


def miab_party(rom, contents):
    """The PR monster party a monster-in-a-box chest fights (its pack's first formation)."""
    return M.party(int.from_bytes(rom.b(PACK2 + 4 * contents, 2), "little") & 0x7FFF)


_scripts = None


def script_name(script_id):
    """A chest entity's script_id -> its script name (master script.csv)."""
    global _scripts
    if _scripts is None:
        import csv
        _scripts = {r[0]: r[1] for r in csv.reader(open(os.path.join(P.MASTER, "script.csv"), encoding="utf-8"))}
    return _scripts.get(str(script_id))


BATTLES = ("Encount", "EncountBoss")   # a box's battle (Kefka's Tower's two boxes: EncountBoss)
_miabs = None


def miab_scripts():
    """Monster-in-a-box chest scripts (the ones that start a battle): {name: [parties]}."""
    global _miabs
    if _miabs is not None:
        return _miabs
    import pr_script as S
    out = {}
    for sid in sorted({e["script"] for e in pr_chests() if e["script"]}):
        name = script_name(sid)
        if not name:
            continue
        try:
            sc = S.load(name)
        except (KeyError, OSError):
            continue
        parties = [m["operands"]["iValues"][0] for m in sc.get("Mnemonics", []) if m.get("mnemonic") in BATTLES]
        if parties:
            out[name] = parties
    _miabs = out
    return out


def asset_keys():
    data = json.load(open(os.path.join(P.VANILLA, "assetspath", "Assets", "GameAssets", "AssetsPath.json"), encoding="utf-8"))
    out = {}
    for group, inner in zip(data["keys"], data["values"]):
        inner = json.loads(inner) if isinstance(inner, str) else inner
        for k, v in zip(inner["keys"], inner["values"]):
            out[(group, v)] = k
    return out


BG = "battle_background_asset_id"
_vanilla_parties = None


def box_look(tables, f, party, box, made):
    """The party a box fights for SNES formation f: `party`, on the box's own background where
    that differs (the SNES fights a box on its map's background, the PR on the party's: Narshe's
    Lobo box with a Phantom Train formation had fought in the train); the music stays the
    formation's, as the SNES plays it. A differing one is a copy under port_encounters'
    per-formation ids (party_ids: the AI's party checks resolve those too); `made` gets new ids."""
    import port_encounters as E
    mp = tables.setdefault("monster_party", P.Table("monster_party"))
    row, own = mp.rows.get(party), mp.rows.get(box)
    if row is None or own is None or row[BG] == own[BG]:
        return party
    want = dict(row, **{BG: own[BG]})
    for pid in E.party_ids(f, mp.rows):          # one made already (another box, an encounter set's)
        if all(mp.rows[pid].get(k) == v for k, v in want.items() if k != "id"):
            return pid
    global _vanilla_parties
    if _vanilla_parties is None:
        _vanilla_parties = P.Table("monster_party").rows
    pid = E.next_party_id(f, mp.rows, _vanilla_parties)
    mp.rows[pid] = dict(want, id=str(pid))
    made.add(pid)
    return pid


def moved_chests(vc, bc, flags):
    """A mod's chest at a spot where vanilla has none, whose event bit is one vanilla chest's that
    the mod no longer has at its own spot: that chest moved (on its map, or to its map's twin),
    so its PR chest is the old spot's. BNW: WoR Narshe's Moogle Charm two tiles up (inside the
    PR room's wall: the PR's chest stays put), the Ancient Castle box from map 406 to its twin 408."""
    out = {}
    for k, b in bc.items():
        if k in vc or k in flags:
            continue
        old = [vk for vk, v in vc.items() if v["bit"] == b["bit"] and vk not in bc and vk in flags]
        if len(old) == 1:
            out[k] = old[0]
    return out


def lineup(rom, f):
    """SNES formation f's monsters as PR monster ids, sorted (port_encounters' party_members)."""
    import port_encounters as E
    return tuple(sorted(e + 1 for e, _, _ in E.formation(rom, f)))


_lineups = None


def seed_party(van, rom, f, party, tables, porter, look):
    """The party fighting the seed's formation f, when the seed edited f (its line-up isn't
    vanilla's f): `party` (idmap.party's, or the PR's own box party) holds vanilla's fight there.
    A vanilla formation with the seed's line-up: its PR party (the PR's layout and music; BNW's
    351 is vanilla's 465); else a new party (the encounter porter's party_for, on `look`'s
    background and music). An unedited formation keeps `party` (every WC seed's)."""
    import port_encounters as E
    want = lineup(rom, f)
    if not want or want == lineup(van, f):
        return party
    mp = tables["monster_party"]
    if party in mp.rows and E.party_members(mp.rows[party]) == want:
        return party
    global _lineups
    if _lineups is None:
        _lineups = defaultdict(list)
        for g in range(576):
            _lineups[lineup(van, g)].append(g)
    for g in _lineups.get(want, []):
        pid = M.party(g)
        if pid in mp.rows and E.party_members(mp.rows[pid]) == want:
            return pid
    return porter().party_for(f, mp.rows[look if look in mp.rows else party])


_map_sets = None


def map_party(sub):
    """The first party of a PR map's random encounters (map.csv monster_set_id), or None: the look
    (background) a box made there fights on, as the SNES fights a box on its map's."""
    global _map_sets
    if _map_sets is None:
        sets = P.Table("monster_set").rows
        _map_sets = {}
        for r in P.Table("map"):
            s = sets.get(int(r["monster_set_id"] or 0))
            if s and int(s["monster_set1"] or 0):
                _map_sets[r["map_name"]] = int(s["monster_set1"])
    return _map_sets.get(sub)


BOX_TEMPLATE = "sc_e_0641"   # the PR's box script: TreasureBox [entity], "Monster-in-a-box!", Encount, Exit
BOX_SCRIPT_IDS = 9800        # new box scripts: 9801 up (script.csv's own end at 9029; merchants 9901 up)


def new_box(e, party, out_dir):
    """A box script for PR chest entity e (an item chest the mod made a box): the PR's box script
    (BOX_TEMPLATE) opening e (its entity_id), fighting `party`, then its after-battle script (the
    fade back in), both in the map's script list. Returns (script name, its after-battle
    script's, the map group's folder, the map)."""
    import copy
    import pr_script as S
    doc = json.load(open(e["file"], encoding="utf-8"))
    obj = next(o for l in doc["layers"] for o in (l.get("objects") or []) if o["id"] == e["obj"])
    entity = next(p["value"] for p in obj["properties"] if p["name"] == "entity_id")
    sub = os.path.basename(os.path.dirname(e["file"]))
    folder = os.path.basename(os.path.dirname(os.path.dirname(e["file"])))
    name = f"sc_bnw_box_{sub[4:].lower()}_{e['obj']}"
    after = name + "_1"
    sc = copy.deepcopy(S.load(BOX_TEMPLATE))
    sc["Name"] = name
    for m in sc["Mnemonics"]:
        if m["mnemonic"] == "TreasureBox":
            m["operands"]["iValues"][0] = entity
        elif m["mnemonic"] in BATTLES:
            m["operands"]["iValues"][0] = party
            m["operands"]["sValues"][0] = after
    end = copy.deepcopy(S.load(BOX_TEMPLATE + "_1"))
    end["Name"] = after
    end["Mnemonics"] = [m for m in end["Mnemonics"] if m["mnemonic"] in ("Nop", "FadeIn", "Exit")]
    end["Segments"][0]["Count"] = len(end["Mnemonics"])
    base = os.path.join(out_dir, e["group"], "Assets", "GameAssets", "Serial", "Res", "Map", folder, sub)
    os.makedirs(base, exist_ok=True)
    for script in (sc, end):
        with open(os.path.join(base, script["Name"] + ".json"), "w", encoding="utf-8") as fh:
            fh.write(json.dumps(script, ensure_ascii=False, separators=(",", ":")))
    S.list_in_map(out_dir, e["group"], folder, sub, [name, after])
    return name, after, folder, sub


def port(van, bnw, out_dir, log, tables=None):
    """BNW's (a seed's) chests onto the PR's; with the porter's tables, a box with other monsters
    fights them on the box's background (box_look). Returns {table: changed ids}."""
    vc, bc = snes_chests(van), snes_chests(bnw)
    changed_bits = {bit for bit, b in bc.items() if not vc.get(bit) or (vc[bit]["type"], vc[bit]["contents"]) != (b["type"], b["contents"])}
    flags = match_flags(vc, changed=changed_bits)
    moved = moved_chests(vc, bc, flags)
    porter_ = []

    def porter():
        """The encounter porter (its party_for), made once when a box needs a new party."""
        if not porter_:
            import port_encounters as E
            porter_.append(E.EncounterPorter(van, bnw, tables, log))
            porter_[0].stats = Counter()
        return porter_[0]
    log(f"chests: vanilla {len(vc)}, BNW {len(bc)}; {len(flags)} matched to PR chests by layout"
        f" ({match_flags.fallback} by contents, {match_flags.taken} shared chests given to the changed SNES chest),"
        f" {sum(len(v) > 1 for v in flags.values())} with copies")
    edits = defaultdict(dict)  # file -> {obj_id: (content_id, num, clear script)}
    scripts = {}               # monster-in-a-box script name -> new party
    forms = {}                 # ... -> the seed's formation there (box_look)
    # the PR's own party for a vanilla monster-in-a-box group (it made its own for some: the Lobo
    # box fights 516, not formation 0's party 1)
    own = {}
    miabs = miab_scripts() if any(c["type"] == MONSTER for c in bc.values()) else {}
    for key, c in vc.items():
        if c["type"] == MONSTER and key in flags:
            for e in flags[key]:
                name = script_name(e["script"]) if e["script"] else None
                if name in miabs:
                    own[c["contents"]] = miabs[name][0]
    # the PR's own party where it differs from the SNES pack's (MIAB Lobo: pack 0 = party 516)
    miab = miab_scripts()
    pr_party = {}
    for k, c in vc.items():
        if c["type"] == MONSTER and k in flags:
            for e in flags[k]:
                name = script_name(e["script"]) if e["script"] else None
                if name in miab:
                    pr_party[miab_party(van, c["contents"])] = miab[name][0]
    skipped = Counter()
    boxes = []                 # (SNES chest, the mod's box) for item chests the mod made boxes
    fills = {}                 # scripted chest's script -> the seed's [(content_id, num)]
    for bit, b in bc.items():
        v = vc.get(bit)
        if v is None and bit[0] in MOVED_MAPS and (MOVED_MAPS[bit[0]], bit[1], bit[2]) in flags:
            # a chest a mod moved to another map at the same spot: the PR chest of where it was
            bit = (MOVED_MAPS[bit[0]], bit[1], bit[2])
            v = vc.get(bit)
        if v is None and bit in moved:
            bit = moved[bit]                       # a chest the mod moved: its old spot's PR chest
            v = vc[bit]
        if v and same_chest(v, b):
            continue
        if bit in MERGED:
            continue                               # joins its neighbour's pick-up (below)
        if bit not in flags:
            skipped["no PR chest (new in BNW or unmatched)"] += 1
            continue
        if b["type"] == ITEM:
            new = (M.item_content(b["contents"]), 1)
        elif b["type"] == GOLD:
            new = (1, b["contents"] * 100)
        elif b["type"] in (EMPTY, 0):
            new = (1, 0)   # an empty chest (WC's -cce, its random empties; no type = nothing): 0 gil, as the PR's own
        elif b["type"] == MONSTER and v and v["type"] == MONSTER:
            # a monster-in-a-box with another monster (WC's -cms): its script's battle changes
            party = own.get(b["contents"]) if miab_party(bnw, b["contents"]) == miab_party(van, b["contents"]) else None
            party = party or miab_party(bnw, b["contents"])
            party = pr_party.get(party, party)
            for e in flags[bit]:
                name = script_name(e["script"]) if e["script"] else None
                if name:
                    scripts[name] = party
                    forms[name] = b["formation"]
            if not any(e["script"] for e in flags[bit]):
                skipped["monster chest without a PR battle script"] += 1
            continue
        elif b["type"] == MONSTER and tables is not None and "WC_FLAGS" not in os.environ:
            # an item / empty chest the mod made a monster-in-a-box (BNW: South Figaro's Earring,
            # Mt. Zozo's Red Cap): each PR copy gets a box script of its own (new_box, below)
            boxes.append((bit, b))
            continue
        else:
            # an item / empty chest WC made a monster-in-a-box (-chrm): the PR chest has no battle
            # script to give it (its chests can't start one without one), so it keeps its contents
            # (a WC seed is ported in game too: its scripts stay the PR's own)
            skipped[f"type {v['type'] if v else 0:#x} -> {b['type']:#x}"] += 1
            continue
        for e in flags[bit]:
            if e.get("empty"):
                fills[e["empty"]] = [new]          # the PR's scripted chest: its script gives it
                continue
            # a monster-in-a-box (battle script) that BNW turned into an item chest: no battle
            props = {"content_id": new[0], "content_num": new[1], "message_key": chest_message(*new)}
            if e["script"] and v and v["type"] == MONSTER:
                props["script_id"] = 0
            edits[e["file"]][e["obj"]] = props
    # a chest the PR dropped beside a pick-up (MERGED): the pick-up gives both
    for gone, kept in MERGED.items():
        if gone not in bc or kept not in bc or kept not in flags:
            continue
        gifts = []
        for b in (bc[kept], bc[gone]):
            if b["type"] == ITEM:
                gifts.append((M.item_content(b["contents"]), 1))
            elif b["type"] == GOLD:
                gifts.append((1, b["contents"] * 100))
            elif b["type"] == MONSTER:
                skipped["a box beside a pick-up (no battle there)"] += 1
        for e in flags[kept]:
            if e.get("empty"):
                fills[e["empty"]] = gifts
    keys = asset_keys()
    written = defaultdict(list)
    filled = []
    for rel, gifts in sorted(fills.items()):
        if fill_empty(rel, gifts, out_dir):
            group, path = rel.replace("\\", "/").split("/", 1)
            stem = os.path.splitext(path)[0]
            written[group].append((keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1]), stem))
            filled.append(os.path.basename(stem))
    if fills:
        log(f"chests: {len(filled)} of the PR's scripted chests filled ({', '.join(filled)})")
    made = set()
    kept = []
    if scripts:
        import pr_script as S
        for name in sorted(scripts):
            sc = S.load(name)
            if tables is not None:
                scripts[name] = seed_party(van, bnw, forms[name], scripts[name], tables, porter, miab[name][0])
                scripts[name] = box_look(tables, forms[name], scripts[name], miab[name][0], made)
            if all(m["operands"]["iValues"][0] == scripts[name] for m in sc.get("Mnemonics", []) if m.get("mnemonic") in BATTLES):
                kept.append(name)                  # the PR's own battle is the seed's already
                continue
            for m in sc.get("Mnemonics", []):
                if m.get("mnemonic") in BATTLES:
                    m["operands"]["iValues"][0] = scripts[name]
            S.save(sc, out_dir, name)
            rel = S.rel(name)
            group, path = rel.split("/", 1)
            stem = os.path.splitext(path)[0]
            written[group].append((keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1]), stem))
        log(f"chests: {len(scripts) - len(kept)} monster-in-a-box battles changed ("
            f"{', '.join(f'{k} -> {v}' for k, v in sorted(scripts.items()) if k not in kept)}); {len(made)} on their box's background"
            + (f"; {len(kept)} already fight the seed's ({', '.join(kept)})" if kept else ""))
    new_ids = set()
    if boxes:
        st = tables.setdefault("script", P.Table("script"))
        made_boxes = []
        for bit, b in boxes:
            for e in sorted(flags[bit], key=lambda e: (e["file"], e["obj"])):
                if e.get("empty") or e["script"]:
                    skipped["an item chest made a box: the PR chest is scripted"] += 1
                    continue
                look = map_party(os.path.basename(os.path.dirname(e["file"])))
                party = miab_party(bnw, b["contents"])
                party = seed_party(van, bnw, b["formation"], party, tables, porter, look or party)
                if look:
                    party = box_look(tables, b["formation"], party, look, made)
                sid = BOX_SCRIPT_IDS + 1 + len(new_ids)
                assert sid not in st.rows and sid < 9900, f"box script id {sid}"
                name, after, folder, sub = new_box(e, party, out_dir)
                st.rows[sid] = {"id": str(sid), "script_name": name}
                new_ids.add(sid)
                edits[e["file"]][e["obj"]] = {"content_id": 1, "content_num": 0, "message_key": MSG_ITEM, "script_id": sid}
                base = f"Assets/GameAssets/Serial/Res/Map/{folder}/{sub}/"
                written[e["group"]] += [(f"{sub}/{name}", base + name), (f"{sub}/{after}", base + after),
                                        ("package", f"Assets/GameAssets/Serial/Res/Map/{folder}/package")]
                made_boxes.append(f"{name} (formation {b['formation']} -> party {party})")
        log(f"chests: {len(made_boxes)} item chests made boxes ({', '.join(made_boxes)})")
    if porter_:
        made |= porter_[0].changed["monster_party"]
    n = 0
    for f, objs in edits.items():
        doc = json.load(open(f, encoding="utf-8"))
        for layer in doc.get("layers", []):
            for o in layer.get("objects", []) or []:
                if o["id"] in objs:
                    want = objs[o["id"]]
                    props = o.setdefault("properties", [])
                    for prop in props:
                        if prop["name"] in want:
                            prop["value"] = want[prop["name"]]
                    have = {prop["name"] for prop in props}
                    for k, val in want.items():
                        if k not in have:
                            props.append({"name": k, "type": "string" if isinstance(val, str) else "int", "value": val})
                    n += 1
        rel = os.path.relpath(f, P.VANILLA).replace("\\", "/")
        group, path = rel.split("/", 1)
        dest = os.path.join(out_dir, group, *path.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8") as fh:
            fh.write(json.dumps(doc, ensure_ascii=False, separators=(",", ":")))   # dumps: the C encoder
        stem = os.path.splitext(path)[0]
        written[group].append((keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1]), stem))
    for group, items in written.items():
        items = list(dict(items).items())          # one entry per key (a map's package: once)
        os.makedirs(os.path.join(out_dir, group, "keys"), exist_ok=True)
        with open(os.path.join(out_dir, group, "keys", "bnw_chests.json"), "w", encoding="utf-8") as fh:
            json.dump({"keys": [k for k, _ in items], "values": [v for _, v in items]}, fh, indent=1)
    log(f"chests: {n} PR chests changed in {len(edits)} map files; skipped {dict(skipped)}")
    return {"monster_party": made, "script": new_ids}

"""Worlds Collide's check NPCs look like their reward (WC re-skins them: the user's playtest,
2026-11-06: "Celes" freed at South Figaro was Relm and should have looked like her).

WC sets a check NPC's sprite to the reward: the reward character (0-13), or for an esper or
item a soldier, imp, merchant or ghost (WC's own random pick), or magicite. wc_run.py records
every sprite WC's events set ({dump}_npcs.json: SNES map, NPC id, sprite); here each such SNES
NPC is paired with the PR map entity (or entities: the PR keeps some NPCs in several event
layers) that shows it, and the seed's copy of that map file shows the reward's PR art
(the entity's asset_id, a mapobject id). A character's art follows WC's -cspr like the
party's (port.port_wc_graphics).

The pairs were found from the SNES NPC's vanilla sprite and position, the PR map group of the
check, and the PR entity showing that character there (2026-11-06; positions agree within a
row where the PR kept the SNES layout). SNES NPCs without a pair keep the PR's art: Doma WoR's
Cyans, Figaro WoR's Edgar, Baren Falls' Gau, Mt. Zozo's cliff Cyan, the Serpent Trench Edgar,
the Magitek Factory's and the Opera House's, Zozo's esper, Mt. Kolts (a palette only), Ebot's
Rock (Gungho's table), Imperial Camp's Cyan, Mobliz's other Terras, Deathgaze's magicite.
"""
import json
import os

import prdata as P

_M = "Assets/GameAssets/Serial/Res/Map"
PAIRS = {
    # (SNES map, NPC id): [(PR file under pr/vanilla, object id)]  WC module, vanilla character
    (22, 0x18): [(f"map_20030/{_M}/Map_20030/Map_20030/ev_e_0250.json", 36),      # narshe_battle, Banon
                 (f"map_20030/{_M}/Map_20030/Map_20030/ev_e_0256.json", 77),
                 (f"map_20030/{_M}/Map_20030/Map_20030/ev_e_0257.json", 41)],
    (23, 0x1C): [(f"map_20040/{_M}/Map_20040/Map_20040/ev_e_0437.json", 35)],      # lone_wolf, Mog
    (51, 0x19): [(f"map_30011/{_M}/Map_30011/Map_30011_2/ev_e_0018.json", 34)],   # narshe_moogle_defense, Terra
    (58, 0x10): [(f"map_20051/{_M}/Map_20051/Map_20051_13/ev_e_0026.json", 24),   # figaro_castle_wob, Edgar
                 (f"map_20051/{_M}/Map_20051/Map_20051_13/ev_e_0029.json", 25)],
    (83, 0x13): [(f"map_20071/{_M}/Map_20071/Map_20071_12/ev_e_0079.json", 14)],  # south_figaro, Celes
    (115, 0x10): [(f"map_20090/{_M}/Map_20090/Map_20090/ev_e_0097.json", 25)],    # gau_father_house, Shadow
    (150, 0x10): [(f"map_20340/{_M}/Map_20340/Map_20340/ev_e_0650.json", 60)],    # mobliz_wor, Terra
    (152, 0x10): [(f"map_30440/{_M}/Map_30440/Map_30440/ev_e_0189.json", 423)],   # phantom_train, the ghost
    (191, 0x10): [(f"map_20131/{_M}/Map_20131/Map_20131_1/ev_e_0285.json", 10)],  # kohlingen, Shadow
    (208, 0x13): [(f"map_30631/{_M}/Map_30631/Map_30631_5/ev_e_0773.json", 16)],  # owzer_mansion, Relm
    (278, 0x10): [(f"map_30791/{_M}/Map_30791/Map_30791_8/ev_e_0865.json", 3)],   # zone_eater, Gogo
    (305, 0x11): [(f"map_20330/{_M}/Map_20330/Map_20330/ev_e_0636_1.json", 167)], # collapsing_house, Sabin
    (313, 0x10): [(f"map_30781/{_M}/Map_30781/Map_30781_2/ev_e_0828.json", 68)],  # phoenix_cave, Locke
    (353, 0x12): [(f"map_30761/{_M}/Map_30761/Map_30761_4/ev_e_0744.json", 18)],  # veldt_cave_wor, Shadow
    (353, 0x13): [(f"map_30761/{_M}/Map_30761/Map_30761_4/ev_e_0745.json", 20)],  # veldt_cave_wor, Relm
    (362, 0x13): [(f"map_30700/{_M}/Map_30700/Map_30700/ev_e_0001.json", 72)],    # fanatics_tower, Strago
    (391, 0x16): [(f"map_40034/{_M}/Map_40034/Map_40034/ev_e_0558.json", 43)],    # sealed_gate, Kefka
    (408, 0x11): [(f"map_30811/{_M}/Map_30811/Map_30811_7/entity_default.json", 15)],  # ancient_castle, the queen
}
PAIRS[(22, 0x25)] = PAIRS[(22, 0x18)]                       # narshe_battle's second Banon
for _m, _n in ((371, 0x11), (372, 0x11), (373, 0x10), (375, 0x1C)):   # esper_mountain, Relm in each room
    PAIRS[(_m, _n)] = [(f"map_30561/{_M}/Map_30561/Map_30561_2/ev_e_0547.json", 17),
                       (f"map_30561/{_M}/Map_30561/Map_30561_2/ev_e_0546.json", 28)]

# the PR's map art (mapobject ids): a PR character's NPC sprite, and WC's other sprites
CHARACTER_NPC = {**{c: 119 + c for c in range(1, 15)}, 15: 136, 16: 140, 17: 139, 31: 141, 33: 138}
WC_SPRITE_NPC = {14: 141, 15: 135, 16: 140, 17: 136, 18: 134, 19: 223, 20: 139, 21: 138, 91: 96}


def files():
    """The PR map files a seed may re-skin (wc_live.managed_files: the plugin swaps them)."""
    return sorted({f for pairs in PAIRS.values() for f, _ in pairs})


def asset_for(sprite, flags):
    """The PR art for a WC NPC sprite; a character's follows -cspr (port.WC_SPRITE_ART)."""
    if 0 <= sprite <= 13:
        donor = sprite + 1
        if "-cspr" in flags and flags.index("-cspr") + 1 < len(flags):
            import port
            ids = [int(x) for x in flags[flags.index("-cspr") + 1].split(".") if x.isdigit()]
            if sprite < len(ids):
                donor = port.WC_SPRITE_ART.get(ids[sprite], donor)
        return CHARACTER_NPC.get(donor)
    return WC_SPRITE_NPC.get(sprite)


def apply(dump, flags, port_dir, log):
    """Re-skin the paired PR entities in the port's copies of their map files (a file the port
    already wrote, a chest map, is edited in place). Returns the files written."""
    if not os.path.exists(dump):
        return []
    sets = json.load(open(dump, encoding="utf-8")).get("set", [])
    edits = {}                                    # rel -> {object id: asset}
    for r in sets:
        if "sprite" not in r:
            continue                              # a palette alone (no PR equivalent)
        asset = asset_for(r["sprite"], flags)
        pairs = PAIRS.get((r["map"], r["npc"]))
        if asset is None or not pairs:
            continue
        for rel, obj in pairs:
            edits.setdefault(rel, {})[obj] = asset
    written = []
    for rel, objs in sorted(edits.items()):
        dest = os.path.join(port_dir, *rel.split("/"))
        src = dest if os.path.exists(dest) else os.path.join(P.VANILLA, *rel.split("/"))
        with open(src, encoding="utf-8") as f:
            doc = json.load(f)
        n = 0
        for layer in doc.get("layers", []) or []:
            for o in layer.get("objects", []) or []:
                if o.get("id") in objs:
                    for prop in o.get("properties", []):
                        if prop["name"] == "asset_id" and prop["value"] != objs[o["id"]]:
                            prop["value"] = objs[o["id"]]
                            n += 1
        if n:
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "w", encoding="utf-8") as f:
                f.write(json.dumps(doc, ensure_ascii=False, separators=(",", ":")))
            written.append(rel)
    log(f"check NPCs: {len(written)} map files show their reward ({sum(len(v) for v in edits.values())} entities)")
    return written

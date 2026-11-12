"""Merchants a mod adds where the PR has none (BNW: a Doma Castle merchant in the World of Ruin,
SNES shop 86 sold by an NPC BNW put on map 126; vanilla's shop 86 is unused).

The PR opens every shop from a script through its own SysCall ("ショップを開く:<town>...", one game
method per shop holding its product group: Last.Interpreter.Instructions.SystemCall.Current), so a
new shop needs:
  * its stock as a new product group (product_group + product rows);
  * an NPC entity on the PR map (the PR's own shopkeeper entity as the template);
  * that NPC's script, the PR's shopkeeper script (greeting, the shop, farewell), whose
    SysCall "BNW:shop <group>" the BNW plugin answers with the game's own
    Current.OpenShopCommon(group, isFirstClock) (BnwPlugin ShopCalls.cs);
  * a script.csv row (the entity's script_id), the script in the map's script list (the map
    group's package.json: a map loads only the scripts listed there, MapModel.GetScript), and
    asset keys for the new script and the changed entity and package files (keys/bnw_npcs.json
    beside port_chests' keys/bnw_chests.json).
Placement comes from the profile (added_merchants): the PR's rooms aren't the SNES ones (BNW's
Doma spot sits behind a wall in the PR's room).
"""
import copy
import json
import os

import idmap as M
import prdata as P

SHOPS = 0x047AC0      # SNES shops: 9 bytes each (type, 8 items)
TEMPLATE_MAP = ("map_20361", "Map_20361", "Map_20361_24")   # South Figaro WoR's item shop
TEMPLATE_SCRIPT = "sc_npc_20361_24_1"
TEMPLATE_OBJ = 7
SCRIPT_IDS = 9900     # new scripts: 9901 up (script.csv's own ids end at 9029)
ITEMS_TITLE = "MSG_SYSTEM_433"   # product_group mes_id_name "Items"


def stock(rom, shop):
    raw = rom.data[SHOPS + 9 * shop:SHOPS + 9 * shop + 9]
    return [M.item_content(x) for x in raw[1:] if x != 0xff]


def map_dir(root, group, folder, sub):
    return os.path.join(root, group, "Assets", "GameAssets", "Serial", "Res", "Map", folder, sub)


def template_npc():
    path = os.path.join(map_dir(P.VANILLA, *TEMPLATE_MAP), "entity_default.json")
    doc = json.load(open(path, encoding="utf-8"))
    for layer in doc["layers"]:
        for o in layer.get("objects") or []:
            if o["id"] == TEMPLATE_OBJ:
                return o
    raise KeyError("the template shopkeeper is gone")


def template_script():
    path = os.path.join(map_dir(P.VANILLA, *TEMPLATE_MAP), TEMPLATE_SCRIPT + ".json")
    return json.load(open(path, encoding="utf-8"))


def op(mnemonic, s=None, label=""):
    sv = (list(s or []) + [""] * 8)[:8]
    return {"label": label, "mnemonic": mnemonic,
            "operands": {"iValues": [0] * 8, "rValues": [0] * 8, "sValues": sv}, "type": 1, "comment": ""}


def shop_script(name, group):
    """The PR's shopkeeper script with the plugin's shop call."""
    sc = template_script()
    sc = copy.deepcopy(sc)
    sc["Name"] = name
    sc["Mnemonics"] = [op("Nop", label="Main"), op("Msg", ["S9999_999_a_10"]), op("SysCall", [f"BNW:shop {group}"]),
                       op("Msg", ["S9999_999_a_11"]), op("Exit")]
    return sc


def set_prop(obj, name, value):
    for p in obj["properties"]:
        if p["name"] == name:
            p["value"] = value
            return
    raise KeyError(name)


def port(van, mod, tables, out_dir, log, profile):
    merchants = getattr(profile, "added_merchants", None) or []
    if not merchants:
        return {}
    product = tables.setdefault("product", P.Table("product"))
    groups = tables.setdefault("product_group", P.Table("product_group"))
    scripts = tables.setdefault("script", P.Table("script"))
    changed = {"product": set(), "product_group": set(), "script": set()}
    keys = {}
    for k, m in enumerate(merchants):
        items = stock(mod, m["shop"])
        if not items or stock(van, m["shop"]) == items:
            continue
        group = max(groups.rows) + 1
        groups.rows[group] = {"id": str(group), "mes_id_name": ITEMS_TITLE}
        changed["product_group"].add(group)
        next_id = max(product.rows) + 1
        template = product.rows[min(product.rows)]
        for cid in items:
            product.rows[next_id] = dict(template, id=str(next_id), content_id=str(cid), group_id=str(group),
                                         coefficient="0", purchase_limit="0")
            changed["product"].add(next_id)
            next_id += 1
        name = m["script"]
        sid = SCRIPT_IDS + 1 + k
        assert sid not in scripts.rows, f"script id {sid} taken"
        scripts.rows[sid] = {"id": str(sid), "script_name": name}
        changed["script"].add(sid)
        gdir, folder, sub = m["group"], m["group"].replace("map_", "Map_"), m["map"]
        out_map = map_dir(out_dir, gdir, folder, sub)
        os.makedirs(out_map, exist_ok=True)
        with open(os.path.join(out_map, name + ".json"), "w", encoding="utf-8") as fh:
            json.dump(shop_script(name, group), fh, ensure_ascii=False, separators=(",", ":"))
        # the entity file: port_chests' copy if it wrote one, else vanilla's
        ent = os.path.join(out_map, "entity_default.json")
        if not os.path.exists(ent):
            ent_src = os.path.join(map_dir(P.VANILLA, gdir, folder, sub), "entity_default.json")
        else:
            ent_src = ent
        doc = json.load(open(ent_src, encoding="utf-8"))
        layer = next(l for l in doc["layers"] if l.get("name") == "UpperObject")
        npc = copy.deepcopy(template_npc())
        npc["id"] = max(o["id"] for l in doc["layers"] for o in (l.get("objects") or [])) + 1
        npc["name"] = m.get("label", "BNW merchant")
        npc["x"], npc["y"] = 16 * m["x"], 16 * (m["y"] + 1)   # bottom-anchored like the PR's own
        set_prop(npc, "script_id", sid)
        set_prop(npc, "asset_id", m.get("asset_id", 29))
        set_prop(npc, "direction", m.get("direction", 4))
        layer["objects"].append(npc)
        with open(ent, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, ensure_ascii=False, separators=(",", ":"))
        import pr_script as S
        package = S.list_in_map(out_dir, gdir, folder, sub, [name])
        base = f"Assets/GameAssets/Serial/Res/Map/{folder}/{sub}/"
        keys.setdefault(gdir, {})[f"{sub}/{name}"] = base + name
        keys[gdir][f"{sub}/entity_default"] = base + "entity_default"
        keys[gdir]["package"] = package
        log(f"merchants: shop {m['shop']} ({len(items)} items) as product group {group}, sold by a new NPC in "
            f"{sub} at ({m['x']}, {m['y']}), script {name} ({sid})")
    for gdir, kv in keys.items():
        kdir = os.path.join(out_dir, gdir, "keys")
        os.makedirs(kdir, exist_ok=True)
        with open(os.path.join(kdir, "bnw_npcs.json"), "w", encoding="utf-8") as fh:
            json.dump({"keys": list(kv), "values": list(kv.values())}, fh, indent=1)
    return changed

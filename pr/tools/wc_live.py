"""Worlds Collide seeds in a running game: what a seed may change, and the WC mod folder's
baseline. Used by wc_seed.py (each seed) and deploy (the baseline).

The plugin (WcPlugin) applies a seed without restarting the game:
 * master tables and text are set in the running game (LiveData.cs). Each seed folder holds
   the FULL table for every table the porter can write (MANAGED_TABLES; vanilla rows where the
   seed changed nothing) and the full system_en text, so applying a seed also undoes the
   previous one;
 * map entities (chests) and monster AI scripts reach the game through Magicite, which reads
   a mod file each time the game loads that asset, but only for files that existed when the
   game started. So the WC mod folder ships every such file a seed could change (BASELINE:
   vanilla chest maps, an AI script in each of the 384 sc_ai_bnw_NNN slots) and a seed only
   overwrites contents (seed folder "files/").

  .venv/Scripts/python tools/wc_live.py baseline OUT_DIR   # the WorldsCollide Magicite folder
"""
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402

# every table the porter has written for any mod (BNW, Beyond Chaos, WC seeds)
MANAGED_TABLES = sorted({
    "ability", "ability_random_group", "armor", "attribute_group", "character_asset", "character_status", "condition",
    "condition_group", "content", "function_group", "game_constant_int", "growth_curve", "item",
    "job_group", "learning", "monster", "monster_ai_script", "monster_party", "monster_set", "product",
    "weapon",
})
MESSAGE_FILES = ["system_en"]
AI_SLOTS = 384
MASTER_PATH = "master/Assets/GameAssets/Serial/Data/Master"
MESSAGE_PATH = "message/Assets/GameAssets/Serial/Data/Message"


def chest_files():
    """Vanilla PR map entity files holding a chest, relative to pr/vanilla (= mod folder paths)."""
    import port_chests
    return sorted({os.path.relpath(c["file"], P.VANILLA).replace("\\", "/") for c in port_chests.pr_chests()})


def ai_files():
    import port_ai
    return [f"monster_ai/{port_ai.AI_PATH}/sc_ai_bnw_{e:03d}.json" for e in range(AI_SLOTS)]


def miab_files():
    """Monster-in-a-box chest scripts (WC's -cms changes their battles), relative to pr/vanilla."""
    import port_chests
    import pr_script as S
    return sorted(S.rel(name) for name in port_chests.miab_scripts())


def extra_files():
    """Whole data files a seed may change: battle_extra_data (procs, metamorph sets: port_procs)."""
    import port_procs
    return [f"{port_procs.GROUP}/{port_procs.ASSET}.json"]


def managed_files():
    import vcache
    # the map scans are the slow part (every map folder): cached; the rest is a list
    maps = vcache.cached("managed_maps_v5", lambda: chest_files() + miab_files() + empty_chest_files())
    return maps + [f for f in npc_files() if f not in set(maps)] + ai_files() + extra_files()


def empty_chest_files():
    """The scripts of the PR's empty chests, which a seed that fills one rewrites
    (port_chests.empty_chests / fill_empty), relative to pr/vanilla."""
    import port_chests
    return sorted({e["empty"].replace("\\", "/") for e in port_chests.empty_chests()})


def npc_files():
    """Map files whose check NPCs a seed re-skins as their reward (tools/wc_npcs.py)."""
    import wc_npcs
    return wc_npcs.files()


def full_table(name, port_csv=None):
    """The vanilla table with the port's rows (a partial Magicite CSV) laid over it."""
    table = P.Table(name)
    if port_csv and os.path.exists(port_csv):
        part = P.Table(name, open(port_csv, encoding="utf-8", newline="").read())
        if part.columns != table.columns:
            raise SystemExit(f"{name}: the port's columns differ from vanilla's")
        table.rows.update(part.rows)
        return table, set(part.rows)
    return table, set()


def full_messages(port_txt=None, name="system_en"):
    base, lang = name.rsplit("_", 1)
    text = P.messages(base, lang)
    changed = {}
    if port_txt and os.path.exists(port_txt):
        with open(port_txt, encoding="utf-8") as f:
            for line in f:
                line = line.rstrip("\r\n")
                if "\t" in line:
                    k, v = line.split("\t", 1)
                    changed[k] = v
    text.update(changed)
    return text, changed


def write_baseline(out):
    """The WorldsCollide Magicite folder: every file a seed may change, vanilla content."""
    import port_ai
    import port_chests
    shutil.rmtree(out, ignore_errors=True)
    keys = port_chests.asset_keys()
    groups = {}
    for rel in sorted(set(chest_files() + miab_files() + npc_files() + empty_chest_files())):
        group, path = rel.split("/", 1)
        dest = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(os.path.join(P.VANILLA, *rel.split("/")), dest)
        stem = os.path.splitext(path)[0]
        groups.setdefault(group, []).append((keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1]), stem))
    # WC's event changes (static scripts; the plugin does each seed's part): same key files
    import wc_events
    for rel in wc_events.build(out):
        group, path = rel.split("/", 1)
        stem = os.path.splitext(path)[0]
        groups.setdefault(group, []).append((keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1]), stem))
    msgs = wc_events.write_messages(out, MESSAGE_PATH.split("/", 1)[1])  # group "message", path inside it
    os.makedirs(os.path.join(out, "message", "keys"), exist_ok=True)
    with open(os.path.join(out, "message", "keys", "wc_message.json"), "w", encoding="utf-8") as f:
        json.dump({"keys": list(msgs), "values": list(msgs.values())}, f, indent=1)
    for group, items in groups.items():
        os.makedirs(os.path.join(out, group, "keys"), exist_ok=True)
        with open(os.path.join(out, group, "keys", "bnw_chests.json"), "w", encoding="utf-8") as f:
            json.dump({"keys": [k for k, _ in items], "values": [v for _, v in items]}, f, indent=1)
    # AI slots: each starts as a copy of that monster's own PR script (no monster points at a
    # slot until a seed compiles one, so this only matters if something reads it anyway)
    monsters = P.Table("monster")
    scripts = {r["id"]: r["script_name"] for r in P.Table("monster_ai_script")}
    ai_dir = os.path.join(out, "monster_ai", *port_ai.AI_PATH.split("/"))
    os.makedirs(ai_dir, exist_ok=True)
    fallback = None
    ai_keys, ai_values = [], []
    for e in range(AI_SLOTS):
        name = f"sc_ai_bnw_{e:03d}"
        row = monsters.rows.get(e + 1)
        src = scripts.get(row["script_id"]) if row else None
        path = os.path.join(P.VANILLA, "monster_ai", *port_ai.AI_PATH.split("/"), f"{src}.json") if src else None
        if path and os.path.exists(path):
            doc = json.load(open(path, encoding="utf-8"))
            fallback = fallback or doc
        else:
            doc = dict(fallback) if fallback else {"Segments": [], "Mnemonics": []}
        doc["Name"] = name + ".json"
        with open(os.path.join(ai_dir, name + ".json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, separators=(",", ":"))
        ai_keys.append(name)
        ai_values.append(f"{port_ai.AI_PATH}/{name}")
    os.makedirs(os.path.join(out, "monster_ai", "keys"), exist_ok=True)
    with open(os.path.join(out, "monster_ai", "keys", "bnw_ai.json"), "w", encoding="utf-8") as f:
        json.dump({"keys": ai_keys, "values": ai_values}, f, indent=1)
    # whole data files (battle_extra_data: metamorph sets, weapon procs), vanilla content, with
    # the key Magicite needs at startup (the seed's copy replaces the file)
    import port_procs
    for rel in extra_files():
        dest = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(os.path.join(P.VANILLA, *rel.split("/")), dest)
    os.makedirs(os.path.join(out, port_procs.GROUP, "keys"), exist_ok=True)
    with open(os.path.join(out, port_procs.GROUP, "keys", "mod_battle_extra.json"), "w", encoding="utf-8") as f:
        json.dump({"keys": ["battle_extra_data"], "values": [port_procs.ASSET]}, f, indent=2)
    files = managed_files()
    with open(os.path.join(out, "wc_baseline.json"), "w", encoding="utf-8") as f:
        json.dump({"tables": MANAGED_TABLES, "messages": MESSAGE_FILES, "files": files}, f, indent=1)
    size = sum(os.path.getsize(os.path.join(r, x)) for r, _, fs in os.walk(out) for x in fs)
    print(f"baseline: {len(files)} files ({len(files) - AI_SLOTS} chest maps, {AI_SLOTS} AI slots), "
          f"{size // 1024} KB -> {out}")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "baseline":
        write_baseline(os.path.abspath(sys.argv[2]))
    else:
        sys.exit(__doc__)

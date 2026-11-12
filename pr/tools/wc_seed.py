"""Worlds Collide seed -> PR data, for the in-game generator (no restart).

  python tools/wc_seed.py --seed abc123 --flags "-cg -oa ..." [--preset "moogle cup"] --out DIR

Runs WC (its own wc.py, untouched, in a fresh process: seeds match the website's) on the
vanilla US v1.0 ROM, then our porter on the result, and writes a seed folder the plugin
applies live (WcPlugin, tools/wc_live.py):
  DIR/master/.../Master/<table>.csv   full tables (wc_live.MANAGED_TABLES + anything else changed)
  DIR/message/.../Message/system_en.txt  full text
  DIR/files/<mod path>                 changed chest maps / AI scripts (wc_live.managed_files())
  DIR/wc_seed.json                     {seed, flags, hash, wc_version, tables, files, ...}
  DIR/wc_log.txt, DIR/generate.log     WC's own log (flags summary, spoilers with -sl); our log
Prints "[wc] ..." progress lines for the plugin's screen. About 5 seconds on a PC.

Runs from the project (pr/.venv) or from the generator bundle (tools/wc_bundle.py), where
wc/, rom/ and vanilla/ sit next to tools/.
"""
import argparse
import json
import re
import os
import shlex
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)  # pr/ or the bundle
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402
import ff6snes as S  # noqa: E402
import wc_live as L  # noqa: E402

# The ROM-free generator (PORTING.md "WC without an SNES ROM"): WC's input is the data the
# generator ships (tools/wc_basedata.py: the ROM's data tables, no graphics, text, maps or code)
# with stand-ins for what WC only rewrites (tools/wc_romfree.py); no SNES ROM is read or written.
# The default where there's no ROM (the generator bundle); FF6_WC_ROMFREE=1 / 0 forces it.
BASE = os.path.join(ROOT, "data", "wc_base.bin")
_mode = os.environ.get("FF6_WC_ROMFREE", "")
ROMFREE = _mode == "1" or (_mode != "0" and not os.path.exists(P.VANILLA_ROM) and os.path.exists(BASE))
if ROMFREE:
    import wc_basedata  # noqa: E402
    P.VANILLA_ROM = BASE                       # the porter's vanilla side: the same input WC had
    S.MEMORY[os.path.abspath(BASE)] = wc_basedata.load(BASE)

WC = os.environ.get("FF6_WC_DIR") or next(
    (p for p in (os.path.join(ROOT, "wc"), os.path.join(ROOT, "..", "mods", "worlds-collide")) if os.path.isdir(p)),
    os.path.join(ROOT, "wc"))
PRESETS = next((p for p in (os.path.join(ROOT, "wc_presets.json"),
                            os.path.join(ROOT, "..", "mods", "worlds-collide-presets.json")) if os.path.exists(p)), None)


def say(text):
    print(f"[wc] {text}", flush=True)


def port_in_process(rom, name, log):
    """port.py's main in this process (it was a second Python: start-up and imports again, seconds
    under Proton on the Deck); its report goes to the log as before."""
    import contextlib
    t = time.time()
    cwd = os.getcwd()
    try:
        os.chdir(ROOT)
        import port
        import profiles
        prof = profiles.Profile()
        prof.rom, prof.mod_name = rom, name
        with contextlib.redirect_stdout(log):
            port.main(prof)
    except SystemExit as e:
        log.write(f"port: exit {e.code}\n")
        say(f"error: the port stopped ({e.code})")
        raise SystemExit(1)
    except Exception as e:  # noqa: BLE001
        import traceback
        log.write(traceback.format_exc())
        say(f"error: the port failed: {e}")
        raise SystemExit(1)
    finally:
        os.chdir(cwd)
    log.write(f"(port: {time.time() - t:.1f} s)\n")
    log.flush()


def run(cmd, cwd, log):
    t = time.time()
    env = {**os.environ, "PYTHONIOENCODING": "utf-8", "FF6_VANILLA_ROM": P.VANILLA_ROM}
    if ROMFREE:
        env.update(WC_ROMFREE="1", WC_BASE=BASE)
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    log.write(f"$ {' '.join(cmd)}\n{p.stdout}{p.stderr}\n({time.time() - t:.1f} s, exit {p.returncode})\n")
    log.flush()
    if p.returncode != 0:
        # WC's argparse errors end in "wc.py: error: ..."; show the useful last line
        lines = [x for x in (p.stderr or p.stdout).strip().splitlines() if x.strip()]
        reason = lines[-1] if lines else f"exit {p.returncode}"
        say(f"error: {reason}")
        raise SystemExit(1)
    return p.stdout


# WC's "setup and start game" sequence (event/start.py: Reserve 0xc9a4f): calls to event
# bits, characters, party, espers, gold, items, start map. The esper/gold/items routines are
# plain event code: 86 xx add esper (xx = esper + 0x36), 84 lo hi add GP, 80 xx add item,
# B0 n 80 xx B1 add n of an item, F4 xx sound effect, FE return.
START_CALLS = 0x0C9A4F


def battle_remap(p):
    """WC's event battles that changed, as PR monster parties (idmap.party): {party: [parties]}.

    Boss / dragon / statue shuffles (-bbs -bbr -drloc -stloc -bmbd -srp3): the event that fought
    pack A now fights pack B (WC's get_boss), so A's formations become B's. Random fixed
    encounters (-fer): the pack keeps its place, its formations change. The PR's events name the
    party directly, so the plugin swaps it where they start the battle (External.Misc.CallEncount);
    several candidates = one at random (a party shared by two changed packs, or a 2-formation pack).
    """
    import idmap as M
    van, fin = p["vanilla"], p["final"]
    out = {}

    def add(old, new):
        if not any(van[old]):
            return    # an unused pack (WC pads them with random formations for its own events)
        for k, f in enumerate(van[old]):
            g = fin[new][k % len(fin[new])]
            if f == g and old == new:
                continue
            out.setdefault(M.party(f), set()).add(M.party(g))

    for old, new in p.get("replacements", {}).items():
        old, new = int(old), int(new)
        if old != new or van[old] != fin[old]:
            add(old, new)
    replaced = {int(k) for k in p.get("replacements", {})}
    for pack in p.get("fixed", []):
        if pack not in replaced and van[pack] != fin[pack]:
            add(pack, pack)
    # a party mapped to itself among others stays a candidate; one mapped only to itself is dropped
    return {str(k): sorted(v) for k, v in sorted(out.items()) if v != {k}}


EVENT_LOOK_BASE = 20000  # parties for swapped event battles fought on the event's own background
                         # (past port_encounters' per-look parties: 4000 + 600 k + f)
# The Cultists' Tower's battle script (script 9024 = sc_b_2024): its SysCall puts the tower's
# magic-only status (ConditionType TowerOfZealots) on the party, then the usual battle loop. The
# PR gives it to the tower's parties (MagiMaster 355, the White Dragon 482, the tower's
# encounters); the SNES's rule is the tower's (battle event bit $3EBB bit 0, set by the tower's
# events: C2/534E), whatever is fought there. Script 0 is the game's default loop (sc_b_none).
TOWER_SCRIPT, DEFAULT_SCRIPT = "9024", "0"


def place_script(src, row):
    """The battle script a swapped event battle runs: the target's own, except the tower's,
    which goes with the place (the user's Deck seed gaadl68ix7j4, 2026-11-12: MagiMaster at
    Tritoch's cliff, "i was unable to use attack"). A target with a script of its own at the
    tower (Air Force, Chadarnook... their routines stay) gets the status from the plugin
    (WcBattles.TowerStart, at the battle's start)."""
    own, here = row.get("script_name", DEFAULT_SCRIPT), src.get("script_name", DEFAULT_SCRIPT)
    if own == TOWER_SCRIPT and here != TOWER_SCRIPT:
        return DEFAULT_SCRIPT
    if own == DEFAULT_SCRIPT and here == TOWER_SCRIPT:
        return TOWER_SCRIPT
    return own


def event_looks(remap, mp, port_dir, sources=None):
    """WC's swapped event battles (battle_remap) fought where the event is: in the SNES the
    background is the map's, in the PR the party's, so a target whose background isn't the event
    party's gets a copy with it (its music stays the formation's, as the SNES plays it: a boss
    keeps its theme). The same for the Cultists' Tower's magic-only script (place_script). Not
    for a party an enemy's AI asks for by id (IsCheckMonsterPTId: the copy would fail the check).
    The copies go into `mp` (the seed's full monster_party table, ids from EVENT_LOOK_BASE), and
    into `sources` as copy -> the party copied (the plugin's Veldt counts a copy as its party:
    WcVeldt); returns (the remap with them, how many)."""
    import glob
    checked = set()
    for path in glob.glob(os.path.join(port_dir, "**", "*.json"), recursive=True):
        text = open(path, encoding="utf-8").read()
        if "IsCheckMonsterPTId" not in text:
            continue
        for m in json.loads(text).get("Mnemonics") or []:
            if m.get("mnemonic") == "IsCheckMonsterPTId":
                checked.add(m["operands"]["iValues"][0])
    bg = "battle_background_asset_id"
    copies, out = {}, {}
    for old, targets in sorted(remap.items(), key=lambda kv: int(kv[0])):
        src = mp.rows.get(int(old))
        new = []
        for t in targets:
            row = mp.rows.get(t)
            if src is None or row is None or t in checked:
                new.append(t)
                continue
            script = place_script(src, row)
            if row[bg] == src[bg] and row.get("script_name", DEFAULT_SCRIPT) == script:
                new.append(t)
                continue
            key = (t, src[bg], script)
            if key not in copies:
                pid = max([EVENT_LOOK_BASE - 1] + list(copies.values())) + 1
                while pid in mp.rows:
                    pid += 1
                mp.rows[pid] = dict(row, id=str(pid), **{bg: src[bg], "script_name": script})
                copies[key] = pid
                if sources is not None:
                    sources[str(pid)] = t
            new.append(copies[key])
        out[old] = sorted(set(new))
    return out, len(copies)


def coliseum(van_path, wc_path):
    """WC's Coliseum (data/coliseum.py: 256 matches of 4 bytes at 0x1FB600, by wagered item:
    opponent, ?, reward, reward hidden) next to vanilla's, in PR ids: [wager content, vanilla
    party, vanilla reward, WC party (0: none), WC reward, hidden, WC monster] (PR monster = SNES
    enemy + 1) for each match WC changed."""
    import idmap as M

    def table(path):
        d = S.rom_bytes(path)
        return [d[0x1FB600 + 4 * i:0x1FB600 + 4 * i + 4] for i in range(256)]

    def content(i):
        try:
            return M.item_content(i) if i != 0xFF else 0
        except Exception:  # noqa: BLE001 - items the PR doesn't have
            return 0

    # the PR fights a Coliseum party (ids 1000+), each one Coliseum copy of a regular monster
    # (monster 500+, same name key): SNES enemy e -> PR monster e + 1 -> its Coliseum party
    import csv
    master = os.path.join(P.VANILLA, "master", "Assets", "GameAssets", "Serial", "Data", "Master")
    key = {int(r["id"]): r["mes_id_name"] for r in csv.DictReader(open(os.path.join(master, "monster.csv"), encoding="utf-8"))}
    party_of_key, alone = {}, {}
    with open(os.path.join(master, "monster_party.csv"), encoding="utf-8") as f:
        rows = csv.reader(f)
        head = next(rows)
        cols = [i for i, h in enumerate(head) if re.fullmatch(r"monster\d", h)]
        for r in rows:
            ms = [int(r[i]) for i in cols if int(r[i]) > 0]
            if len(ms) != 1:
                continue
            if int(r[0]) >= 1000 and key.get(ms[0]):
                party_of_key.setdefault(key[ms[0]], int(r[0]))
            elif int(r[0]) < 1000:
                alone.setdefault(ms[0], int(r[0]))      # a regular battle with that monster alone

    def party(enemy):
        # its Coliseum party, else a regular one-monster battle with it, else 0 (the game's own stays)
        return party_of_key.get(key.get(enemy + 1)) or alone.get(enemy + 1, 0)

    out = []
    for wager, (v, w) in enumerate(zip(table(van_path), table(wc_path))):
        if v == w or wager == 0xFF:
            continue
        wc = content(wager)
        if wc <= 0:
            continue
        out.append([wc, party(v[0]), content(v[2]), party(w[0]), content(w[2]), int(w[3] != 0), w[0] + 1])
    return out


def start_state(rom_path):
    import idmap as M
    d = S.rom_bytes(rom_path)
    calls, i = [], START_CALLS
    while d[i] == 0xB2 and len(calls) < 10:
        calls.append((d[i + 1] | d[i + 2] << 8 | d[i + 3] << 16) + 0x0A0000)
        i += 4
    out = {"gold": 0, "items": {}, "espers": []}
    if len(calls) < 6:
        return out
    for a in calls[3:6]:  # espers, gold, items
        n = 0
        while a < len(d) and n < 400:
            op = d[a]
            n += 1
            if op == 0xFE:
                break
            if op == 0x84:
                out["gold"] += d[a + 1] | d[a + 2] << 8
                a += 3
            elif op == 0x86:
                c = M.esper_content(d[a + 1] - 0x36)
                if c:
                    out["espers"].append(c)
                a += 2
            elif op == 0x80:
                c = M.item_content(d[a + 1])
                if c:
                    out["items"][c] = out["items"].get(c, 0) + 1
                a += 2
            elif op == 0xB0 and d[a + 2] == 0x80 and d[a + 4] == 0xB1:
                c = M.item_content(d[a + 3])
                if c:
                    out["items"][c] = out["items"].get(c, 0) + d[a + 1]
                a += 5
            elif op == 0xF4:
                a += 2
            else:
                break  # something else: stop reading this routine
    out["items"] = [[c, k] for c, k in sorted(out["items"].items())]
    return out


def pr_reward(r):
    """WC reward (SNES ids) -> [kind, PR id]: character status id (Terra 1 .. Umaro 14),
    esper / item content id."""
    import idmap as M
    if r["type"] == "character":
        return ["character", r["id"] + 1]
    if r["type"] == "esper":
        return ["esper", M.esper_content(r["id"])]
    return ["item", M.item_content(r["id"])]


def event_rewards(dump):
    """(start party as character status ids, [{event, module, gate, rewards}]) from wc_run.py's dump."""
    events = json.load(open(dump, encoding="utf-8"))
    start, checks = [], []
    for e in events:
        raw = [r for r in e["rewards"] if r["id"] is not None]
        # in WC's reward1, reward2... order (the Auction House adds them the other way round)
        import re
        slots = [re.fullmatch(r"reward(\d+)", r.get("slot") or "") for r in raw]
        if raw and all(slots):
            raw = [r for _, r in sorted(zip((int(m.group(1)) for m in slots), raw), key=lambda t: t[0])]
        rewards = [pr_reward(r) for r in raw]
        if e["module"] == "start":
            start = [i for k, i in rewards if k == "character"]
            continue
        entry = {"event": e["event"], "module": e["module"],
                 "gate": None if e["gate"] is None else e["gate"] + 1, "rewards": rewards}
        if e["module"] == "tzen":
            # WC logs "Seraph (1,234 WOB, 5,678 WOR)": the seller's random price in each world
            import re
            m = re.search(r"\(([\d,]+) WOB, ([\d,]+) WOR\)", " ".join(e.get("log", [])))
            if m:
                entry["prices"] = [int(m.group(1).replace(",", "")), int(m.group(2).replace(",", ""))]
        checks.append(entry)
    return start, checks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", required=True)
    ap.add_argument("--flags", default="")
    ap.add_argument("--preset", default="")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    flags = a.flags
    if a.preset:
        flags = json.load(open(PRESETS, encoding="utf-8"))[a.preset] + " " + flags
    out = os.path.abspath(a.out)
    work = out + ".work"
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    log = open(os.path.join(work, "generate.log"), "w", encoding="utf-8")
    log.write(f"WC {WC}\nROM {'none: the shipped data (ROM-free) ' + BASE if ROMFREE else P.VANILLA_ROM}\nPython {sys.version}\n")
    if not os.path.exists(P.VANILLA_ROM):
        say(f"error: vanilla ROM not found ({P.VANILLA_ROM})")
        raise SystemExit(1)
    t0 = time.time()

    say("randomizing (Worlds Collide)")
    rom = os.path.join(work, "wc_out.bin" if ROMFREE else "wc.sfc")
    dump = os.path.join(work, "wc_events.json")
    wc_out = run([sys.executable, os.path.join(HERE, "wc_run.py"), dump, "-i", P.VANILLA_ROM, "-o", rom, "-s", a.seed]
                 + shlex.split(flags), WC, log)
    if ROMFREE:
        S.MEMORY[os.path.abspath(rom)] = S.rom_bytes(rom)   # WC's working bytes, for the port
    info = {"seed": a.seed, "flags": flags.strip(), "preset": a.preset, "hash": ""}
    for line in wc_out.splitlines():
        if line.startswith("Hash"):
            info["hash"] = line.split(None, 1)[1].strip()
    sys.path.insert(0, WC)
    try:
        import version
        info["wc_version"] = version.__version__
    except ImportError:
        info["wc_version"] = ""

    say("porting to the Pixel Remaster")
    name = "WCSeed"
    os.environ["WC_FLAGS"] = flags    # flags some converters read (port_spells: -sel/-bel)
    # WC's ability scaling: the porter's AI compiler reads the tiers (tools/ai_compile.py SCALED)
    tiers = os.path.join(work, "wc_events_tiers.json")
    if os.path.exists(tiers):
        os.environ["WC_AI_TIERS"] = tiers
    else:
        os.environ.pop("WC_AI_TIERS", None)
    os.environ["WC_SNES_AI"] = "1"    # monsters run WC's own scripts in the plugin (port_ai shims)
    port_in_process(rom, name, log)
    port_dir = os.path.join(ROOT, "mods", name)
    import port_ai
    snes_ai = getattr(port_ai.port, "snes_ai", None)
    # WC's check NPCs show their reward (tools/wc_npcs.py), in the port's copies of their maps
    import wc_npcs
    wc_npcs.apply(os.path.splitext(dump)[0] + "_npcs.json", shlex.split(flags), port_dir,
                  lambda m: log.write(m + "\n"))

    say("packing the seed")
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out)
    port_master = os.path.join(port_dir, *L.MASTER_PATH.split("/"))
    port_tables = sorted(f[:-4] for f in os.listdir(port_master) if f.endswith(".csv")) if os.path.isdir(port_master) else []
    extra = [t for t in port_tables if t not in L.MANAGED_TABLES]
    if extra:
        log.write(f"WARNING: tables outside wc_live.MANAGED_TABLES: {extra} (a later seed won't undo them)\n")
    tables = {}
    # WC's swapped event battles (the plugin swaps the party where an event starts its battle);
    # the parties they fight on the event's own background are added to monster_party below
    packs = os.path.splitext(dump)[0] + "_packs.json"
    pk = json.load(open(packs, encoding="utf-8")) if os.path.exists(packs) else None
    remap = battle_remap(pk) if pk else None
    look_sources = {}
    parties_final = None
    for t in sorted(set(L.MANAGED_TABLES) | set(port_tables)):
        table, changed = L.full_table(t, os.path.join(port_master, t + ".csv"))
        if t == "monster_party":
            parties_final = table
        if t == "monster_party" and remap:
            remap, looks = event_looks(remap, table, port_dir, look_sources)
            log.write(f"swapped event battles: {len(remap)} parties, {looks} fought on their event's background\n")
        dest = os.path.join(out, *L.MASTER_PATH.split("/"), t + ".csv")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8", newline="") as f:
            f.write(table.to_csv(table.rows.keys()))
        tables[t] = len(changed)
    texts = {}
    for m in L.MESSAGE_FILES:
        text, changed = L.full_messages(os.path.join(port_dir, *L.MESSAGE_PATH.split("/"), m + ".txt"), m)
        dest = os.path.join(out, *L.MESSAGE_PATH.split("/"), m + ".txt")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8", newline="") as f:
            for k, v in text.items():
                f.write(f"{k}\t{v}\r\n")
        texts[m] = len(changed)
    managed = set(L.managed_files())
    files, unmanaged = [], []
    for r, _, fs in os.walk(port_dir):
        for fn in fs:
            rel = os.path.relpath(os.path.join(r, fn), port_dir).replace("\\", "/")
            if rel.startswith(("master/", "message/")) or "/keys/" in rel or "/" not in rel:
                continue
            if rel in managed:
                dest = os.path.join(out, "files", *rel.split("/"))
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                try:
                    os.replace(os.path.join(r, fn), dest)   # port.py clears its folder every run
                except OSError:
                    shutil.copyfile(os.path.join(r, fn), dest)   # another drive (a move can't cross one)
                files.append(rel)
            else:
                unmanaged.append(rel)
    if unmanaged:
        log.write(f"WARNING: {len(unmanaged)} port files no seed can change live (left out): {unmanaged[:20]}\n")
    for fn in os.listdir(work):
        if fn.endswith(".txt") and fn.startswith("wc"):
            shutil.copyfile(os.path.join(work, fn), os.path.join(out, "wc_log.txt"))
    try:
        info["start"] = start_state(rom)
        # the characters WC dealt MagiTek (SNES command 29) as a command: the plugin lists and
        # allows the PR's Magitek (26) for them anywhere (WcMagitek); the port gave them one slot
        info["magitek"] = [c.id + 1 for c in S.characters(S.Rom(rom))[:14] if 29 in c.commands]
        try:
            # WC's event timers (-etr randomizes them), seconds, by the script the PR's countdown runs
            # at zero: collapsing house, opera, Floating Continent (WC writes frames: x60)
            d = S.rom_bytes(rom)
            # WC's esper level-up bonuses (-ebs / -ebr): SNES esper byte 10 (0-16, 0xFF none), each
            # esper's PR magicite content id; the plugin answers the PR's bonus lookups from them
            import idmap as M
            van_bonus = [r[10] for r in S.Rom(P.VANILLA_ROM).table(*S.ESPERS)]
            at, size, count = S.ESPERS
            info["esper_bonus"] = {"vanilla": [[M.esper_content(e), van_bonus[e]] for e in range(count)],
                                   "seed": [[M.esper_content(e), d[at + size * e + 10]] for e in range(count)
                                            if d[at + size * e + 10] != van_bonus[e]]}
            # the weapons that allow Runic / Bushido (SNES weapon byte 19 bit 7 / bit 1; -stra: all):
            # PR weapon content ids, vanilla's and the seed's (the plugin sets the PR's two lists)
            import bnw_plugin_data as BD
            rom_wc = S.Rom(rom)
            van_rom = S.Rom(P.VANILLA_ROM)
            info["command_weapons"] = {cmd: {k: [x["content_id"] for x in BD._select(BD._equipment(r), ("bit", 19, bit), BD._content_ids())]
                                             for k, r in (("vanilla", van_rom), ("seed", rom_wc))}
                                       for cmd, bit in (("runic", 7), ("bushido", 1))}
            # WC's -ari (the auction's normal items random): its GetItem item in each sale (Cherub Down,
            # Cure Ring, Hero Ring, Zephyr Cape: the PR's content ids 287 288 300 291) -> the seed's
            # (WC calls its own routine there: Call (B2) addr - A0000 -> AddItem (80) item)
            def sale_item(a):
                if d[a] == 0xB2:
                    a = (d[a + 1] | d[a + 2] << 8 | d[a + 3] << 16) + 0x0A0000
                return M.item_content(d[a + 1]) if d[a] == 0x80 else 0
            info["auction_items"] = {str(van_c): sale_item(a) for van_c, a in
                                     ((287, 0xB5012), (288, 0xB55A4), (300, 0xB56FF), (291, 0xB5C9F))
                                     if sale_item(a) not in (0, van_c)}
            info["timers"] = {name: int.from_bytes(d[at:at + 2], "little") // 60
                              for name, at in (("sc_e_0638", 0xC5925), ("sc_e_0341", 0xABA03), ("sc_e_1059", 0xAE3F6))}
        except Exception as e:  # noqa: BLE001
            log.write(f"timers: {e}" + chr(10))
        try:
            info["coliseum"] = coliseum(P.VANILLA_ROM, rom)
        except Exception as e:  # noqa: BLE001 - the seed plays with the vanilla Coliseum
            log.write(f"coliseum: {e}" + chr(10))
    except Exception as e:  # noqa: BLE001 - a seed without start data still plays
        log.write(f"start state: {e}" + chr(10))
        info["start"] = {"gold": 0, "items": [], "espers": []}
    try:
        info["start"]["characters"], info["checks"] = event_rewards(dump)
    except Exception as e:  # noqa: BLE001
        log.write(f"event rewards: {e}" + chr(10))
        info["start"]["characters"], info["checks"] = [], []
    objectives = os.path.splitext(dump)[0] + "_objectives.json"
    if os.path.exists(objectives):
        import idmap as M
        objs = json.load(open(objectives, encoding="utf-8"))
        for o in objs:
            o["items"] = [[M.item_content(i), n] for i, n in o.get("items", [])]   # PR content ids
            for c in o["conditions"]:
                if "character" in c:
                    c["status"] = c["character"] + 1                 # PR character status id
                if "esper" in c:
                    c["content"] = M.esper_content(c["esper"])       # PR magicite content id
        info["objectives"] = objs
    for suffix, key in (("_spells.json", "spell_table"), ("_forget.json", "forget_table")):
        spells = os.path.splitext(dump)[0] + suffix
        if os.path.exists(spells):
            import idmap as M
            info[key] = [a for a in (M.spell(i) for i in json.load(open(spells, encoding="utf-8"))) if a is not None]
    import idmap as M
    if pk:
        info["battles"] = remap
        info["look_sources"] = look_sources    # event battles' copies -> their party (WcVeldt)
        # battle quests: the PR parties the quest's event starts (its vanilla formations)
        info["quests"] = {name: sorted({M.party(f) for f in pk["vanilla"][pack] if f or any(pk["vanilla"][pack])})
                          for name, pack in pk.get("quests", {}).items()}
    skills = os.path.splitext(dump)[0] + "_skills.json"
    if os.path.exists(skills):
        # WC's random start skills as PR abilities: Rage 800 + rage, Lore 140 + lore, Dance 164 + dance
        sk = json.load(open(skills, encoding="utf-8"))
        info["start_skills"] = {"rages": [800 + r for r in sk.get("rages", [])], "lores": [140 + i for i in sk.get("lores", [])],
                                "dances": [164 + i for i in sk.get("dances", [])]}
    ccrs = os.path.splitext(dump)[0] + "_ccrs.json"
    if os.path.exists(ccrs):
        import idmap as M
        import port_chests
        sc = json.load(open(ccrs, encoding="utf-8"))
        # the PR's item and gil chests as the port left them (flag ids), the plugin counts the
        # opened ones; the n-th opened gives WC's n-th
        item_flags, gil_flags, seen = [], [], set()
        for e in port_chests._flags(port_chests.pr_chests()):
            if e["script"] or e["flag"] is None or e["flag"] in seen:
                continue
            seen.add(e["flag"])
            rel_ = os.path.relpath(e["file"], P.VANILLA)
            ported = os.path.join(port_dir, rel_)
            cid, num = e["key"][1] if e["key"][0] == "content" else 1, e["key"][1] if e["key"][0] == "gil" else 1
            if os.path.exists(ported):
                for layer in json.load(open(ported, encoding="utf-8")).get("layers", []):
                    for o in layer.get("objects", []) or []:
                        if o["id"] == e["obj"]:
                            pp = {x["name"]: x["value"] for x in o.get("properties", [])}
                            cid, num = pp.get("content_id", cid), pp.get("content_num", num)
            if cid == 1 and num > 0:
                gil_flags.append(e["flag"])
            elif cid > 1:
                item_flags.append(e["flag"])
        info["scaled_chests"] = {"items": [M.item_content(i) for i in sc["items"]], "gold": [g * 100 for g in sc["gold"]],
                                 "item_flags": item_flags, "gold_flags": gil_flags}
    try:
        # WC's new monster-in-a-box chests (-chrm): the PR chest (flag id) -> the PR party it fights
        # (the plugin empties the chest and starts the battle; a PR chest can't without a script)
        import port_chests as PC
        van_rom, wc_rom = S.Rom(P.VANILLA_ROM), S.Rom(rom)
        vc, bc = PC.snes_chests(van_rom), PC.snes_chests(wc_rom)
        new_boxes = {k: c for k, c in bc.items() if c["type"] == PC.MONSTER and (k not in vc or vc[k]["type"] != PC.MONSTER)}
        flags = PC.match_flags(vc)
        # the SNES boxes' PR scripts: the port decides their battles (port_chests.box_scripts)
        info["boxes"] = PC.box_scripts(flags, vc)
        if new_boxes:
            out_boxes = {}
            for k, c in new_boxes.items():
                for e in flags.get(k, []):
                    if not e["script"]:
                        out_boxes[str(e["flag"])] = PC.miab_party(wc_rom, c["contents"])
            info["monster_chests"] = out_boxes
    except Exception as e:  # noqa: BLE001
        log.write(f"monster chests: {e}" + chr(10))
    clock = os.path.splitext(dump)[0] + "_clock.json"
    if os.path.exists(clock):
        # WC's Zozo clock: the answer as the PR menu's option indices (its options are WC's order:
        # hours 2..12 by 2, minutes and seconds 10..50 by 10) and the clues for the PR's NPC lines
        import wc_events as E
        ck = json.load(open(clock, encoding="utf-8"))
        h, m, sec = (int(x) for x in ck["time"].split(":"))
        info["clock"] = {"time": ck["time"], "answer": [(h - 2) // 2, m // 10 - 1, sec // 10 - 1],
                         "texts": {E.ZOZO_CLUES[int(i)]: E.wc_text(t) for i, t in ck["texts"].items() if int(i) in E.ZOZO_CLUES}}
        # the clues as the seed's story text (the plugin lays every *_en.txt of a seed over the
        # game's messages; the baseline's vanilla_data has these keys' vanilla lines)
        dest = os.path.join(out, *L.MESSAGE_PATH.split("/"), "story_mes_en.txt")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8", newline="") as f:
            for k, v in info["clock"]["texts"].items():
                f.write(f"{k}\t{v}\r\n")
    csb = os.path.splitext(dump)[0] + "_csb.json"
    if os.path.exists(csb):
        info["cursed_shield_battles"] = json.load(open(csb, encoding="utf-8"))
    equip = os.path.splitext(dump)[0] + "_esper_equip.json"
    if os.path.exists(equip):
        # -eer / -eebr: magicite content id -> the character status ids who may equip it
        import idmap as M
        info["esper_equip"] = {str(M.esper_content(e)): [c + 1 for c in range(14) if bits >> c & 1]
                               for e, bits in enumerate(json.load(open(equip, encoding="utf-8")))}
    rages = os.path.splitext(dump)[0] + "_rages.json"
    if os.path.exists(rages):
        # "Learn Rages": WC's random rage order (rage r = enemy r) as PR Rage abilities (799 + monster r + 1)
        info["rage_table"] = [800 + r for r in json.load(open(rages, encoding="utf-8"))]
    bosses = os.path.splitext(dump)[0] + "_bosses.json"
    if os.path.exists(bosses):
        info["bosses"] = [{"name": b["name"], "party": M.party(b["formation"])} for b in json.load(open(bosses, encoding="utf-8"))]
    import ai_compile
    scaling = ai_compile.plugin_table()   # -ase / -asr: the spells WC picks from when a monster acts
    if scaling:
        info["ability_scaling"] = scaling
    if snes_ai:
        # the event battles' own parties (look_sources: the party fought on the event's background):
        # their formation, and the SNES slot of each of their cells (their layout is their own)
        if look_sources and parties_final is not None:
            import types
            form = snes_ai["formations"]
            extra = {int(p): form[src] for p, src in look_sources.items() if src in form and int(p) not in form}
            form.update(extra)
            snes_ai["party_slots"].update(port_ai.party_slots(types.SimpleNamespace(data=S.rom_bytes(rom)), parties_final, extra,
                                                              snes_ai.setdefault("party_members", {})))
        info["snes_ai"] = snes_ai   # the plugin runs these scripts (WcEnemyAi.cs)
    info.update(tables=tables, messages=texts, files=sorted(files), unmanaged=len(unmanaged),
                created=time.strftime("%Y-%m-%d %H:%M:%S"), seconds=round(time.time() - t0, 1))
    with open(os.path.join(out, "wc_seed.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=1)
    log.close()
    shutil.copyfile(os.path.join(work, "generate.log"), os.path.join(out, "generate.log"))
    # WC's SNES ROM stays in the work folder and goes with it: the mod is distributed and never
    # hands out a ROM (testing only: tools/dev, outside the bundle)
    shutil.rmtree(work, ignore_errors=True)
    shutil.rmtree(port_dir, ignore_errors=True)
    changed_rows = sum(tables.values())
    say(f"done: seed {a.seed} ({info['hash']}): {changed_rows} rows in {sum(1 for v in tables.values() if v)} tables, "
        f"{sum(texts.values())} texts, {len(files)} files, {info['seconds']} s")


if __name__ == "__main__":
    main()

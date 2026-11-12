"""Bring a Worlds Collide seed up to the generator's monster AI: cast-time ability scaling (seeds
made before 2026-11-11) and WC's own AI scripts run by the plugin (before 2026-11-12, snes_ai).

Older seeds compiled WC's ability scaling placeholders (-ase / -asr) to one fixed spell per monster,
and ran approximations of WC's AI as PR scripts. This regenerates the seed with its own seed and
flags (wc_seed.py), checks that nothing differs from the installed seed but what those changes make
(the monster AI script files, the monster table's script ids, the AI script table, new ability rows
7600+ and the AI markers 7680-7682, wc_seed.json's ability_scaling and snes_ai), backs the seed
folder up (BepInEx/wc-seed-backups/<seed folder>-<time>, outside the seeds list) and copies those
parts in. Anything else that differs stops it: a save made with the seed must keep working. The
plugin copies the changed files into the game and applies the tables at its next start.

  python tools/wc_seed_upgrade.py SEED_DIR [--deck DECK_SEED_DIR] [--deck-only] [--dry-run]

--deck: the same seed folder on the Steam Deck (ssh host "deck"): backed up there and given the
same files. --deck-only: SEED_DIR is already upgraded; only give the Deck's copy its parts. From
Git Bash, run with MSYS_NO_PATHCONV=1 (it rewrites /home/... into a Windows path). Neither game
may be running.

Also, without regenerating: the Cultists' Tower's battle script on the swapped event battles' party
copies (seeds made before 2026-11-12 gave a copy its target's script, so MagiMaster fought elsewhere
kept the tower's magic-only status: wc_seed.place_script). Only the script_name of those copy rows
(ids from wc_seed.EVENT_LOOK_BASE) in monster_party.csv changes; nothing new is added.
And undead in battle (seeds made before 2026-11-12 cleared only the species bit for -bnu, so its
bosses stayed undead in battle): monster_flag_group_id of the monster rows whose undead bit says
otherwise (port.flag_group_with) in monster.csv; nothing new is added.
"""
import argparse
import csv
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
MASTER = "master/Assets/GameAssets/Serial/Data/Master/"
ABILITY = MASTER + "ability.csv"
MONSTER = MASTER + "monster.csv"
AI_SCRIPTS = MASTER + "monster_ai_script.csv"
AI_DIR = "files/monster_ai"
LOGS = {"generate.log", "wc_log.txt", "wc_seed.json"}
SCALED_BASE = 7600
NEW_ABILITIES = set(range(SCALED_BASE, SCALED_BASE + 36)) | {7680, 7681, 7682} | set(range(7700, 7800))   # scaling, AI markers, WC's items
AI_TABLES = ("ability", "monster", "monster_ai_script")
JSON_PARTS = ("ability_scaling", "snes_ai")
MONSTER_PARTY = MASTER + "monster_party.csv"


def tower_fixes(seed_dir):
    """The swapped event battles' party copies whose battle script isn't wc_seed.place_script's:
    {copy id: (script now, script wanted)}; a copy two events want differently is left (logged)."""
    sys.path.insert(0, HERE)
    import wc_seed
    info = json.load(open(os.path.join(seed_dir, "wc_seed.json"), encoding="utf-8"))
    mp = rows(os.path.join(seed_dir, MONSTER_PARTY))
    sources = info.get("look_sources") or {}
    wants = {}
    for old, targets in (info.get("battles") or {}).items():
        src = mp.get(str(old))
        for t in targets:
            target = mp.get(str(sources.get(str(t), "")))
            if src is None or target is None or str(t) not in mp:
                continue                                       # not one of the seed's copies
            wants.setdefault(str(t), set()).add(wc_seed.place_script(src, target))
    out = {}
    for pid, want in sorted(wants.items(), key=lambda kv: int(kv[0])):
        if len(want) > 1:
            print(f"party {pid}: events want scripts {sorted(want)}; left as it is")
            continue
        want = want.pop()
        if mp[pid]["script_name"] != want:
            out[pid] = (mp[pid]["script_name"], want)
    return out


def rewrite_column(seed_dir, rel, column, fixes):
    """Rewrite one column of the given rows ({id: (now, wanted)}) of a seed table, the file's text
    otherwise as it is."""
    path = os.path.join(seed_dir, rel)
    with open(path, encoding="utf-8", newline="") as f:
        lines = f.read().splitlines(keepends=True)
    col = lines[0].rstrip("\r\n").split(",").index(column)
    done = 0
    for i, line in enumerate(lines[1:], 1):
        body = line.rstrip("\r\n")
        cells = body.split(",")
        if cells[0] in fixes:
            assert cells[col] == fixes[cells[0]][0], f"{rel} row {cells[0]}: {column} {cells[col]}"
            cells[col] = fixes[cells[0]][1]
            lines[i] = ",".join(cells) + line[len(body):]
            done += 1
    assert done == len(fixes), f"{done} of {len(fixes)} rows found"
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("".join(lines))


def apply_tower_fixes(seed_dir, fixes):
    """Rewrite those rows' script_name in monster_party.csv, the file's text otherwise as it is."""
    rewrite_column(seed_dir, MONSTER_PARTY, "script_name", fixes)


def undead_fixes(seed_dir):
    """Monsters whose undead bit (species bit 0) and undead in battle (the Undead flag in their
    monster_flag_group: tools/decomp/undead) disagree. Seeds made before 2026-11-12 moved only the
    species bit, so WC's -bnu left its bosses undead in battle (the user's seed gaadl68ix7j4: the
    Assassin's Dagger healed the second Behemoth King to full on every hit). {monster id: (flag
    group now, flag group wanted)}; one that would need a flag group of its own is left (logged)."""
    sys.path.insert(0, HERE)
    import port
    import prdata as P
    own = os.path.join(seed_dir, MASTER + "monster_flag_group.csv")
    groups = P.Table("monster_flag_group", open(own, encoding="utf-8").read() if os.path.exists(own) else None)
    undead = {int(r["group_id"]) for r in groups if int(r["monster_flag_id"]) == port.UNDEAD_FLAG}
    out = {}
    for mid, r in rows(os.path.join(seed_dir, MONSTER)).items():
        on, gid = bool(int(r["species"]) & 1), int(r["monster_flag_group_id"])
        if on == (gid in undead):
            continue
        made = set()
        want = port.flag_group_with(groups, gid, port.UNDEAD_FLAG, on, made)
        if made:
            print(f"monster {mid}: no flag group has group {gid}'s flags {'with' if on else 'without'} Undead; left as it is")
            continue
        out[mid] = (str(gid), str(want))
    return out


def upgrade_undead(seed_dir, deck, dry_run):
    """The undead part of the upgrade, PC (and the Deck's copy): None when nothing to do."""
    fixes = undead_fixes(seed_dir)
    if not fixes:
        return None
    for mid, (was, want) in fixes.items():
        print(f"monster {mid}: flag group {was} -> {want} (undead in battle as its species says)")
    if dry_run:
        return 0
    bak = backup_dir(seed_dir)
    shutil.copytree(seed_dir, bak)
    rewrite_column(seed_dir, MONSTER, "monster_flag_group_id", fixes)
    info = json.load(open(os.path.join(seed_dir, "wc_seed.json"), encoding="utf-8"))
    info["upgraded"] = time.strftime("%Y-%m-%d %H:%M:%S") + " (undead in battle follows the undead bit: -bnu)"
    with open(os.path.join(seed_dir, "wc_seed.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=1)
    print(f"PC: {len(fixes)} monsters in {seed_dir} (backup: {bak})")
    return push_deck(deck, seed_dir, [MONSTER], False) if deck else 0


def upgrade_tower(seed_dir, deck, dry_run):
    """The tower-script part of the upgrade, PC (and the Deck's copy): None when nothing to do."""
    fixes = tower_fixes(seed_dir)
    if not fixes:
        return None
    for pid, (was, want) in fixes.items():
        print(f"party {pid}: battle script {was} -> {want} (the Cultists' Tower's goes with the place)")
    if dry_run:
        return 0
    bak = backup_dir(seed_dir)
    shutil.copytree(seed_dir, bak)
    apply_tower_fixes(seed_dir, fixes)
    info = json.load(open(os.path.join(seed_dir, "wc_seed.json"), encoding="utf-8"))
    info["upgraded"] = time.strftime("%Y-%m-%d %H:%M:%S") + " (the Cultists' Tower's battle script with the place)"
    with open(os.path.join(seed_dir, "wc_seed.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, indent=1)
    print(f"PC: {len(fixes)} parties in {seed_dir} (backup: {bak})")
    return push_deck(deck, seed_dir, [MONSTER_PARTY], False) if deck else 0


def snes_ai_version():
    sys.path.insert(0, HERE)
    import port_ai
    return port_ai.SNES_AI_VERSION


def needed(info):
    """The upgrades this seed lacks."""
    out = []
    if any(f in info.get("flags", "").split() for f in ("-ase", "-asr")) and not info.get("ability_scaling"):
        out.append("cast-time ability scaling")
    if (info.get("snes_ai") or {}).get("version", 0) < snes_ai_version():
        out.append("WC's AI scripts in the plugin")
    return out


def tree(root):
    out = {}
    for d, _, fs in os.walk(root):
        for f in fs:
            p = os.path.join(d, f)
            out[os.path.relpath(p, root).replace("\\", "/")] = hashlib.md5(open(p, "rb").read()).hexdigest()
    return out


def rows(path):
    with open(path, encoding="utf-8") as f:
        return {r["id"]: r for r in csv.DictReader(f)}


def differences(old, new):
    """What differs between the installed seed and the regenerated one, outside what the upgrade
    changes ([] = only the expected parts)."""
    bad = []
    a, b = tree(old), tree(new)
    for k in sorted(set(a) | set(b)):
        if a.get(k) == b.get(k) or k in LOGS:
            continue
        if k.startswith(AI_DIR + "/") and k in b:
            continue                                  # AI scripts: changed or new
        if k == ABILITY:
            ra, rb = rows(os.path.join(old, k)), rows(os.path.join(new, k))
            changed = [i for i in ra if ra[i] != rb.get(i)]
            added = [i for i in rb if i not in ra]
            if changed or any(int(i) not in NEW_ABILITIES for i in added):
                bad.append(f"{k}: rows changed {changed[:5]}, added {added[:5]}")
            continue
        if k == MONSTER:
            ra, rb = rows(os.path.join(old, k)), rows(os.path.join(new, k))
            if set(ra) != set(rb):
                bad.append(f"{k}: rows added or removed")
            else:
                other = sorted({c for i in ra for c in ra[i] if ra[i][c] != rb[i].get(c)} - {"script_id"})
                if other:
                    bad.append(f"{k}: columns other than script_id differ: {other[:5]}")
            continue
        if k == AI_SCRIPTS:
            continue                                  # which script each id names
        bad.append(f"{k}: {'only installed' if k not in b else 'only new' if k not in a else 'differs'}")
    ja = json.load(open(os.path.join(old, "wc_seed.json"), encoding="utf-8"))
    jb = json.load(open(os.path.join(new, "wc_seed.json"), encoding="utf-8"))
    for k in sorted(set(ja) | set(jb)):
        if k in ("created", "seconds", "upgraded", "tables", "files") + JSON_PARTS or ja.get(k) == jb.get(k):
            continue
        bad.append(f"wc_seed.json {k} differs")
    ta, tb = dict(ja.get("tables", {})), dict(jb.get("tables", {}))
    for t in AI_TABLES:
        ta.pop(t, None)
        tb.pop(t, None)
    if ta != tb:
        bad.append("wc_seed.json tables differ beyond the AI's")
    fa = {f for f in ja.get("files", []) if not f.startswith("monster_ai/")}
    fb = {f for f in jb.get("files", []) if not f.startswith("monster_ai/")}
    if fa != fb:
        bad.append(f"wc_seed.json files differ beyond monster AI: {sorted(fa ^ fb)[:5]}")
    return bad


def changed_parts(old, new, what):
    """The upgrade's files (relative paths) and the merged wc_seed.json."""
    a, b = tree(old), tree(new)
    parts = [k for k in b if k.startswith(AI_DIR + "/") and a.get(k) != b[k]]
    parts += [k for k in (ABILITY, MONSTER, AI_SCRIPTS) if k in b and a.get(k) != b.get(k)]
    info = json.load(open(os.path.join(old, "wc_seed.json"), encoding="utf-8"))
    jb = json.load(open(os.path.join(new, "wc_seed.json"), encoding="utf-8"))
    for k in JSON_PARTS:
        if k in jb:
            info[k] = jb[k]
    for t in AI_TABLES:
        if t in jb.get("tables", {}):
            info.setdefault("tables", {})[t] = jb["tables"][t]
    info["files"] = jb.get("files", info.get("files", []))
    info["upgraded"] = time.strftime("%Y-%m-%d %H:%M:%S") + f" ({', '.join(what)})"
    return sorted(parts), info


def backup_dir(seed_dir):
    seeds = os.path.dirname(os.path.abspath(seed_dir))
    bepinex = os.path.dirname(os.path.dirname(os.path.dirname(seeds)))   # seeds -> WorldsCollide -> plugins -> BepInEx
    return os.path.join(bepinex, "wc-seed-backups", f"{os.path.basename(seed_dir)}-{time.strftime('%Y%m%d-%H%M%S')}")


def ssh(cmd, data=None):
    r = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "deck", cmd], input=data, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"ssh {cmd[:80]}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout.decode(errors="replace")


def push_deck(deck, seed_dir, parts, dry_run):
    """The upgraded parts (and wc_seed.json) into the Deck's copy of the seed, after a backup there."""
    deck = deck.rstrip("/")
    if not deck.startswith("/home/"):
        print(f"Deck path {deck!r} isn't a Deck path (Git Bash rewrites /home/...: MSYS_NO_PATHCONV=1)")
        return 1
    if ssh("pgrep -fa '[F]INAL FANTASY VI.exe' || true").strip():
        print("Deck: the game is running there; not upgraded")
        return 1
    name = os.path.basename(deck)
    bepinex = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(deck))))
    dbak = f"{bepinex}/wc-seed-backups/{name}-{time.strftime('%Y%m%d-%H%M%S')}"
    print(f"Deck: {len(parts)} files into {deck}")
    if dry_run:
        return 0
    ssh(f"test -f '{deck}/wc_seed.json' && mkdir -p '{os.path.dirname(dbak)}' && cp -a '{deck}' '{dbak}'")
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as t:
        for p in parts + ["wc_seed.json"]:
            t.add(os.path.join(seed_dir, p), arcname=p)
    ssh(f"cd '{deck}' && tar -xf -", buf.getvalue())
    print(f"Deck: upgraded {deck} (backup: {dbak})")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("seed_dir")
    ap.add_argument("--deck", default="")
    ap.add_argument("--deck-only", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    seed_dir = os.path.abspath(a.seed_dir)
    info = json.load(open(os.path.join(seed_dir, "wc_seed.json"), encoding="utf-8"))
    if a.deck_only:
        if needed(info) or not a.deck:
            print("--deck-only needs an upgraded SEED_DIR and --deck")
            return 1
        parts = sorted(os.path.relpath(os.path.join(d, f), seed_dir).replace(os.sep, "/")
                       for d, _, fs in os.walk(os.path.join(seed_dir, AI_DIR)) for f in fs)
        parts += [k for k in (ABILITY, MONSTER, AI_SCRIPTS, MONSTER_PARTY) if os.path.exists(os.path.join(seed_dir, k))]
        return push_deck(a.deck, seed_dir, parts, a.dry_run)
    tower = upgrade_tower(seed_dir, a.deck, a.dry_run)
    if tower:
        return tower
    # before a regeneration: the regenerated monster table has the fixed flag groups
    undead = upgrade_undead(seed_dir, a.deck, a.dry_run)
    if undead:
        return undead
    info = json.load(open(os.path.join(seed_dir, "wc_seed.json"), encoding="utf-8"))
    what = needed(info)
    if not what:
        print("already up to date (ability scaling and the plugin's AI)")
        return 0
    print("upgrading: " + ", ".join(what))
    work = tempfile.mkdtemp(prefix="wc_upgrade_")
    try:
        new = os.path.join(work, "seed")
        print(f"regenerating seed {info['seed']} ({info.get('hash', '')}) ...")
        r = subprocess.run([sys.executable, os.path.join(HERE, "wc_seed.py"), "--seed", info["seed"], "--flags", info["flags"],
                            "--preset", info.get("preset", "") or "", "--out", new], capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout[-2000:], r.stderr[-2000:])
            return 1
        jn = json.load(open(os.path.join(new, "wc_seed.json"), encoding="utf-8"))
        if jn.get("hash") != info.get("hash"):
            print(f"the regenerated seed isn't the same game (hash {jn.get('hash')} vs {info.get('hash')}): stopped")
            return 1
        bad = differences(seed_dir, new)
        if bad:
            print("the regenerated seed differs beyond the upgrade; nothing changed:")
            for x in bad[:30]:
                print("  " + x)
            return 1
        parts, merged = changed_parts(seed_dir, new, what)
        print(f"{len(parts)} files to bring in ({sum(p.startswith(AI_DIR) for p in parts)} monster AI scripts)")
        if a.dry_run:
            return 0
        bak = backup_dir(seed_dir)
        shutil.copytree(seed_dir, bak)
        for p in parts:
            os.makedirs(os.path.dirname(os.path.join(seed_dir, p)), exist_ok=True)
            shutil.copyfile(os.path.join(new, p), os.path.join(seed_dir, p))
        with open(os.path.join(seed_dir, "wc_seed.json"), "w", encoding="utf-8") as f:
            json.dump(merged, f, indent=1)
        print(f"PC: upgraded {seed_dir} (backup: {bak})")
        if a.deck:
            return push_deck(a.deck, seed_dir, parts, False)
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

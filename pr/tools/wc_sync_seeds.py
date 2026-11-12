"""Share Worlds Collide seeds between the PC and the Steam Deck (deploy.sh --deck runs it).

Steam Cloud carries the saves, not the seeds: a save made on one machine loads on the other
with its seed missing (seeds/state.json "slots" maps a save slot to its seed id, per machine).
This copies each side's seeds the other lacks (seed folders with a wc_seed.json; test seeds
"*-test" stay put) and merges the slot records: a slot one side knows and the other doesn't is
copied over; a slot both know differently keeps each side's own (reported). "active" and
"activeFiles" (what each install has applied) are never touched.

  python tools/wc_sync_seeds.py PC_SEEDS_DIR DECK_HOST DECK_SEEDS_DIR [--dry-run]
"""
import io
import json
import os
import subprocess
import sys
import tarfile


def ssh(host, cmd, data=None):
    r = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host, cmd],
                       input=data, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"ssh {cmd[:60]}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout


def seeds_here(root):
    return {d for d in os.listdir(root) if not d.endswith("-test") and os.path.isfile(os.path.join(root, d, "wc_seed.json"))} \
        if os.path.isdir(root) else set()


def seeds_there(host, root):
    out = ssh(host, f"cd '{root}' 2>/dev/null && for d in */; do [ -f \"$d/wc_seed.json\" ] && echo \"${{d%/}}\"; done; true")
    return {line for line in out.decode().split() if line and not line.endswith("-test")}


def state_here(root):
    p = os.path.join(root, "state.json")
    try:
        return json.load(open(p, encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def state_there(host, root):
    try:
        return json.loads(ssh(host, f"cat '{root}/state.json' 2>/dev/null || echo '{{}}'").decode() or "{}")
    except (RuntimeError, ValueError):
        return {}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry = "--dry-run" in sys.argv
    pc, host, deck = args
    if not dry:
        os.makedirs(pc, exist_ok=True)
        ssh(host, f"mkdir -p '{deck}'")
    mine, theirs = seeds_here(pc), seeds_there(host, deck)
    to_deck, to_pc = sorted(mine - theirs), sorted(theirs - mine)
    if to_deck and not dry:
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tar:
            for d in to_deck:
                tar.add(os.path.join(pc, d), arcname=d)
        ssh(host, f"tar -xf - -C '{deck}'", buf.getvalue())
    if to_pc and not dry:
        data = ssh(host, f"tar -cf - -C '{deck}' " + " ".join(f"'{d}'" for d in to_pc))
        with tarfile.open(fileobj=io.BytesIO(data), mode="r") as tar:
            tar.extractall(pc, filter="data")
    sp, sd = state_here(pc), state_there(host, deck)
    slots_p, slots_d = dict(sp.get("slots") or {}), dict(sd.get("slots") or {})
    conflicts = [k for k in slots_p.keys() & slots_d.keys() if slots_p[k] != slots_d[k]]
    new_p = {**{k: v for k, v in slots_d.items() if k not in slots_p}, **slots_p}
    new_d = {**{k: v for k, v in slots_p.items() if k not in slots_d}, **slots_d}
    if not dry:
        if new_p != slots_p:
            sp["slots"] = new_p
            sp.setdefault("active", "")
            sp.setdefault("activeFiles", [])
            tmp = os.path.join(pc, "state.json.tmp")
            json.dump(sp, open(tmp, "w", encoding="utf-8"), indent=2)
            os.replace(tmp, os.path.join(pc, "state.json"))
        if new_d != slots_d:
            sd["slots"] = new_d
            sd.setdefault("active", "")
            sd.setdefault("activeFiles", [])
            ssh(host, f"cat > '{deck}/state.json.tmp' && mv '{deck}/state.json.tmp' '{deck}/state.json'",
                json.dumps(sd, indent=2).encode())
    print(f"seeds: {len(to_deck)} to the Deck, {len(to_pc)} to the PC; slots: +{len(new_d) - len(slots_d)} on the Deck, "
          f"+{len(new_p) - len(slots_p)} on the PC" + (f"; {len(conflicts)} slots differ (each kept its own): {sorted(conflicts)}" if conflicts else "")
          + (" (dry run)" if dry else ""))


if __name__ == "__main__":
    main()

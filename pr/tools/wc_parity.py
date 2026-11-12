"""The ROM-free generator against the real-ROM one (dev tool, needs the vanilla ROM): the same
seed made both ways must give the same seed folder.

  python tools/wc_parity.py [--keep DIR] SEED PRESET [FLAGS...]

Runs tools/wc_seed.py with FF6_WC_ROMFREE=0 and =1 and compares every file the plugin applies
(master tables, messages, files/), wc_seed.json (all but its timestamps) and WC's log (all but
the lines naming its files and time). Exit 0 when they're the same.
"""
import filecmp
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
VOLATILE = ("created", "seconds")
LOG_VOLATILE = ("Input", "Output", "Log ", "Generated")


def make(seed, preset, flags, out, romfree):
    env = dict(os.environ, FF6_WC_ROMFREE="1" if romfree else "0", PYTHONIOENCODING="utf-8")
    cmd = [sys.executable, os.path.join(HERE, "wc_seed.py"), "--seed", seed, "--preset", preset,
           "--flags=" + " ".join(flags), "--out", out]
    p = subprocess.run(cmd, cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        raise SystemExit(f"{'ROM-free' if romfree else 'real ROM'} generation failed: {(p.stderr or p.stdout)[-800:]}")
    return p.stdout.strip().splitlines()[-1]


def compare(a, b):
    diffs = []
    for root, _, files in os.walk(a):
        for fn in files:
            pa = os.path.join(root, fn)
            rel = os.path.relpath(pa, a)
            pb = os.path.join(b, rel)
            if fn == "generate.log":
                continue
            if not os.path.exists(pb):
                diffs.append(f"only in the real-ROM seed: {rel}")
            elif fn == "wc_seed.json":
                ja, jb = (json.load(open(x, encoding="utf-8")) for x in (pa, pb))
                for k in sorted(set(ja) | set(jb)):
                    if k not in VOLATILE and ja.get(k) != jb.get(k):
                        diffs.append(f"wc_seed.json: {k} differs")
            elif fn == "wc_log.txt":
                la, lb = ([line for line in open(x, encoding="utf-8").read().splitlines()
                           if not line.startswith(LOG_VOLATILE)] for x in (pa, pb))
                if la != lb:
                    first = next((i for i, (x, y) in enumerate(zip(la, lb)) if x != y), min(len(la), len(lb)))
                    diffs.append(f"wc_log.txt differs from line {first}: {la[first:first + 1]} / {lb[first:first + 1]}")
            elif not filecmp.cmp(pa, pb, shallow=False):
                diffs.append(f"differs: {rel}")
    for root, _, files in os.walk(b):
        for fn in files:
            rel = os.path.relpath(os.path.join(root, fn), b)
            if fn != "generate.log" and not os.path.exists(os.path.join(a, rel)):
                diffs.append(f"only in the ROM-free seed: {rel}")
    return diffs


def main(argv):
    keep = None
    if argv[:1] == ["--keep"]:
        keep, argv = argv[1], argv[2:]
    seed, preset, flags = argv[0], argv[1], argv[2:]
    tmp = keep or tempfile.mkdtemp(prefix="wc_parity_")
    a, b = os.path.join(tmp, "rom"), os.path.join(tmp, "romfree")
    try:
        print("real ROM: ", make(seed, preset, flags, a, False))
        print("ROM-free: ", make(seed, preset, flags, b, True))
        diffs = compare(a, b)
        print("\n".join(diffs) if diffs else "the same seed folder")
        return 1 if diffs else 0
    finally:
        if not keep:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

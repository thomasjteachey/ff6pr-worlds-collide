"""Check flag strings from the in-game screen's model (plugin/Wc.Tests) against WC's parser.

  python tools/wc_flags_check.py plugin/Wc.Tests/wc_flags_cases.tsv

Each line "name<TAB>flags": WC's argparse must accept the flags; "preset X" lines must also
parse to the same options as "preset-original X" (the preset as written).
"""
import os
import shlex
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
WC = os.path.join(PROJECT, "mods", "worlds-collide")
ROM = os.path.join(PROJECT, "rom", "vanilla", "Final Fantasy III (USA).sfc")


def main():
    cases = [line.rstrip("\n").split("\t", 1) for line in open(sys.argv[1], encoding="utf-8") if "\t" in line]
    os.chdir(WC)
    sys.path.insert(0, WC)
    sys.argv = ["wc.py", "-i", ROM]
    import args  # noqa: E402
    parser = args.parser
    parsed, bad = {}, 0
    for name, flags in cases:
        try:
            ns = parser.parse_args(["-i", ROM] + shlex.split(flags))
            parsed[name] = vars(ns)
        except SystemExit:
            bad += 1
            print(f"REJECTED {name}: {flags}")
    for name in [n for n, _ in cases if n.startswith("preset ")]:
        orig = name.replace("preset ", "preset-original ", 1)
        if name in parsed and orig in parsed and parsed[name] != parsed[orig]:
            bad += 1
            diff = {k: (parsed[orig].get(k), parsed[name].get(k)) for k in parsed[name] if parsed[name].get(k) != parsed[orig].get(k)}
            print(f"DIFFERS {name}: {diff}")
    print(f"{len(cases)} flag strings, {bad} problems")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()

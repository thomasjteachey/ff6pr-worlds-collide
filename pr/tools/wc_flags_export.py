"""Worlds Collide's flags for the in-game options screen -> wc_flags.json.

  .venv/Scripts/python tools/wc_flags_export.py OUT.json

Reads WC's own argparse definitions (mods/worlds-collide/args), so the screen offers exactly
the flags the bundled WC accepts: sections in WC's order, each flag's type, range, choices,
nargs and help, and the website's dropdowns (argparse mutually exclusive groups) as single
choice rows. Presets come from mods/worlds-collide-presets.json.

WC's own build-wc-flag-metadata.py drops the mutually exclusive groups (argparse leaves
them untitled), which is why this exists.
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
WC = os.environ.get("FF6_WC_DIR") or os.path.join(PROJECT, "mods", "worlds-collide")
ROM = os.path.join(PROJECT, "rom", "vanilla", "Final Fantasy III (USA).sfc")
PRESETS = os.path.join(PROJECT, "mods", "worlds-collide-presets.json")

# command-line plumbing, not game options (the seed has its own field on the screen)
SKIP = {"help", "input_file", "output_file", "manifest_file", "seed_id", "debug", "no_rom_output",
        "stdout_log", "website_url", "seed"}
# cosmetic options the PR port leaves out (the user's call, 2026-11-06: no bearing on play): SNES
# palettes and minimap contrast, healing text color, NPC tips, the Y NPC gag. The screen keeps a
# pasted or preset flag it doesn't offer (both presets carry -wmhc), so seeds stay the website's.
COSMETIC_LEFT_OUT = {"character_palettes", "character_sprite_palettes", "world_minimap_high_contrast",
                     "alternate_healing_text_color", "npc_dialog_tips",
                     "y_npc_mascot", "y_npc_creature", "y_npc_imperial", "y_npc_main", "y_npc_reflect",
                     "y_npc_stone", "y_npc_vanish_xzone", "y_npc_sketch", "y_npc_random", "y_npc_remove"}
SKIP |= COSMETIC_LEFT_OUT
# what an option does in the port where that differs from WC's help
HELP = {
    "-frw": "Removes the worst flashes from story scenes (the Pixel Remaster's battle effects are its own).",
    "-frm": "Removes most flashes from story scenes (the Pixel Remaster's battle effects are its own).",
}
SECTION_NAMES = {"options": "Settings", "optional arguments": "Settings", "seed_spoilers": "Settings"}
# choice rows whose flags share no name prefix
CHOICE_NAMES = {("-open", "-cg"): "Game Mode"}
# What each WC section changes in the Pixel Remaster port today (shown on the in-game
# screen): "yes" = data the porter carries; "partly" = some of its flags are data, others
# are WC events or assembly; "no" = events/assembly the port doesn't have yet (PORTING.md);
# "pr" = SNES bug fixes the Pixel Remaster mostly has already.
PORT_STATUS = {
    "Settings": "yes",       # -open/-cg (gates: WcEvents), -sl/-hf (Track)
    "Objectives": "yes",     # tracked (14 of 15 quests), every result applied (WcObjectives)
    "Starting Party": "yes", "Characters": "yes",  # -sal/-stl/-sn/-eu (WcRecruits, port)
    "SwdTechs": "yes", "Blitzes": "yes", "Lores": "yes", "Rages": "yes", "Dances": "yes",  # -fst/-brl/-srr/-rnl/-slr/-sdr/-dns/-dda/-llr
    "Steal": "yes", "Sketch/Control": "yes", "Commands": "yes",  # Steal chances: WcSteal; -scia; -scis: Sketch (plugin), Control (accuracy 255)
    "Experience, Magic Points, Gold": "yes",  # multipliers and -nxppd in WcRewards
    "Scaling": "yes",  # WcScaling: party, checks, dragons, bosses, time
    "Bosses": "yes",   # shuffles / random (WcBattles), -bmkl, -dgne, -srp3
    "Encounters": "yes", "Espers": "yes", "Natural Magic": "yes", "Misc. Magic": "yes",  # -ebs/-ebr/-eer/-eebr/-ems: WcEspers
    "Starting Gold/Items": "yes",  # granted at New Game (WcSeeds.GrantStart)
    "Items": "yes", "Shops": "yes", "Chests": "yes",  # -saw (WcChallenges), -csb, -stra, equipment spells
    "Graphics": "partly", "Coliseum": "yes", "Auction House": "yes",  # names; sprites/portraits with PR art; flashes in story scenes
    "Challenges": "yes",     # data ones port, -nosaves, -pd (WcChallenges; its metamorph sets: port_procs)
    "Bug Fixes": "pr",       # SNES bugs the Pixel Remaster fixes itself; -fbs: the Demon statue's back ledge (wc_events.kt_demon_drop)
    "Misc.": "yes",          # -etr/-etn, -scan, -warp, -move (the PR's own dash), -ond (the job title)
}
# options whose flag names don't say what they do
OPTION_NAMES = {f"-{p}{s}": name for p in ("ls", "hm", "xg")
                for s, name in (("ce", "Characters + Espers"), ("ced", "Characters + Espers + Dragons"))}
OPTION_NAMES["-elr"] = "Random"


ACRONYMS = {"hp": "HP", "mp": "MP", "xp": "XP", "gp": "GP", "exp": "EXP", "npc": "NPC", "ai": "AI", "mpgp": "MP/GP"}


def words(text):
    small = {"and", "or", "of", "the", "to", "in", "on", "by", "for", "per"}
    out = []
    for i, w in enumerate(text.replace("_", " ").replace("-", " ").split()):
        if w.lower() in ACRONYMS:
            out.append(ACRONYMS[w.lower()])
        else:
            out.append(w if (i and w in small) else w[:1].upper() + w[1:])
    return " ".join(out)


def long_name(action):
    return next((o[2:] for o in action.option_strings if o.startswith("--")), None)


def label(action):
    name = long_name(action)
    if name:
        return words(name)
    return (action.help or action.dest).split(".")[0][:40]


def help_text(action):
    text = action.help or ""
    metavar = action.metavar
    if isinstance(metavar, (list, tuple)):
        metavar = " ".join(metavar)
    try:
        text = text % {"metavar": metavar or "", "default": action.default, "dest": action.dest,
                       "choices": "", "prog": "wc.py"}
    except (KeyError, ValueError, TypeError):
        text = text.replace("%(metavar)s", str(metavar or "")).replace("%(default)s", str(action.default))
    return text


def flag_def(action):
    d = {"flag": action.option_strings[0], "label": label(action), "help": HELP.get(action.option_strings[0], help_text(action))}
    nargs = action.nargs
    if nargs == 0:
        d["type"] = "bool"
        return d
    tname = getattr(action.type, "__name__", None) or "str"
    d["type"] = {"int": "int", "float": "float"}.get(tname, "str")
    if nargs is not None:
        d["nargs"] = nargs
    if action.metavar:
        d["metavar"] = list(action.metavar) if isinstance(action.metavar, (list, tuple)) else [action.metavar]
    choices = action.choices
    if isinstance(choices, range):
        d["min"], d["max"] = choices[0], choices[-1]
    elif choices is not None:
        d["choices"] = [c if isinstance(c, str) else c for c in choices]
    if action.default not in (None, False):
        d["default"] = action.default
    if action.const is not None:
        d["const"] = action.const
    return d


def common_prefix(names):
    parts = [n.split("-") for n in names]
    out = []
    for group in zip(*parts):
        if len(set(group)) != 1:
            break
        out.append(group[0])
    return out


def export(parser, presets):
    mx_of = {}
    for i, g in enumerate(parser._mutually_exclusive_groups):
        for a in g._group_actions:
            mx_of[a] = g
    flags, sections, seen = {}, [], set()
    for group in parser._action_groups:
        title = SECTION_NAMES.get(group.title, group.title)
        rows = []
        for a in group._group_actions:
            if a.dest in SKIP or not a.option_strings or a in seen:
                continue
            mx = mx_of.get(a)
            if mx is None:
                seen.add(a)
                flags[a.option_strings[0]] = flag_def(a)
                rows.append({"kind": "flag", "flag": a.option_strings[0]})
                continue
            members = [m for m in mx._group_actions if m.dest not in SKIP and m.option_strings]
            if len(members) == 1:  # a group of one is just a flag
                seen.add(a)
                flags[a.option_strings[0]] = flag_def(a)
                rows.append({"kind": "flag", "flag": a.option_strings[0]})
                continue
            for m in members:
                seen.add(m)
                flags[m.option_strings[0]] = flag_def(m)
            keys = tuple(m.option_strings[0] for m in members)
            names = [long_name(m) or m.dest for m in members]
            prefix = common_prefix(names)
            name = CHOICE_NAMES.get(keys) or (words(" ".join(prefix)) if prefix else " / ".join(flags[k]["label"] for k in keys))
            options = []
            for k, n in zip(keys, names):
                rest = n.split("-")[len(prefix):] if prefix else n.split("-")
                text = OPTION_NAMES.get(k) or words(" ".join(rest))
                if not text or (len(text) <= 3 and text.upper() not in ACRONYMS.values()):
                    # cryptic suffix (-lsce "Ce"): the help says what it is
                    text = flags[k]["help"].split(" (")[0].rstrip(".")
                    if len(text) > 48:
                        text = text[:46].rsplit(" ", 1)[0] + "..."
                options.append({"flag": k, "label": text})
            rows.append({"kind": "choice", "label": name, "options": options})
        if rows:
            existing = next((s for s in sections if s["title"] == title), None)
            if existing:
                existing["rows"].extend(rows)
            else:
                sections.append({"title": title, "rows": rows})
    for sec in sections:
        sec["port"] = PORT_STATUS.get(sec["title"], "unknown")
    return {"flags": flags, "sections": sections, "presets": presets}


def main():
    out = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(PROJECT, "pr", "plugin", "WcPlugin", "wc_flags.json")
    presets = json.load(open(PRESETS, encoding="utf-8")) if os.path.exists(PRESETS) else {}
    os.chdir(WC)
    sys.path.insert(0, WC)
    sys.argv = ["wc.py", "-i", ROM]  # WC's args module parses sys.argv when imported
    import args  # noqa: E402
    import version  # noqa: E402
    data = export(args.parser, presets)
    data["wc_version"] = version.__version__
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
    n = sum(len(s["rows"]) for s in data["sections"])
    choices = sum(r["kind"] == "choice" for s in data["sections"] for r in s["rows"])
    print(f"WC {data['wc_version']}: {len(data['flags'])} flags in {len(data['sections'])} sections, "
          f"{n} rows ({choices} choice rows), {len(presets)} presets -> {out}")


if __name__ == "__main__":
    main()

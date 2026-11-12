"""PR event scripts (map_*/.../sc_e_*.json, sc_map_*.json): find, dump, edit.

A script is {"SystemFlag", "Segments": [{Label, EntryPoint, Count}], "Mnemonics": [...], "Name",
"Title", ...}; a mnemonic is {"label", "mnemonic", "operands": {"iValues", "rValues",
"sValues"}, "type" (1 main, 2 puppet), "comment"}. Jumps are mnemonic indices:
Jump/Call [target], Branch [index, value, target] [category, op, "imm"] (if
category[index] op value: goto target), SetPuppet [slot, entity, entry]. A jump to T goes on
at T + 1 (targets are labelled Nops, which never run themselves). Appending code at the end of a
script and jumping to it keeps every vanilla index unchanged (hook()).

  .venv/Scripts/python tools/pr_script.py NAME [NAME...]   (dump with indices)
"""
import copy
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402

_index = None


def _script_files():
    """Every vanilla script file, relative to the vanilla folder (cached: vcache)."""
    import vcache
    return vcache.cached("script_files_v1", lambda: [
        os.path.relpath(f, P.VANILLA) for f in glob.glob(os.path.join(P.VANILLA, "map_*", "**", "s*.json"), recursive=True)])


def index():
    """{script name: path} over every vanilla map folder."""
    global _index
    if _index is None:
        _index = {}
        for rel in _script_files():
            f = os.path.join(P.VANILLA, rel)
            _index.setdefault(os.path.splitext(os.path.basename(f))[0], f)
    return _index


_all = None


def all_paths(name):
    """Every vanilla file of a script name (some scenes exist in two maps: sc_e_0239_b)."""
    global _all
    if _all is None:
        _all = {}
        for rel in _script_files():
            f = os.path.join(P.VANILLA, rel)
            _all.setdefault(os.path.splitext(os.path.basename(f))[0].lower(), []).append(f)
    return sorted(_all.get(name.lower(), []))


def load_path(f):
    return json.load(open(f, encoding="utf-8"))


def save_path(script, out_root, f):
    """Write a script copy where Magicite serves it (its vanilla file's relative path)."""
    rel_ = os.path.relpath(f, P.VANILLA).replace("\\", "/")
    dest = os.path.join(out_root, *rel_.split("/"))
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(script, ensure_ascii=False, separators=(",", ":")))
    return rel_


def path(name):
    """The vanilla file of a script (names inside files can differ in case: sc_Map_20190)."""
    ix = index()
    if name in ix:
        return ix[name]
    low = {k.lower(): v for k, v in ix.items()}
    return low[name.lower()]


def rel(name):
    """Mod-folder path of a vanilla script (relative to pr/vanilla)."""
    return os.path.relpath(path(name), P.VANILLA).replace("\\", "/")


def load(name):
    return json.load(open(path(name), encoding="utf-8"))


def op(mnemonic, i=(), s=(), r=(), label="", kind=1):
    """One instruction. A jump target in `i` may be "@name": the index of label(name) in the
    same block (resolved by append/hook)."""
    iv = list(i) + [0] * (8 - len(i))
    sv = list(s) + [""] * (8 - len(s))
    rv = list(r) + [0] * (8 - len(r))
    return {"label": label, "mnemonic": mnemonic, "operands": {"iValues": iv, "rValues": rv, "sValues": sv},
            "type": kind, "comment": ""}


def label(name, kind=1):
    """A jump target: a labelled Nop. The VM continues at the instruction AFTER a jump's target
    (Jump [T], Branch [.., .., T], Call [T] go on at T + 1; vanilla targets are labelled Nops),
    so the target itself never runs."""
    return op("Nop", label=name, kind=kind)


def _resolve(ops, base):
    names = {o["label"]: base + n for n, o in enumerate(ops) if o["mnemonic"] == "Nop" and o["label"]}
    for o in ops:
        iv = o["operands"]["iValues"]
        for k, v in enumerate(iv):
            if isinstance(v, str):
                assert v.startswith("@") and v[1:] in names, f"unknown target {v}"
                iv[k] = names[v[1:]]


def append(script, name, ops):
    """Add label(name) + `ops` as a new segment at the end ("@x" targets resolved); returns
    the label's index (jump there to run the block)."""
    at = len(script["Mnemonics"])
    if not script.get("ScriptLocal"):
        # PlaySE [id, 1] keeps the sound's handle in a script-local variable: in a script that
        # declares none the game throws, and the VM hangs (Gau's father's house, 2026-11-05).
        # Vanilla scripts all declare one ("なし" = none) where they use it.
        script["ScriptLocal"] = ["なし"]
        script["ScriptLocalValue"] = [0]
    ops = [label(name)] + copy.deepcopy(ops)
    _resolve(ops, at)
    script["Mnemonics"].extend(ops)
    script["Segments"].append({"Label": name, "EntryPoint": at, "Count": len(ops)})
    return at


def copy_ops(script, a, b):
    """Copies of instructions [a, b) (absolute jump targets stay valid), labels dropped."""
    out = copy.deepcopy(script["Mnemonics"][a:b])
    for o in out:
        if o["mnemonic"] != "Nop":
            o["label"] = ""
    return [o for o in out if o["mnemonic"] != "Nop"]


def hook(script, at, name, ops):
    """Divert the vanilla code at `at` into an appended block: the instruction at `at` becomes
    Jump [block] and moves into the block under label "vanilla", followed by a jump back to
    at + 1. `ops` run first and may go to "@vanilla" to carry on with the vanilla code (or
    jump anywhere else). `at` must not be a jump target (vanilla targets are labelled Nops:
    use the instruction after one)."""
    ms = script["Mnemonics"]
    moved = copy.deepcopy(ms[at])
    assert moved["mnemonic"] != "Nop" or not moved["label"], f"{script.get('Name')}[{at}] is a jump target"
    moved["label"] = ""
    # Jump [at] continues at at + 1
    start = append(script, name, list(ops) + [label("vanilla"), moved, op("Jump", i=[at])])
    ms[at] = op("Jump", i=[start], label=ms[at]["label"], kind=ms[at].get("type", 1))
    return start


def dumps(script):
    out = [f"{script.get('Name')}  {script.get('Title', {}).get('main', '')}  segments "
           + ", ".join(f"{s['Label']}@{s['EntryPoint']}" for s in script["Segments"])]
    for n, m in enumerate(script["Mnemonics"]):
        o = m.get("operands") or {}
        iv = list(o.get("iValues") or [])
        while iv and iv[-1] == 0:
            iv.pop()
        sv = [x for x in (o.get("sValues") or []) if x]
        rv = [x for x in (o.get("rValues") or []) if x]
        lab = f"{m['label']}: " if m.get("label") else ""
        out.append(f"{n:4d} {lab}{m['mnemonic']} {iv if iv else ''} {sv if sv else ''} {rv if rv else ''}".rstrip())
    return "\n".join(out)


def save(script, out_root, name=None):
    """Write the script where Magicite serves it: out_root/<its vanilla relative path>."""
    dest = os.path.join(out_root, *rel(name or script["Name"]).split("/"))
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "w", encoding="utf-8") as f:
        f.write(json.dumps(script, ensure_ascii=False, separators=(",", ":")))
    return dest


def list_in_map(out_root, group, folder, sub, names):
    """Add scripts to their map's entry in the map group's package.json (the port's copy if it
    wrote one, else vanilla's): a map loads only the scripts listed there (MapModel.GetScript;
    a new script missing from it isn't found in game). Returns the package's asset key."""
    tail = (group, "Assets", "GameAssets", "Serial", "Res", "Map", folder, "package.json")
    out = os.path.join(out_root, *tail)
    src = out if os.path.exists(out) else os.path.join(P.VANILLA, *tail)
    doc = json.load(open(src, encoding="utf-8"))
    entry = next(m for m in doc["map"] if m["name"] == sub)
    for name in names:
        asset = f"{sub}/{name}"
        if not any(x["asset"] == asset for x in entry["script"]):
            entry["script"].append({"name": name, "asset": asset})
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, ensure_ascii=False, separators=(",", ":"))
    return f"Assets/GameAssets/Serial/Res/Map/{folder}/package"


_text = None


def text(key):
    """Story/system text for a message key (for dumps)."""
    global _text
    if _text is None:
        _text = {}
        for f in ("story_mes_en", "system_en"):
            for line in open(f"{P.VANILLA}/message/Assets/GameAssets/Serial/Data/Message/{f}.txt", encoding="utf-8"):
                if "\t" in line:
                    k, v = line.rstrip("\n").split("\t", 1)
                    _text[k] = v
    return _text.get(key, "")


def scene(script, quiet=("SetPuppet", "ExecPuppet", "Return", "SetPos", "LookAt", "Move", "PlayAnim",
                         "CueAnim", "Route", "Lerp", "Wait", "Hide", "Show")):
    """Like dumps, without puppet noise and with each Msg's text."""
    out = []
    for line, m in zip(dumps(script).splitlines()[1:], script["Mnemonics"]):
        if m["mnemonic"] in quiet:
            continue
        sv = (m.get("operands") or {}).get("sValues") or []
        if m["mnemonic"] in ("Msg", "Select") and sv and sv[0]:
            line += "  # " + " / ".join(text(k)[:70].replace("\\n", " ") for k in sv if k)
        out.append(line)
    return "\n".join(out)


if __name__ == "__main__":
    args = sys.argv[1:]
    brief = "--scene" in args
    for n in [a for a in args if a != "--scene"]:
        sc = load(n)
        print(scene(sc) if brief else dumps(sc))
        print()

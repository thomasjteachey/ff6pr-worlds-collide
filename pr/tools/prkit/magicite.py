"""Write and check Magicite mod folders.

A Magicite mod folder (one per mod, in the game's StreamingAssets/Magicite/<Mod>/):

    <group>/<container path>        the asset, e.g. master/Assets/GameAssets/Serial/Data/Master/item.csv
    <group>/keys/<anything>.json    {"keys": [asset address], "values": [container path without extension]}

<group> is the game's bundle group (master, message, map_20011, battleextradata...). Master CSVs
hold the header and only the rows the mod changes (Magicite merges them by id); message files
hold only the keys the mod sets. The asset address is the game's own (assetspath's AssetsPath.json):
"item" for a table, "Map_20011_2/sc_e_0021" for a map script.

Writing:
    from prkit import data, magicite
    mod = magicite.MagiciteMod("build/MyMod/magicite", data.GameData("ff5"))
    items = mod.table("item")                   # an editable copy of the game's table
    items.set(1, "buy", 5)                      # only changed rows are written
    mod.text()["MSG_ITEM_NAME_1"] = "Potion+"   # system_en.txt
    mod.asset("battleextradata", "Assets/GameAssets/Serial/Data/BattleExtraData/battle_extra_data.json", "{...}")
    mod.write()

Checking (tests, kit.py check):  problems, notes = magicite.validate(folder, data.GameData("ff6"))
"""
import json
import os
import shutil

from .data import MASTER_PATH, MESSAGE_PATH, Table

TEXT_EXT = (".csv", ".txt", ".json", ".bytes")


class TableEdit(Table):
    """A copy of a game table; set()/add() record which rows the mod writes."""

    def __init__(self, base):
        self.name = base.name
        self.columns = list(base.columns)
        self.rows = {i: dict(r) for i, r in base.rows.items()}
        self.changed = set()

    def set(self, i, col, value):
        if col not in self.columns:
            raise KeyError(f"{self.name} has no column {col!r}")
        value = str(value)
        if self.rows[i][col] != value:
            self.rows[i][col] = value
            self.changed.add(i)

    def add(self, values, copy_of=None, id=None):
        """A new row: a copy of row copy_of (or empty), with values {column: value}; returns its id."""
        new = id if id is not None else self.next_id()
        row = dict(self.rows[copy_of]) if copy_of is not None else {c: "" for c in self.columns}
        row[self.columns[0]] = str(new)
        for k, v in values.items():
            if k not in self.columns:
                raise KeyError(f"{self.name} has no column {k!r}")
            row[k] = str(v)
        self.rows[new] = row
        self.changed.add(new)
        return new

    def touch(self, i):
        """Write row i even if unchanged (a row the mod must own, e.g. against another mod's)."""
        self.changed.add(i)


class MagiciteMod:
    def __init__(self, out_dir, game_data, keys_name="mod"):
        self.out = out_dir
        self.data = game_data
        self.keys_name = keys_name
        self.tables = {}
        self.texts = {}
        self.assets = {}   # (group, container) -> (key, bytes)

    def table(self, name):
        if name not in self.tables:
            self.tables[name] = TableEdit(self.data.table(name))
        return self.tables[name]

    def text(self, file="system_en"):
        """{key: text} the mod sets in message/<file>.txt (a dict to fill)."""
        return self.texts.setdefault(file, {})

    def asset(self, group, container, content, key=None):
        """Any other asset: container 'Assets/...' with its extension; key defaults to the game's address."""
        if isinstance(content, (dict, list)):
            content = json.dumps(content, ensure_ascii=False, indent=1)
        if isinstance(content, str):
            content = content.encode("utf-8")
        stem = os.path.splitext(container)[0]
        if key is None:
            key = {v: k for k, v in self.data.addresses(group).items()}.get(stem) or stem.rsplit("/", 1)[-1]
        self.assets[(group, container)] = (key, content)

    def write(self, clean=True):
        """Write the folder (cleared first by default). Returns {group: number of assets}."""
        if clean and os.path.isdir(self.out):
            shutil.rmtree(self.out)
        os.makedirs(self.out, exist_ok=True)   # an empty mod is still a folder Magicite finds
        listed = {}   # group -> [(key, container stem)]
        for name, t in sorted(self.tables.items()):
            if not t.changed:
                continue
            path = os.path.join(self.out, "master", *MASTER_PATH.split("/"), name + ".csv")
            _write(path, t.to_csv(t.changed).encode("utf-8"))
            listed.setdefault("master", []).append((name, f"{MASTER_PATH}/{name}"))
        for fname, lines in sorted(self.texts.items()):
            if not lines:
                continue
            body = "".join(f"{k}\t{lines[k]}\r\n" for k in sorted(lines))
            _write(os.path.join(self.out, "message", *MESSAGE_PATH.split("/"), fname + ".txt"), body.encode("utf-8"))
            listed.setdefault("message", []).append((fname, f"{MESSAGE_PATH}/{fname}"))
        for (group, container), (key, content) in sorted(self.assets.items()):
            _write(os.path.join(self.out, group, *container.split("/")), content)
            listed.setdefault(group, []).append((key, os.path.splitext(container)[0]))
        for group, entries in listed.items():
            doc = {"keys": [k for k, _ in entries], "values": [v for _, v in entries]}
            _write(os.path.join(self.out, group, "keys", f"{self.keys_name}_{group}.json"),
                   json.dumps(doc, indent=2).encode("utf-8"))
        return {g: len(e) for g, e in listed.items()}


def _write(path, content):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(content)


# --- checking a folder ----------------------------------------------------------------------------

def validate(folder, game_data=None, ignore=()):
    """(problems, notes) for a Magicite mod folder. Problems break loading or do nothing in game:
    a keys entry naming no file, a file no keys entry names, a master CSV whose header isn't the
    game's (the game reads columns by position), a text line without a tab. Notes are worth a
    look: an address the game doesn't have (a new asset), a row id the game doesn't have.
    Files at the folder's top (a plugin's data beside it) and names in ignore are skipped."""
    problems, notes = [], []
    if not os.path.isdir(folder):
        return [f"{folder}: no such folder"], notes
    for group in sorted(os.listdir(folder)):
        gdir = os.path.join(folder, group)
        if not os.path.isdir(gdir) or group in ignore:
            continue
        kdir = os.path.join(gdir, "keys")
        files = {}
        for root, _, names in os.walk(gdir):
            if os.path.abspath(root).startswith(os.path.abspath(kdir)):
                continue
            for n in names:
                rel = os.path.relpath(os.path.join(root, n), gdir).replace("\\", "/")
                files[os.path.splitext(rel)[0]] = rel
        if not os.path.isdir(kdir):
            if files:
                problems.append(f"{group}: {len(files)} files but no keys/ folder (Magicite loads nothing from it)")
            continue
        listed = {}
        for kf in sorted(os.listdir(kdir)):
            if not kf.endswith(".json"):
                continue
            try:
                with open(os.path.join(kdir, kf), encoding="utf-8") as f:
                    doc = json.load(f)
                keys, values = doc["keys"], doc["values"]
            except (OSError, ValueError, KeyError, TypeError) as e:
                problems.append(f"{group}/keys/{kf}: not a keys file ({e})")
                continue
            if len(keys) != len(values):
                problems.append(f"{group}/keys/{kf}: {len(keys)} keys but {len(values)} values")
            for k, v in zip(keys, values):
                if v not in files:
                    problems.append(f"{group}/keys/{kf}: {k} -> {v}: no such file")
                listed[v] = k
        for stem, rel in sorted(files.items()):
            if stem not in listed:
                problems.append(f"{group}/{rel}: no keys entry names it (Magicite ignores it)")
        vanilla = game_data.addresses(group) if game_data is not None else {}
        for stem, key in sorted(listed.items()):
            if game_data is not None and stem in files:
                if key not in vanilla:
                    notes.append(f"{group}: {key} is not an address of the game's (a new asset?)")
                elif vanilla[key] != stem:
                    problems.append(f"{group}: {key} is the game's {vanilla[key]}, not {stem}")
            rel = files.get(stem)
            if rel is None:
                continue
            path = os.path.join(gdir, *rel.split("/"))
            if rel.endswith(".csv") and stem.startswith(MASTER_PATH + "/"):
                _check_csv(game_data, group, key, path, problems, notes)
            elif rel.endswith(".txt") and stem.startswith(MESSAGE_PATH + "/"):
                with open(path, encoding="utf-8", errors="replace") as f:
                    for n, line in enumerate(f, 1):
                        if line.strip() and "\t" not in line:
                            problems.append(f"{group}/{rel}:{n}: no tab (lines are key<TAB>text)")
                            break
    return problems, notes


def _check_csv(game_data, group, name, path, problems, notes):
    try:
        with open(path, encoding="utf-8", newline="") as f:
            mod = Table(name, f.read())
    except (OSError, ValueError, StopIteration) as e:
        problems.append(f"{group}: {name}.csv unreadable ({e})")
        return
    if game_data is None or not game_data.extracted:
        return
    try:
        base = game_data.table(name)
    except OSError:
        notes.append(f"{group}: {name}.csv is not one of the game's tables")
        return
    if mod.columns != base.columns:
        problems.append(f"{group}: {name}.csv columns differ from the game's (it reads them by position)")
    new = [i for i in mod.rows if i not in base.rows]
    if new:
        notes.append(f"{group}: {name}.csv adds {len(new)} rows (ids {min(new)}..{max(new)})")

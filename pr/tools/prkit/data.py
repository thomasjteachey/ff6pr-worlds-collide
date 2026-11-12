"""A PR game's own data, as extracted (kit.py extract <game>): master tables and message text.

    from prkit import data
    d = data.GameData("ff5")
    monsters = d.table("monster")             # Table: columns + rows by id (strings, as in the CSV)
    monsters[1]["hp"], monsters.int(1, "hp")
    names = d.messages("system")              # {"MSG_...": "text"}

Every PR keeps the same layout (Magicite's): master/Assets/GameAssets/Serial/Data/Master/<table>.csv
and message/Assets/GameAssets/Serial/Data/Message/<file>_<lang>.txt ("key<TAB>text" lines).
tools/prdata.py is FF6's older copy of the same reader (kept apart: the WC generator bundle ships it).
"""
import csv
import io
import json
import os

from . import games

MASTER_PATH = "Assets/GameAssets/Serial/Data/Master"
MESSAGE_PATH = "Assets/GameAssets/Serial/Data/Message"


class Table:
    """A master CSV: column order and rows (dicts of strings) keyed by id."""

    def __init__(self, name, text=None, master_dir=None):
        self.name = name
        if text is None:
            if master_dir is None:
                raise ValueError("Table needs the CSV text or a master folder")
            with open(os.path.join(master_dir, name + ".csv"), encoding="utf-8", newline="") as f:
                text = f.read()
        reader = csv.reader(io.StringIO(text))
        self.columns = next(reader)
        self.rows = {}
        for r in reader:
            if r:
                self.rows[int(r[0])] = dict(zip(self.columns, r))

    def __getitem__(self, i):
        return self.rows[i]

    def __contains__(self, i):
        return i in self.rows

    def __iter__(self):
        return iter(self.rows.values())

    def __len__(self):
        return len(self.rows)

    def int(self, i, col):
        return int(self.rows[i][col])

    def next_id(self):
        return max(self.rows, default=0) + 1

    def to_csv(self, ids=None):
        """Header plus the given rows (all by default): Magicite's partial-CSV format."""
        out = io.StringIO()
        w = csv.writer(out, lineterminator="\n")
        w.writerow(self.columns)
        for i in sorted(self.rows if ids is None else ids):
            w.writerow([self.rows[i].get(c, "") for c in self.columns])
        return out.getvalue()


def read_messages(path):
    out = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if "\t" in line:
                k, v = line.split("\t", 1)
                out[k] = v
    return out


class GameData:
    """One game's extracted data folder (default: the project's, games.Game.vanilla)."""

    def __init__(self, game="ff6", vanilla=None):
        self.game = game if isinstance(game, games.Game) else games.get(game)
        self.root = vanilla or self.game.vanilla
        self.master_dir = os.path.join(self.root, "master", *MASTER_PATH.split("/"))
        self.message_dir = os.path.join(self.root, "message", *MESSAGE_PATH.split("/"))
        self._tables = {}
        self._assets = None

    @property
    def extracted(self):
        return os.path.isdir(self.master_dir)

    def _need(self):
        if not self.extracted:
            raise FileNotFoundError(f"{self.game.title}'s data isn't extracted: "
                                    f".venv/Scripts/python tools/kit.py extract {self.game.id}")

    def table(self, name):
        """A master table (cached: treat it as read-only; MagiciteMod.table() gives an editable copy)."""
        self._need()
        if name not in self._tables:
            self._tables[name] = Table(name, master_dir=self.master_dir)
        return self._tables[name]

    def table_names(self):
        self._need()
        return sorted(os.path.splitext(f)[0] for f in os.listdir(self.master_dir) if f.endswith(".csv"))

    def messages(self, name="system", lang="en"):
        self._need()
        return read_messages(os.path.join(self.message_dir, f"{name}_{lang}.txt"))

    def groups(self):
        """Asset groups (bundle names) in the extract, e.g. master, message, map_20010."""
        return sorted(d for d in os.listdir(self.root)
                      if os.path.isdir(os.path.join(self.root, d, "keys")))

    def addresses(self, group):
        """{asset address: container path without extension} of one group, as the game loads it
        (assetspath/.../AssetsPath.json; a map script's address is 'Map_20011_2/sc_e_0021')."""
        if self._assets is None:
            self._assets = {}
            path = os.path.join(self.root, "assetspath", "Assets", "GameAssets", "AssetsPath.json")
            if os.path.exists(path):
                with open(path, encoding="utf-8") as f:
                    doc = json.load(f)
                for g, inner in zip(doc.get("keys", []), doc.get("values", [])):
                    self._assets[g] = inner
        inner = self._assets.get(group)
        if inner is None:
            return {}
        if isinstance(inner, str):
            inner = self._assets[group] = json.loads(inner)
        return dict(zip(inner.get("keys", []), inner.get("values", [])))

    def asset_path(self, group, container):
        """Where an asset of the extract is: container is 'Assets/...' with its extension."""
        return os.path.join(self.root, group, *container.split("/"))


def strip_tags(text):
    import re
    return re.sub(r"<[^>]*>", "", text or "").strip()

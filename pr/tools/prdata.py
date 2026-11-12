"""Read and write FF6 Pixel Remaster data extracted by tools/extract.py."""
import csv
import io
import os

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
VANILLA = os.path.join(ROOT, "vanilla")
MASTER = os.path.join(VANILLA, "master", "Assets", "GameAssets", "Serial", "Data", "Master")
MESSAGE = os.path.join(VANILLA, "message", "Assets", "GameAssets", "Serial", "Data", "Message")
# the US v1.0 ROM every mod is diffed against: FF6_VANILLA_ROM, else rom/ next to tools/ (the
# Worlds Collide generator bundle), else the project's rom/vanilla/
ROM_NAME = "Final Fantasy III (USA).sfc"
VANILLA_ROM = os.environ.get("FF6_VANILLA_ROM") or next(
    (p for p in (os.path.join(ROOT, "rom", ROM_NAME), os.path.join(ROOT, "..", "rom", "vanilla", ROM_NAME))
     if os.path.exists(p)), os.path.join(ROOT, "..", "rom", "vanilla", ROM_NAME))


class Table:
    """A PR master CSV: keeps column order and rows (as dicts of strings) keyed by id."""

    def __init__(self, name, text=None):
        self.name = name
        if text is None:
            with open(os.path.join(MASTER, name + ".csv"), encoding="utf-8", newline="") as f:
                text = f.read()
        reader = csv.reader(io.StringIO(text))
        self.columns = next(reader)
        self.rows = {}
        for r in reader:
            if r:
                self.rows[int(r[0])] = dict(zip(self.columns, r))

    def __getitem__(self, i):
        return self.rows[i]

    def __iter__(self):
        return iter(self.rows.values())

    def int(self, i, col):
        return int(self.rows[i][col])

    def to_csv(self, ids):
        """Header plus the given rows: Magicite's partial-CSV format."""
        out = io.StringIO()
        w = csv.writer(out, lineterminator="\n")
        w.writerow(self.columns)
        for i in sorted(ids):
            w.writerow([self.rows[i][c] for c in self.columns])
        return out.getvalue()


def messages(name="system", lang="en"):
    out = {}
    with open(os.path.join(MESSAGE, f"{name}_{lang}.txt"), encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if "\t" in line:
                k, v = line.split("\t", 1)
                out[k] = v
    return out


def strip_tags(text):
    import re
    return re.sub(r"<[^>]*>", "", text or "").strip()

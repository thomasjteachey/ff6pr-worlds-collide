"""Values computed from the vanilla files (pr/vanilla, the bundle's generator/vanilla) that every
seed recomputed: the script index, the chests, the files a seed may change. They're kept in
<vanilla>/../cache/<name>.json, made again when the vanilla folder's listing or mtime changes
(the bundle ships them made: wc_bundle.py). A seed on the Deck spent seconds walking 20 MB of
small files through Wine for these.
"""
import hashlib
import json
import os

import prdata as P

_stamp = None


# a file every extract writes: its size and time survive a copy (copy2, tar), folders' times don't
MARK = os.path.join("assetspath", "Assets", "GameAssets", "AssetsPath.json")


def stamp():
    """The vanilla folder as it is: its top-level names and its asset list's size and time."""
    global _stamp
    if _stamp is None:
        h = hashlib.sha1()
        with os.scandir(P.VANILLA) as it:
            for name in sorted(e.name for e in it):
                h.update(name.encode() + b";")
        try:
            st = os.stat(os.path.join(P.VANILLA, MARK))
            h.update(f"{st.st_size}:{int(st.st_mtime)}".encode())
        except OSError:
            pass
        _stamp = h.hexdigest()
    return _stamp


def folder():
    return os.path.join(os.path.dirname(os.path.abspath(P.VANILLA)), "cache")


def cached(name, compute):
    """compute() (JSON-able), from the cache when it was made from this vanilla folder."""
    path = os.path.join(folder(), name + ".json")
    try:
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        if doc.get("stamp") == stamp():
            return doc["value"]
    except (OSError, ValueError, KeyError):
        pass
    value = compute()
    try:
        os.makedirs(folder(), exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(json.dumps({"stamp": stamp(), "value": value}, ensure_ascii=False, separators=(",", ":")))
        os.replace(tmp, path)
    except OSError:
        pass
    return value

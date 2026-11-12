"""The mod manifest, pr/modkit.json: every mod the project ships and what it's made of.

    from prkit import mods
    m = mods.get("bnw")
    m.name, m.game, m.plugin_files, m.magicite, m.version()
    mods.all_mods()
"""
import json
import os
import sys

from . import PR, games

MANIFEST = os.path.join(PR, "modkit.json")


class Mod:
    def __init__(self, mod_id, doc, root=PR):
        self.id = mod_id
        self.doc = doc
        self.root = root
        self.name = doc["name"]
        self.game_id = games.resolve_id(doc.get("game", "ff6"))
        self.title = doc.get("title", self.name)
        plugin = doc.get("plugin") or {}
        self.plugin_project = plugin.get("project")
        self.plugin_files = [tuple(x) for x in plugin.get("files", [])]
        self.plugin_dirs = [tuple(x) for x in plugin.get("dirs", [])]
        self.magicite = doc.get("magicite")
        self.magicite_skip = set(doc.get("magicite_skip", []))
        self.exclusive = doc.get("exclusive")
        self.keep = list(doc.get("keep", []))
        self.build = [list(c) for c in doc.get("build", [])]
        self.post_plugin_build = [list(c) for c in doc.get("post_plugin_build", [])]
        self.deploy = doc.get("deploy")
        self.readme_note = doc.get("readme_note", "")
        # made on the player's machine from their own game (the zip never carries the game's files)
        self.package_leave_out = [p.strip("/") for p in doc.get("package_leave_out", [])]
        self.package_magicite = doc.get("package_magicite", True)
        self.player_setup = doc.get("player_setup")

    def __repr__(self):
        return f"Mod({self.id}: {self.name}, {self.game_id})"

    @property
    def game(self):
        return games.get(self.game_id)

    def path(self, rel):
        return os.path.normpath(os.path.join(self.root, rel))

    @property
    def has_plugin(self):
        return bool(self.plugin_files or self.plugin_dirs)

    def version(self):
        if self.doc.get("version"):
            return str(self.doc["version"])
        vf = self.doc.get("version_file")
        if not vf:
            return ""
        try:
            with open(self.path(vf), encoding="utf-8") as f:
                return str(json.load(f).get(self.doc.get("version_key", "version")) or "")
        except (OSError, ValueError):
            return ""

    def missing(self):
        """Built parts that aren't there yet (paths relative to pr/)."""
        out = [p for p, _ in self.plugin_files if not os.path.exists(self.path(p))]
        out += [p for p, _ in self.plugin_dirs if not os.path.isdir(self.path(p))]
        if self.magicite and not os.path.isdir(self.path(self.magicite)):
            out.append(self.magicite)
        return out

    def commands(self, which):
        """The manifest's commands ('build' / 'post_plugin_build') as argv lists, {python} filled in."""
        return [[sys.executable if a == "{python}" else a for a in cmd] for cmd in getattr(self, which)]

    def rivals(self, all_mods=None):
        """Other mods for the same game with the same exclusive tag."""
        if not self.exclusive:
            return []
        return [m for m in (all_mods or all_mods_list(self.root)) if m.id != self.id
                and m.exclusive == self.exclusive and m.game_id == self.game_id]


def load(root=PR):
    path = os.path.join(root, "modkit.json")
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    return {k: Mod(k, v, root) for k, v in doc.get("mods", {}).items()}


def all_mods_list(root=PR):
    return list(load(root).values())


def get(mod_id, root=PR):
    found = load(root)
    if mod_id in found:
        return found[mod_id]
    for m in found.values():   # by folder name too: BraveNewWorld
        if m.name.lower() == str(mod_id).lower():
            return m
    raise KeyError(f"no mod {mod_id!r} in {os.path.join(root, 'modkit.json')} ({', '.join(found)})")


def add(mod_id, doc, root=PR):
    """Add a mod to the manifest (scaffold): keeps the file's other entries and their order."""
    path = os.path.join(root, "modkit.json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    else:
        manifest = {"mods": {}}
    if mod_id in manifest.setdefault("mods", {}):
        raise KeyError(f"{mod_id} is already in {path}")
    manifest["mods"][mod_id] = doc
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(dumps(manifest) + "\n")


def _flat(v):
    return not isinstance(v, (dict, list)) or (isinstance(v, list) and all(not isinstance(x, (dict, list)) for x in v))


def dumps(obj, indent=0):
    """JSON as the manifest is written by hand: objects one key a line, short lists on one line."""
    pad, inner = "  " * indent, "  " * (indent + 1)
    if isinstance(obj, dict):
        if not obj:
            return "{}"
        items = [f"{inner}{json.dumps(k, ensure_ascii=False)}: {dumps(v, indent + 1)}" for k, v in obj.items()]
        return "{\n" + ",\n".join(items) + "\n" + pad + "}"
    if isinstance(obj, list):
        if all(_flat(x) for x in obj):
            one = "[" + ", ".join(json.dumps(x, ensure_ascii=False) if not isinstance(x, list) else dumps(x) for x in obj) + "]"
            if len(one) + len(pad) < 110:
                return one
        return "[\n" + ",\n".join(inner + dumps(x, indent + 1) for x in obj) + "\n" + pad + "]"
    return json.dumps(obj, ensure_ascii=False)

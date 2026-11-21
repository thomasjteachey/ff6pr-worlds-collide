"""Worlds Collide seeds in a running game: what a seed may change, and the WC mod folder's
baseline. Used by wc_seed.py (each seed) and deploy (the baseline).

The shared part (any randomizer's seeds: managed tables, chest maps, AI slots, full tables and
text, the baseline's layout) is the kit's tools/seed_live.py; WC adds its re-skinned check NPCs'
maps (tools/wc_npcs.py) and its static event scripts and messages (tools/wc_events.py).

  .venv/Scripts/python tools/wc_live.py baseline OUT_DIR   # the WorldsCollide Magicite folder
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import kitpath  # noqa: E402,F401  (the modding kit's tools, when it's this repository's kit/ submodule)
import seed_live  # noqa: E402
from seed_live import (MANAGED_TABLES, MESSAGE_FILES, AI_SLOTS, MASTER_PATH, MESSAGE_PATH,  # noqa: E402,F401
                       chest_files, ai_files, miab_files, extra_files, empty_chest_files, full_table,
                       full_messages)


def managed_files():
    return seed_live.managed_files(npc_files())


def npc_files():
    """Map files whose check NPCs a seed re-skins as their reward (tools/wc_npcs.py)."""
    import wc_npcs
    return wc_npcs.files()


def write_baseline(out):
    """The WorldsCollide Magicite folder: every file a seed may change, vanilla content, and
    WC's event changes (static scripts; the plugin does each seed's part)."""
    def events(out, add):
        import wc_events
        for rel in wc_events.build(out):
            add(rel)
        msgs = wc_events.write_messages(out, MESSAGE_PATH.split("/", 1)[1])  # group "message", path inside it
        os.makedirs(os.path.join(out, "message", "keys"), exist_ok=True)
        with open(os.path.join(out, "message", "keys", "wc_message.json"), "w", encoding="utf-8") as f:
            json.dump({"keys": list(msgs), "values": list(msgs.values())}, f, indent=1)

    seed_live.write_baseline(out, more_maps=npc_files(), extra=events, baseline_name="wc_baseline.json",
                             files=managed_files())


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "baseline":
        write_baseline(os.path.abspath(sys.argv[2]))
    else:
        sys.exit(__doc__)

"""Put the Worlds Collide install back after a self-test that didn't finish (killed or hung):
no active seed unless one is given, test seeds ("*-test") deleted, their save-slot records
dropped. The plugin brings the Magicite files back in line with the active seed (the baseline
without one) on the next start.

  .venv/Scripts/python tools/wc_test_reset.py [ACTIVE_SEED_ID]
"""
import json
import os
import shutil
import sys

SEEDS = r"D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR\BepInEx\plugins\WorldsCollide\seeds"


def main():
    active = sys.argv[1] if len(sys.argv) > 1 else ""
    path = os.path.join(SEEDS, "state.json")
    state = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}
    was = state.get("active", "")
    state["active"] = active
    if active != was:
        state["activeFiles"] = []
    state["slots"] = {k: v for k, v in (state.get("slots") or {}).items() if not v.endswith("-test")}
    json.dump(state, open(path, "w", encoding="utf-8"), indent=2)
    removed = []
    for d in os.listdir(SEEDS):
        # a test seed, and the work folder of one whose generation was cut short
        if d.endswith(("-test", "-test.work")) and os.path.isdir(os.path.join(SEEDS, d)):
            shutil.rmtree(os.path.join(SEEDS, d))
            removed.append(d)
    print(f"active seed {was or 'none'} -> {active or 'none'}; removed {len(removed)} test seeds {removed}")


if __name__ == "__main__":
    main()

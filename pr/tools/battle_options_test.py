"""Battle Options: a headless check in the game (only with the user's OK, never while they play).

    .venv/Scripts/python tools/battle_options_test.py [--game ff4|ff5|ff6] [--party 1] [--minutes 12]

Starts the selected ATB Pixel Remaster with no window and no sound through the kit's runner
(tools/prkit/headless.py), with
Battle Options' own request (BepInEx/battleoptions_test.request: saves blocked, the harness on), so a
kit mod installed beside it (Brave New World) keeps its own runs. Then, over the harness:
  1. the title's Config screen: Battle Mode's choices (Wait / Active / Pause, each step's help line),
     the Cast Bars and Enemy ATB rows (their place, choices, help);
  2. a save loaded and a battle: with Pause, enemy gauges held while a character's turn is up;
     a character's command and its wait drawn as a cast bar; where enemy gauges go on screen.
Checksums the save folder before and after (nothing may change). Prints every reply.
"""
import argparse
import hashlib
import os
import re
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import game_harness  # noqa: E402
from prkit import headless  # noqa: E402

def checksums(root):
    out = {}
    for d, _, files in os.walk(root):
        for n in files:
            p = os.path.join(d, n)
            with open(p, "rb") as f:
                out[p] = (hashlib.sha1(f.read()).hexdigest(), os.path.getmtime(p))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", choices=("ff4", "ff5", "ff6"), default="ff6")
    ap.add_argument("--party", default="1", help="the battle's monster party")
    ap.add_argument("--minutes", type=int, default=12)
    a = ap.parse_args()
    number = {"ff4": "IV", "ff5": "V", "ff6": "VI"}[a.game]
    saves = os.path.join(os.path.expanduser("~"), "Documents", "My Games", f"FINAL FANTASY {number} PR")
    before = checksums(saves)
    headless.REQUEST = "battleoptions_test.request"
    done = {}
    t = threading.Thread(target=lambda: done.setdefault("rc", headless.run(a.game, harness=True, minutes=a.minutes)))
    t.start()
    try:
        sock = game_harness.connect(150)
        f = sock.makefile("r", encoding="utf-8", newline="\n")

        def ask(line, show=True):
            r = game_harness.ask(sock, f, line)
            if show:
                print(f"> {line}\n{r}", flush=True)
            return r

        def until(line, pattern, timeout=120, every=1.0, repeat=None):
            end = time.time() + timeout
            while time.time() < end:
                r = ask(line, show=False)
                if re.search(pattern, r):
                    print(f"> {line}  (until /{pattern}/)\n{r}", flush=True)
                    return r
                if repeat:
                    ask(repeat, show=False)
                time.sleep(every)
            print(f"> {line}: no /{pattern}/ in {timeout} s; last: {r}", flush=True)
            return None

        def battle_page():
            """the open Config page that has Battle Mode: (its selected row's type, the report) or None"""
            r = ask("bo menu", show=False)
            for line in r.split(" \\n "):
                m = re.match(r"menu '.*' active True: \d+ rows, types \[([^\]]*)\], selected (-?\d+)", line.strip())
                if m and "1" in m.group(1).split():
                    return int(m.group(2)), r
            return None

        def select(row_type, tries=24):
            for _ in range(tries):
                page = battle_page()
                if page and page[0] == row_type:
                    return True
                ask("press down", show=False)
                ask("wait 6", show=False)
            print(f"[test] row type {row_type} not reached", flush=True)
            return False

        ask("bo")
        # the title (Start leaves its attract demo), then at once a New Game (the harness's title load
        # doesn't take with WC installed), messages skipped
        until("state", r"scene title", 240, 2.0, repeat="press start")
        ask("skipmsg on")
        until("newgame", r"^(?!error)", 90, 1.0, repeat="press start")
        until("state", r"scene field", 240, 2.0)
        ask("wait 90")
        # the in-game menu's Config (the title's has only system settings)
        ask("bo cfg")
        ask("wait 120")
        ask("bo menu")
        # a page list first, if any: enter each page in turn until the one with Battle Mode is open
        for i in range(10):
            if battle_page():
                break
            ask("press action")
            ask("wait 30")
            if battle_page():
                break
            ask("press cancel")
            ask("wait 20")
            ask("press down")
            ask("wait 10")
        ask("bo menu")
        # Battle Mode: three steps right come back round (Wait / Active / Pause)
        if select(1):
            for _ in range(3):
                ask("press right")
                ask("wait 10")
                ask("bo")
                ask("bo menu")
        # Cast Bars and Enemy ATB, each stepped twice
        for row in (7101, 7102):
            if select(row):
                ask("bo menu")
                for _ in range(2):
                    ask("press right")
                    ask("wait 6")
                    ask("bo")
        ask("bo set pause on")
        for _ in range(6):
            ask("press cancel")
            ask("wait 30")
        ask("state")
        # a battle where the field is (encount: also while the map's script runs)
        until("encount " + a.party, r"^(?!error)", 60, 3.0)
        until("state", r"scene battle", 60, 1.0)
        ask("wait 240")
        # Pause on: the queue, and enemy gauges held while it isn't empty
        for i in range(8):
            ask("bo")
            ask("bo layout")
            ask("wait 30")
        # a character's command: Fight on the first enemy, then its wait as a cast bar
        for _ in range(3):
            ask("press action")
            ask("wait 4")
            ask("bo")
        for i in range(6):
            ask("bo")
            ask("wait 15")
        ask("bo set pause off")
        for i in range(4):
            ask("bo")
            ask("bo layout")
            ask("wait 30")
        ask("quit")
    except Exception as e:
        print(f"[test] harness: {e}", flush=True)
    t.join()
    after = checksums(saves)
    changed = [p for p in set(before) | set(after) if before.get(p) != after.get(p)]
    print(f"[test] save folder: {len(after)} files, {len(changed)} changed" + ("".join(f"\n  CHANGED {p}" for p in changed)))
    return 1 if changed else 0


if __name__ == "__main__":
    sys.exit(main())

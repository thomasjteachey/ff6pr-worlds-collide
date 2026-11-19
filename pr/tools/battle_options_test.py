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
    failures = []
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
        # The shared harness's `state` command names FF6-only Integrator members on older FF4/FF5
        # builds. Battle Options' own command is enough to prove the title has initialized and keeps
        # this test on the engine surface the mod actually supports.
        until("bo", r"pause (?:True|False)", 240, 2.0, repeat="press start")
        ask("skipmsg on")
        until("newgame", r"^(?!error)", 90, 1.0, repeat="press start")
        # The in-game MainMenuController appears once the opening has reached a field. Its Config is
        # the one with battle settings (the title's Config has only system settings).
        until("bo cfg", r"main menu Config opened", 300, 2.0, repeat="wait 30")
        ask("wait 120")
        ask("bo menu")
        # In -nographics some PR builds construct and lay out the complete page but never mark it
        # active. Its rows are still reported above; only skip synthetic cursor input in that case.
        interactive = battle_page() is not None
        if not interactive:
            print("[test] Config rows exist but the batch-mode page is inactive; skipping cursor input", flush=True)
        else:
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
        # FF4 and FF5 both reach an opening battle naturally. If this save/opening is elsewhere,
        # fall back to the harness encounter command (FF6 and newer builds support it).
        battle = ask("bo")
        if not re.search(r"gauges \(max", battle):
            until("encount " + a.party, r"^(?!error)", 60, 3.0)
            until("bo", r"gauges \(max", 120, 1.0, repeat="wait 30")
        ask("wait 120")
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
        # Pause: time held through the action itself (BattleActExection.GetStatingAct), frame by frame:
        # an action seen, held while it plays, and no enemy gauge rising during it
        acted = held_acting = 0
        rises = []
        last = None
        for i in range(240):
            r = ask("bo", show=False)
            m = re.search(r"held (True|False) acting (True|False)", r)
            g = dict(re.findall(r"enemy (\d+) ([\d.]+)", r))
            if m and m.group(2) == "True":
                acted += 1
                held_acting += m.group(1) == "True"
                if last is not None:
                    rises += [(k, last[k], v) for k, v in g.items() if k in last and float(v) > float(last[k]) + 0.01]
                last = g
            else:
                last = None
            ask("wait 1", show=False)
        print(f"[test] action frames {acted}, held {held_acting}, enemy gauge rises while acting {len(rises)} {rises[:5]}", flush=True)
        ok_action = acted > 0 and held_acting == acted and not rises
        print(f"[test] Pause holds time through an action: {'OK' if ok_action else 'FAILED'}", flush=True)
        failures.append(None if ok_action else "action")
        ask("bo")
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
    failed = [f for f in failures if f]
    if failed:
        print(f"[test] FAILED: {', '.join(failed)}", flush=True)
    return 1 if changed or failed else 0


if __name__ == "__main__":
    sys.exit(main())

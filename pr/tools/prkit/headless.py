"""Start a Pixel Remaster headless for a kit plugin's test run: no window, no sound, no saves.

    kit.py run <game> [--harness] [--minutes 30] [--until REGEX] [--set key=value ...] [--dry-run]

Then talk to it (with --harness) from another shell:
    .venv/Scripts/python tools/game_harness.py state "press action" "wait 60"

Only with the user's OK, never while they play (it starts their game). What it does, as
tools/probe_headless.ps1 does for BNW's probe:
  - refuses if the game runs, or another run (this one's lock, or the probe's) is under way;
  - writes BepInEx/prkit_test.request (a kit plugin reads and deletes it at start: one run only;
    it blocks every save, mutes the game and, with harness=1, listens for commands);
  - adds steam_appid.txt so Steamworks doesn't relaunch the game through Steam (a normal window),
    and removes it afterwards if it added it;
  - sets the previous BepInEx log aside;
  - starts the game with -batchmode -nographics, hidden;
  - stops its own game (only that process) if it shows a window, if someone else starts the game
    meanwhile, when --until matches the log, or after --minutes;
  - prints the log's kit lines ([kit], [test], [check], [harness], errors).
A kit plugin of that game must be installed (kit.py deploy <mod> --pc): the request does nothing
otherwise.
"""
import ctypes
import os
import re
import subprocess
import sys
import time

from . import games

REQUEST = "prkit_test.request"
LOCK = "prkit_run.lock"
OTHER_LOCKS = ["probe_run.lock"]          # tools/probe_headless.ps1's
KIT_LINES = re.compile(r"\[kit\]|\[test\]|\[check\]|\[harness\]|\[Error|Exception")


def _pid_alive(pid):
    if not pid:
        return False
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
    return str(pid) in out


def _read_pid(path):
    try:
        with open(path, encoding="ascii", errors="ignore") as f:
            return int(f.read().strip() or 0)
    except (OSError, ValueError):
        return 0


def _pids(process):
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {process}.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    pids = []
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            pids.append(int(parts[1]))
    return pids


def _has_visible_window(pid):
    """Does the process own a visible top-level window? (EnumWindows; False off Windows)."""
    if os.name != "nt":
        return False
    user32 = ctypes.windll.user32
    found = []
    proto = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(hwnd, _):
        owner = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
            return False
        return True

    user32.EnumWindows(proto(cb), 0)
    return bool(found)


def request_text(settings):
    return "".join(f"{k}={v}\n" for k, v in settings.items())


def run(game_id, harness=False, minutes=30, until=None, settings=None, dry_run=False, out=print):
    g = games.get(game_id)
    if not g.installed:
        out(f"[run] {g.title} PR isn't installed")
        return 1
    st = g.status()
    if not st.get("bepinex"):
        out(f"[run] no BepInEx in {g.install} (kit.py setup {g.id})")
        return 1
    req = dict(settings or {})
    if harness:
        req["harness"] = "1"
    req.setdefault("minutes", str(minutes))
    args = [g.exe, "-batchmode", "-nographics"]
    if dry_run:
        out(f"[run] would write {os.path.join(g.bepinex, REQUEST)}:\n{request_text(req)}[run] would start: {' '.join(args)} (hidden)")
        return 0
    if g.running():
        out(f"[run] {g.title} is running: not starting a run")
        return 1
    lock = os.path.join(g.bepinex, LOCK)
    for name in OTHER_LOCKS + [LOCK]:
        p = os.path.join(g.bepinex, name)
        if os.path.exists(p) and _pid_alive(_read_pid(p)):
            out(f"[run] another run is under way ({name}): not starting")
            return 1
    with open(lock, "w", encoding="ascii") as f:
        f.write(str(os.getpid()))
    log = os.path.join(g.bepinex, "LogOutput.log")
    appid = os.path.join(g.install, "steam_appid.txt")
    added_appid = not os.path.exists(appid)
    result = "timeout"
    proc = None
    pattern = re.compile(until) if until else None
    start = time.time()
    try:
        with open(os.path.join(g.bepinex, REQUEST), "w", encoding="ascii", newline="\n") as f:
            f.write(request_text(req))
        if added_appid:
            with open(appid, "w", encoding="ascii") as f:
                f.write(str(g.appid))
        if os.path.exists(log):
            os.replace(log, os.path.join(g.bepinex, "LogOutput.prev.log"))
        si = subprocess.STARTUPINFO()
        si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        si.wShowWindow = 0  # SW_HIDE
        proc = subprocess.Popen(args, cwd=g.install, startupinfo=si)
        out(f"[run] {g.title} started headless (pid {proc.pid}); stops after {minutes} min")
        while time.time() - start < minutes * 60 + 60:
            time.sleep(0.5)
            if proc.poll() is not None:
                result = "the game exited"
                if time.time() - start < 60:
                    # exited at once: Steam may be relaunching it outside this run (no request, so no
                    # save block): a game started since this run began is stopped
                    time.sleep(20)
                    for pid in _pids(g.process):
                        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
                        result = "the game exited at start and was relaunched (by Steam?): stopped the relaunched game"
                break
            if _has_visible_window(proc.pid):
                proc.kill()
                result = "a window appeared: stopped the run's game"
                break
            if any(pid != proc.pid for pid in _pids(g.process)):
                proc.kill()
                result = "the game was started by someone else: stopped the run (that game left alone)"
                break
            if pattern and os.path.exists(log):
                with open(log, encoding="utf-8", errors="replace") as f:
                    if pattern.search(f.read()):
                        result = "matched --until"
                        time.sleep(3)
                        break
    finally:
        if proc is not None and proc.poll() is None:
            proc.kill()
            result += " (stopped the run's game)"
        if added_appid and os.path.exists(appid):
            os.remove(appid)
        if os.path.exists(os.path.join(g.bepinex, REQUEST)):
            os.remove(os.path.join(g.bepinex, REQUEST))   # unread: no kit plugin took it
        if os.path.exists(lock):
            os.remove(lock)
    out(f"[run] {result} after {int(time.time() - start)} s")
    if os.path.exists(log):
        with open(log, encoding="utf-8", errors="replace") as f:
            for line in f:
                if KIT_LINES.search(line) or (pattern and pattern.search(line)):
                    out(line.rstrip())
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1]))

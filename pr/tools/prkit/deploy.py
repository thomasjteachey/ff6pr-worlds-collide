"""Build a mod and install it in its game on this PC, and push it to the Steam Deck.

    kit.py deploy <mod> [--pc | --deck] [--full] [--no-build] [--dry-run]

A mod whose manifest entry has its own "deploy" command (Brave New World, Worlds Collide:
tools/deploy.sh) runs that instead. Otherwise:

  build  the manifest's "build" commands (data scripts), dotnet build -c Release of the plugin,
         then its "post_plugin_build" commands
  PC     the game must be closed. Mods with the same "exclusive" tag for this game are set aside
         first: BepInEx/plugins/<Other> -> BepInEx/mod-shelf/<Other> (BepInEx loads nothing there),
         its Magicite folder -> BepInEx/mod-shelf/<Other>.magicite. This mod's shelved copy comes
         back (with its "keep" folders, e.g. saves of its own), BepInEx/plugins/<Mod> is refilled
         ("keep" folders stay), and Magicite/<Mod> mirrors the built folder.
  Deck   (ssh host "deck", keys in pageant; PRKIT_DECK_HOST to change) the same game under Proton:
         BepInEx itself on the first push or with --full, Wine told to load BepInEx's winhttp.dll
         (tools/deck_winhttp_override.py), the same shelving, then the plugin folder,
         BepInEx/config and the Magicite folder. The game must be closed there too.
"""
import os
import shlex
import shutil
import subprocess

from . import PR, TOOLS, games, mods

GIT_BASH = [r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files (x86)\Git\bin\bash.exe"]


class DeployError(Exception):
    pass


def msys_path(path):
    """D:\\x\\y -> /d/x/y: the form Git Bash's tar and redirections read without surprises
    (tar takes "D:/..." for a remote host:path)."""
    p = path.replace("\\", "/")
    if len(p) > 2 and p[1] == ":" and p[0].isalpha():
        p = "/" + p[0].lower() + p[2:]
    return p


def bash_exe():
    """Git Bash (its ssh and tar are the ones tools/deploy.sh uses), never WSL's bash.exe."""
    for p in GIT_BASH:
        if os.path.exists(p):
            return p
    found = shutil.which("bash")
    if found and "system32" not in found.lower():
        return found
    raise DeployError("Git Bash not found (needed for ssh/tar to the Deck)")


class Deployer:
    def __init__(self, mod, dry_run=False, out=print):
        self.m = mod
        self.g = mod.game
        self.dry = dry_run
        self.say = lambda s: out(f"[deploy] {s}")

    # --- build ---------------------------------------------------------------------------------------

    def run(self, cmd, cwd=PR):
        self.say(("would run: " if self.dry else "run: ") + " ".join(cmd))
        if self.dry:
            return
        r = subprocess.run(cmd, cwd=cwd)
        if r.returncode != 0:
            raise DeployError(f"{cmd[0]} failed ({r.returncode})")

    def build(self):
        for cmd in self.m.commands("build"):
            self.run(cmd)
        if self.m.plugin_project:
            self.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd=self.m.path(self.m.plugin_project))
        for cmd in self.m.commands("post_plugin_build"):
            self.run(cmd)
        if not self.dry:
            missing = self.m.missing()
            if missing:
                raise DeployError("the build didn't make: " + ", ".join(missing))

    # --- this PC -------------------------------------------------------------------------------------

    def _move(self, src, dst):
        self.say(f"{'would move' if self.dry else 'move'} {src} -> {dst}")
        if self.dry:
            return
        if os.path.exists(dst):
            shutil.rmtree(dst)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.move(src, dst)

    def install_pc(self):
        g, m = self.g, self.m
        if not g.installed:
            raise DeployError(f"{g.title} PR isn't installed on this PC")
        if not g.status().get("bepinex"):
            raise DeployError(f"no BepInEx in {g.install}: tools/kit.py setup {g.id}")
        if g.running():
            if not self.dry:
                raise DeployError(f"{g.title} is running on this PC: close it first")
            self.say(f"({g.title} is running on this PC: a real deploy would stop here)")
        plugin = os.path.join(g.plugins_dir, m.name)
        magicite = os.path.join(g.magicite_dir, m.name)
        for r in m.rivals():
            if os.path.isdir(os.path.join(g.plugins_dir, r.name)):
                self._move(os.path.join(g.plugins_dir, r.name), os.path.join(g.shelf_dir, r.name))
            if os.path.isdir(os.path.join(g.magicite_dir, r.name)):
                self._move(os.path.join(g.magicite_dir, r.name), os.path.join(g.shelf_dir, r.name + ".magicite"))
        if not os.path.isdir(plugin) and os.path.isdir(os.path.join(g.shelf_dir, m.name)):
            self._move(os.path.join(g.shelf_dir, m.name), plugin)
        if m.has_plugin:
            self.say(f"{'would fill' if self.dry else 'fill'} {plugin} (keeping {', '.join(m.keep) or 'nothing'})")
            if not self.dry:
                os.makedirs(plugin, exist_ok=True)
                for e in os.listdir(plugin):
                    if e in m.keep:
                        continue
                    p = os.path.join(plugin, e)
                    shutil.rmtree(p) if os.path.isdir(p) else os.remove(p)
                for src, name in m.plugin_files:
                    dst = os.path.join(plugin, *name.split("/"))
                    os.makedirs(os.path.dirname(dst), exist_ok=True)
                    shutil.copy2(m.path(src), dst)
                for src, name in m.plugin_dirs:
                    shutil.copytree(m.path(src), os.path.join(plugin, *name.split("/")), dirs_exist_ok=True)
        if m.magicite:
            self.say(f"{'would mirror' if self.dry else 'mirror'} {m.magicite} -> {magicite}")
            if not self.dry:
                if os.path.isdir(magicite):
                    shutil.rmtree(magicite)
                shutil.copytree(m.path(m.magicite), magicite,
                                ignore=shutil.ignore_patterns(*m.magicite_skip) if m.magicite_skip else None)
        self.say(f"PC: {m.name} {'would be ' if self.dry else ''}installed in {g.install}")

    # --- the Steam Deck ------------------------------------------------------------------------------

    def deck_script(self, full=False):
        """The bash script that pushes the PC's install of the mod to the Deck."""
        g, m = self.g, self.m
        q = shlex.quote
        host, deck = g.deck_host, g.deck_install
        pc = msys_path(g.install)
        plugin = f"BepInEx/plugins/{m.name}"
        mg = g.rel(g.magicite_dir)
        lines = [
            "set -euo pipefail",
            f"HOST={q(host)}; DECK={q(deck)}; PC={q(pc)}",
            'say() { echo "[deploy] $*"; }',
            'ssh -o ConnectTimeout=10 -o BatchMode=yes "$HOST" true 2>/dev/null || { say "Deck not reachable (asleep or off the network?): run --deck later"; exit 1; }',
            f'if ssh "$HOST" "pgrep -f {q(g.process + "[.]exe")} >/dev/null"; then say "the game is running on the Deck: close it first"; exit 1; fi',
        ]
        base = ["BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini"]
        cond = "true" if full else f'! ssh "$HOST" "[ -d {q(deck + "/BepInEx/core")} ]"'
        lines += [
            f"if {cond}; then",
            '  say "Deck: sending BepInEx (core, interop, .NET runtime, loader, Magicite)"',
            '  tar -cf - -C "$PC" --exclude=BepInEx/LogOutput.log --exclude=BepInEx/ErrorLog.log '
            '--exclude=BepInEx/plugins --exclude=BepInEx/mod-shelf ' + " ".join(base) +
            ' | ssh "$HOST" "tar -xf - -C ' + q(deck) + '"',
            '  (cd "$PC" && tar -cf - $(ls BepInEx/plugins/Magicite.dll BepInEx/plugins/Magicite.bundle BepInEx/plugins/Syldra.dll 2>/dev/null)) '
            '| ssh "$HOST" "tar -xf - -C ' + q(deck) + '"',
            "fi",
            f'ssh "$HOST" {q(f"python3 - {g.appid} {q(g.process)}")} < {q(msys_path(os.path.join(TOOLS, "deck_winhttp_override.py")))} '
            '|| say "Deck: winhttp override not set (see above)"',
        ]
        remote = ["set -e", f"cd {q(deck)}", f"mkdir -p BepInEx/mod-shelf {q(mg)}"]
        for r in m.rivals():
            rp, rs, rm = q(f"BepInEx/plugins/{r.name}"), q(f"BepInEx/mod-shelf/{r.name}"), q(f"{mg}/{r.name}")
            remote += [f"if [ -d {rp} ]; then rm -rf {rs}; mv {rp} {rs}; echo '[deploy]   {r.name} set aside'; fi",
                       f"if [ -d {rm} ]; then rm -rf {q(f'BepInEx/mod-shelf/{r.name}.magicite')}; mv {rm} {q(f'BepInEx/mod-shelf/{r.name}.magicite')}; fi"]
        remote.append(f"if [ ! -d {q(plugin)} ] && [ -d {q('BepInEx/mod-shelf/' + m.name)} ]; then mv {q('BepInEx/mod-shelf/' + m.name)} {q(plugin)}; fi")
        keep = " ".join(f"! -name {q(k)}" for k in m.keep)
        remote.append(f"[ -d {q(plugin)} ] && find {q(plugin)} -mindepth 1 -maxdepth 1 {keep} -exec rm -rf {{}} + || true")
        if m.magicite:
            remote.append(f"rm -rf {q(mg + '/' + m.name)}")
        lines.append('ssh "$HOST" bash <<\'REMOTE\'')
        lines += remote
        lines.append("REMOTE")
        parts = ["BepInEx/config"]
        if m.has_plugin:
            parts.append(plugin)
        if m.magicite:
            parts.append(f"{mg}/{m.name}")
        excludes = " ".join(f"--exclude={q(plugin + '/' + k)}" for k in m.keep)
        lines.append(f'tar -cf - -C "$PC" {excludes} {" ".join(q(p) for p in parts)} | ssh "$HOST" "tar -xf - -C {q(deck)}"')
        lines.append(f'say "Deck: {m.name} installed"')
        return "\n".join(lines) + "\n"

    def install_deck(self, full=False):
        if not self.m.has_plugin and not self.m.magicite:
            return
        installed = (os.path.isdir(os.path.join(self.g.plugins_dir, self.m.name))
                     or os.path.isdir(os.path.join(self.g.magicite_dir, self.m.name)))
        if not installed and not self.dry:
            raise DeployError(f"{self.m.name} isn't installed on the PC: deploy it there first")
        script = self.deck_script(full)
        if self.dry:
            self.say("would run on Git Bash:\n" + script)
            return
        r = subprocess.run([bash_exe(), "-s"], input=script, text=True)
        if r.returncode != 0:
            raise DeployError("the Deck push failed (see above)")


def deploy(mod_id, pc=True, deck=True, full=False, build=True, dry_run=False, out=print):
    m = mods.get(mod_id)
    if m.deploy:
        cmd = list(m.deploy) + (["--pc"] if pc and not deck else []) + (["--deck"] if deck and not pc else []) + (["--full"] if full else [])
        if cmd and cmd[0] == "bash":
            cmd[0] = bash_exe()
        out(f"[deploy] {m.name} has its own deploy: {' '.join(cmd)}")
        if dry_run:
            return 0
        return subprocess.run(cmd, cwd=PR).returncode
    d = Deployer(m, dry_run, out)
    try:
        if pc:
            if build:
                d.build()
            d.install_pc()
        if deck:
            d.install_deck(full)
    except DeployError as e:
        out(f"[deploy] {e}")
        return 1
    return 0

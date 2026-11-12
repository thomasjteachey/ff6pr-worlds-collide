# Modding kit: any Final Fantasy Pixel Remaster

The kit (PrKit, 2026-11-11) is the shared layer for mods of FF1-FF6 Pixel Remaster: the tooling
that finds and extracts a game, writes and checks its data, builds, installs, packages and tests
a mod; and the plugin library every BepInEx plugin compiles in. A new feature is one class, and a
new mod is one command. PORTING.md holds the FF6 port's history and its lessons. This file is
how to build on them.

| Layer | Where | What it gives a mod |
|---|---|---|
| Game registry | `tools/prkit/games.py` | the six PRs, found through Steam's own library list; every path (exe, `_Data`, bundles, BepInEx, Magicite, the Deck's copy) |
| Game data | `tools/prkit/data.py`, `magicite.py` | read the extracted tables/text; write a Magicite folder (changed rows only); check one loads |
| Manifest | `pr/modkit.json`, `tools/prkit/mods.py` | every mod: its game, plugin, data, build steps, conflicts |
| Build, install, ship | `tools/prkit/deploy.py`, `package.py`, `bepinex.py` | PC + Steam Deck installs, player zips, BepInEx for a new game |
| Tests | `tools/prkit/headless.py`, `plugin/PrKit/TestRun.cs`, `Harness.cs` | headless runs (no window, no sound, no saves), commands over TCP |
| Plugin library | `plugin/PrKit/*.cs` (+ `PrKit.props`) | safe hooks, switchable features, master data/text, live data, gamepad fix |
| Start a mod | `tools/prkit/scaffold.py` | plugin + example feature + data script + test + manifest entry |

Everything is behind one command: `.venv/Scripts/python tools/kit.py <command>` (from `pr/`).

## A new mod in five commands

```bash
.venv/Scripts/python tools/kit.py games                          # what's installed, BepInEx/interop/Magicite
.venv/Scripts/python tools/kit.py new-mod FiveJobs --game ff5    # plugin, feature, data script, test, manifest
.venv/Scripts/python tools/data_fivejobs.py                      # the mod's game data -> build/FiveJobs/magicite
.venv/Scripts/python tools/kit.py deploy fivejobs --pc           # build + install (the game closed)
.venv/Scripts/python tools/kit.py package fivejobs               # build/dist/FiveJobs-PR-0.1.0.zip
```

`new-mod` options: `--title`, `--id`, `--no-data` (a plugin only), `--no-plugin` (data only).
The scaffold's plugin builds as made (`PRKIT_BUILD=1` in `tests/test_prkit.py` checks that).

## A new game (FF5 and the others)

None but FF6 is installed on this PC (2026-11-11). With one installed through Steam:

1. `kit.py games`: finds it from Steam's `libraryfolders.vdf` and `appmanifest_<appid>.acf`
   (`PRKIT_FF5_DIR=...` if it's somewhere else).
2. `kit.py setup ff5`: BepInEx 6 + Magicite copied from FF6's install (the same files for every
   PR), listed in `pr/games/ff5/install-files.txt`.
3. Start the game once yourself: BepInEx generates `BepInEx/interop` (the game's code as .NET
   assemblies) on that first start. Close it.
4. `kit.py games --write-props`: `plugin/PrKit/Games.props` gets the game's folder, so every plugin
   with `<PrGame>ff5</PrGame>` builds against it.
5. `kit.py extract ff5`: its tables, text and scripts -> `pr/games/ff5/vanilla` (FF6's stay in
   `pr/vanilla`). `kit.py api ff5`: every type/field/method -> `pr/games/ff5/vanilla/Assembly-CSharp.api.txt`,
   where hook targets are found. Decompiling (Ghidra, PORTING.md "Decompiled game code") takes
   the game's `GameAssembly.dll` and `global-metadata.dat` the same way.

The first `kit.py deploy` of a mod to the Deck sends BepInEx there and sets that game's Proton
prefix to load BepInEx's `winhttp.dll` (`tools/deck_winhttp_override.py <appid> <process>`).

## Game data (Magicite)

Every PR keeps its data the same way: `master/.../Master/<table>.csv` (tables), `message/.../Message/<file>_<lang>.txt`
(text: `key<TAB>text`), map/battle scripts and other JSON in their own groups. A Magicite mod folder
holds only what it changes, plus `keys/*.json` per group naming each asset by the game's address
(`assetspath/.../AssetsPath.json`: `item` for a table, `Map_20011_2/sc_e_0021` for a map script).

```python
from prkit import data, magicite
game = data.GameData("ff5")                     # the extract
mod = magicite.MagiciteMod(OUT, game, keys_name="fivejobs")
items = mod.table("item")                       # an editable copy; only changed rows are written
items.set(1, "buy", 25)
new_id = items.add({"buy": 500}, copy_of=1)     # a new row from an existing one
mod.text()["MSG_ITEM_NAME_1"] = "Potion+"
mod.asset("battleextradata", "Assets/GameAssets/Serial/Data/BattleExtraData/battle_extra_data.json", {...})
mod.write()
problems, notes = magicite.validate(OUT, game)
```

`validate` (also `kit.py check <mod>`) flags what doesn't load or does nothing: a keys entry with no
file, a file no keys entry names, a table whose columns differ from the game's (it reads them by
position), a text line without a tab, an address that is the game's but for another file. Notes are
new assets and new row ids (often intended). BNW's and WC's folders pass with no problems.

Tables a mod usually touches (FF6 names; the other PRs share most): `character_status`,
`growth_curve`, `exp_table`, `item`/`weapon`/`armor`, `content` (names/icons for everything),
`ability`, `command`, `job_group` (who equips what), `monster`, `monster_party`, `monster_set`,
`product`/`product_group` (shops), `treasure_box`. Text: `system_en.txt` (names, descriptions),
`story_mes_en.txt` (dialogue). PORTING.md "PR data model" maps them in detail.

A plugin can apply a Magicite folder's tables and text without a restart: `LiveData.Apply(dir)`
(how Worlds Collide switches seeds). Map scripts load with their map, so those need the folder in
place before the game starts. Text written straight into the message dictionary must have real
line breaks: the files' literal `\n` is only converted by the game's loader (`MessageManager.Parse`
replaces it with `Environment.NewLine`), so `LiveData` does the same (`LiveData.Unescape`).

## Plugin features (C#)

A plugin's `.csproj` sets `<PrGame>` and imports `plugin/PrKit/PrKit.props`, which compiles the kit
in and references the game's BepInEx and interop assemblies. The kit is compiled into each plugin,
so mods never depend on one another's kit or version, and it registers no IL2CPP classes, so any
number of kit plugins load side by side.

```csharp
public override void Load()                      // Plugin.cs (the scaffold writes it)
{
    Kit.Init(Log, "Five Jobs");
    if (!Game.Require(PrGame.FF5)) return;       // built on FF5's own code
    if (!MagiciteDir.Has("FiveJobs")) { Kit.Warn("data not installed: off"); return; }
    Features = new FeatureHost(Config, new Harmony(Guid));
    Features.Add(new JobLimits());
    Features.Add(new SingleGamepad());            // the kit's Steam Deck double-press fix
    Features.ApplyAll();
    AddComponent<FiveJobsRunner>();              // its own runner class name (IL2CPP registers by name)
}

class JobLimits : Feature                        // one class per thing the mod changes
{
    public JobLimits() : base("Jobs", "JobLimits", true, "Four jobs per run.") { }
    ConfigEntry<int> count;
    public override void Bind() => count = Setting("Count", 4, "How many jobs.");
    public override void Apply(Harmony h) =>
        Hooks.Patch(h, Hooks.TypeByName("Serial.FF5.Management.JobManager"), "ChangeJob", typeof(JobLimits), prefix: nameof(ChangeJob_Prefix));
    public static bool ChangeJob_Prefix(Il2CppSystem.Object __instance) { ... }
    public override string Check() => Master.Ready ? $"{Master.All<Last.Data.Master.Job>()?.Count} jobs" : null;
}
```

(The FF5 names above are placeholders; find the real ones in `kit.py api ff5`.)

What the host does: a `[Section] Key = default` switch per feature in `BepInEx/config/<guid>.cfg`;
only enabled features are applied; a feature that throws while starting is turned off and logged,
the others still start; `Update`/`OnGUI` are called every frame with each feature's exceptions
logged once; `Check()` runs once the game's data has loaded and its line goes to the log
(`[check] JobLimits: 22 jobs`), which is how a headless run shows a feature works; `[Kit] PerfLog`
times every feature.

Rules every hook follows (from the FF6 crashes in PORTING.md "Plugin"; the engine is the same in
every PR):

- `Hooks.Patch` refuses methods whose machine code is tiny (empty, a jump, a ret within 6 bytes):
  the linker folds identical functions, so hooking one hooks many, and reading the wrong object's
  fields closes the game. Hook the method that does the work.
- Check the instance before touching it: `if (!Hooks.IsA<T>(__instance)) return;`.
- Harmony can't patch `MasterManager`'s generic methods. Read data with `Master.Get<T>(id)` /
  `Master.All<T>()`, text with `GameText.Get/Set`.
- Hooks run on several threads (stat getters do): lock what they share. Log with `Hooks.WarnOnce`
  / `NoteOnce`, never every call.
- `Last.*` types are the shared engine (the same in all six games); `Serial.FF<n>.*` is one game's
  own code. A plugin built against FF6 that touches only `Last.*` usually runs in FF5 too; for
  anything else use `Game.Require` or `Hooks.TypeByName("Serial.FF5....")`.
- UI found in a screen's first frame has stale positions (it slides in), and anything added under a
  layout group becomes a layout item: parent new UI to an existing text (PORTING.md, the EL display).

## Testing

- Offline, in seconds: `.venv/Scripts/python -m unittest discover -s tests` (each scaffolded mod gets
  a `tests/test_<id>.py` that validates its Magicite folder), `cd plugin/PrKit.Tests && dotnet run -c Release`
  (the kit's game-free logic), `kit.py check <mod>`. `tools/run_tests.ps1 -Offline` runs them all.
- In the game, headless, **only with the user's OK and never while they play**:
  `kit.py run ff5 --harness` starts the game with no window and no sound, and a kit plugin of that
  game takes the request: every save is blocked for the run (if the save hooks can't be installed,
  it quits instead), the sound is muted each frame, the harness listens on 127.0.0.1:47811. The run
  stops its own game if a window appears, if anyone else starts the game, on `--until <regex>`, or
  after `--minutes`. Talk to it with the same client as BNW's harness:
  `.venv/Scripts/python tools/game_harness.py state "press action" "wait 60"`. Built-in commands: ping,
  help, state, press, wait, skipmsg, newgame, slots, load, warp, script, battle, encount, log, quit.
  A mod adds its own: `Harness.Register("jobs", args => "...", "jobs: each character's job")`; a
  handler that returns null is called again each frame until it answers (waiting for a battle, a map).
- Never by driving the screen (no computer-use, no visible launches).

## Install and ship

`pr/modkit.json` lists every mod. `kit.py deploy <mod>` builds (the entry's `build` commands, then
the plugin, then `post_plugin_build`), installs on the PC (the game closed), then pushes to the Deck
(`--pc` / `--deck` for one side, `--full` resends BepInEx, `--dry-run` shows the plan and the Deck
script). Mods with the same `exclusive` tag for a game are never installed together: the others move
to `BepInEx/mod-shelf` (their Magicite folder beside them as `<Mod>.magicite`), and come back with
their `keep` folders (player data) when deployed again. Brave New World and Worlds Collide keep their
own `tools/deploy.sh` (its WC seed sync and bundle steps); `kit.py deploy bnw|wc` runs it.
`kit.py package <mod>` (= `tools/package_mod.py`) writes the player zip: the plugin folder, the
Magicite folder under the game's own `_Data` name, a README (BepInEx build, Magicite, the Proton
launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`, the mod's `readme_note`); never a ROM
(`.sfc`/`.smc` are refused), and never the game's own files: a file byte-identical to one in the
game's extract (`pr/vanilla`, `kit.py extract`) is refused (Magicite `keys/` lists aside). A mod
that needs copies of game files makes them on the player's machine from their own game:
`tools/pr_bundle.py` reads any PR's text assets with the standard library alone (byte-identical to
`extract.py`'s on all 2,374 of FF6's text bundles, 30 s), and modkit.json's `package_leave_out` /
`package_magicite: false` keep the made parts out of the zip. `player_setup` names the script the
plugin runs to make them; the packager refuses the mod until the plugin's DLL names it.

Worlds Collide is public (2026-11-12, the user: "just make a github release and make the entire WC
thing public"): https://github.com/thomasjteachey/ff6pr-worlds-collide. `tools/wc_publish.py`
re-exports its tree into `../ff6pr-worlds-collide` (the WC plugin, all of `tools/*.py`, prkit, the WC
tests, these docs; WC itself as the `mods/worlds-collide` submodule at the commit used here; the
same game-file check as the zip), then commit and push from there. Releases carry
`build/dist/WorldsCollide-PR.zip`; it's over the Chrome extension's 10 MB upload limit, so it goes
up in two parts that the release page's JavaScript rejoins into one File (SHA-256 checked) and hands
to its `#releases-upload` input (v0.6.0, 2026-11-12), or by hand. No GitHub CLI on this PC.

Worlds Collide's (2026-11-12): `tools/wc_gamedata.py`. Every copy of the game has the same files, so
`wc_bundle.py` works out the player's files once (`make_recipe`, ~40 s): `generator/gamedata_recipe.json`
says how to make each of the 1,633 (a copy of a game file, a game monster AI script under a slot's
name, a line patch of a game file's JSON or text with WC's changes, a duplicate, or one of ours
shipped in `generator/gamedata/`: key lists, the cache), each with its sha1, and
`generator/gamedata.json` lists the 1,584 game files that takes. `wc_gamedata.py setup` (bundled
Python, ~10 s on the PC: 5.5 extract, 4.5 make) reads them from the game's bundles, makes and checks
every file, gives the shipped cache this folder's vcache stamp, then writes `generator/vanilla/.complete`
(the manifest's id). `wc_gamedata.py status` exits 0 when that's current; `selftest` makes a player's
install from the build and compares it file by file.

## Where BNW and WC stand, and what's left

- **Brave New World is on the kit** (2026-11-11, plugin v0.22.1): `BnwPlugin.csproj` imports `PrKit.props`,
  its 236 hook calls use `Hooks.*` (the same code `EsperHooks` held), `GameHarness` is the kit's `Harness`
  plus BNW's `party`/`skipmsg`/`quit`, and `FormulaProbe` blocks saves and mutes with `TestRun`. Its log now
  starts with a `[kit]` line and says `[test] saves blocked ...` / `[test] save to slot N skipped` where it
  said `[probe] ...`. Built and unit-tested (Rules.Tests); not yet deployed or run in game.
- **Worlds Collide is next**, held for the session working on WC until it says WcPlugin is free (it edits
  `WcSeeds.cs`/`WcTest.cs` across turns, and without git overlapping edits would clobber each other). The
  change is small and agreed: `PrKit.props`, WC's `LiveData.cs`/`PadFix.cs` replaced by the kit's
  `LiveData`/`SingleGamepad` (same switch), `using PrKit;` in four files, WcTest's device check and mute
  through the kit. WC's own `h.Patch` hooks stay as they are (no tiny-code check: switching them could turn
  off a hook that works). `plugin/Shared` goes once WC no longer compiles it. The change is a script,
  `tools/prkit/migrate_wc.py` (one-off: `--check` first; it edits by exact text and writes nothing if a
  line has moved), to delete once applied.
- **Worlds Collide's player setup** (2026-11-12, `WcGameData.cs`): the first thing in `Plugin.Load`,
  when `generator/vanilla/.complete` is missing or isn't `generator/gamedata.json`'s `"id"`, runs
  `wc_gamedata.py setup` with the bundled Python (42 s on the PC, in the console before the game
  window), then `WcMode` needs its success. BepInEx loads WC before Magicite (independent plugins
  go by GUID), so Magicite reads the folder made on that same start: no restart (headless check:
  setup, startup apply, 534 of 534 Magicite files the seed's). A deploy's install is already set up
  (`wc_bundle.py` writes `.complete` into the build deploy mirrors). The packager checks the DLL
  names `wc_gamedata.py`.
- The kit hasn't run in a game yet: the first headless run of a kit plugin (or BNW's next probe run)
  should show `[kit]`, `[check]` and `[test] saves blocked` lines.
- FF1-FF5 paths are Steam's names and app ids, not yet seen on this PC.
- The SNES-to-PR porter (`tools/port*.py`, `ff6snes.py`, `idmap.py`) reads FF6's ROM layout; a port of an
  FF5 SNES mod needs FF5's layout first (the converters' approach carries over, the tables don't).

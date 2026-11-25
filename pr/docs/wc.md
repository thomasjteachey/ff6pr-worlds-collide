# Worlds Collide on the Pixel Remaster

Owner: the Cross-mod coordinator. Moved verbatim from `pr/PORTING.md` on 2026-11-20 (roadmap item 5); `git log --follow` / the split commit trace any paragraph. Status rows stay in `pr/PORTING.md`.

## Live progress (newest first)

- 2026-11-25 (coordinator local_7324b381): **0.6.13 released (GitHub v0.6.13 Latest; Nexus file 622 main + primary, mod version 0.6.13), all without a browser.** Enemies' battle
  lines could freeze a fight: 0.6.12's generator wrote the AI shim's ExecMsg row with one integer (port_ai pad=False),
  and Message.Msg reads two, so ViewMes threw every frame (BNW's Vargas stall). Kit 892128a pads new rows and
  SnesAiHost.ViewMes_Prefix repairs old seeds' rows. WC tests never saw it: WcEvents.Msg_Prefix answers every Msg in a
  test run; the new request option msgs=1 lets battle lines run the game's own Msg. Gate wc-13 (build/batches/wc-13.py)
  10/10 PASS, test_wc OK, wc_gamedata selftest, Wc.Tests 0 failures. Source 12f9af1, kit 32d767f; zip sha256
  aee477b39085ff9deabc29db5cccedcab8cffa530d4f87d27f8ee893ec3dca5f; plugin md5 92035905 on the PC and the Deck's shelf.
  Releases now go out with gh (installed for the user) and the WC repo's publish-nexus.yml (wc_publish.py NEXUS_WORKFLOW):
  a published release uploads its zip to Nexus with the repo secret NEXUSMODS_API_KEY (sealed over from the Ivalice repo's
  secret by a one-off workflow; nobody saw it). The file must be uploaded with primary_mod_manager_download true: the
  first 0.6.13 upload (false) stayed non-primary and the mod version didn't move, so it was uploaded again (621 archived).
  Later that day (BNW 0.26.7's run): v3's changelog endpoint wants the mod's uid (game 4335 << 32 | 165), not 165; the
  workflow passes the uid now (WC repo 5fb8f0a) and has a changelog_only manual run, which posted 0.6.13's changelog.

## From the Status section

### WC without an SNES ROM (the user's goal, 2026-11-06: our own randomizer at parity with WC)

Until 2026-11-06 the in-game generator ran WC on the vanilla US ROM, which the bundle carried
(not distributable). Findings:
- WC reads 437,822 bytes of its input ROM (170 regions): data tables (characters, items,
  espers, spells, shops, chests, enemies, formations, coliseum), but also dialogue (134 KB),
  character sprites (126 KB), maps/NPCs/exits, enemy AI scripts, names and palettes.
- It needs all of them: with graphics, dialogue, maps or enemy AI blanked it stops (it decodes
  sprite sheets, dialogue, compressed tilemaps and AI scripts to rewrite them), and it only
  accepts the exact vanilla ROM (a SHA-256 check).
- So "ROM-free" means running WC's randomizing logic on data from the PR's own tables, with
  the ROM-only rewriting (sprites, dialogue, maps, AI text) stubbed out while every random
  draw stays in WC's order, so a seed still matches the website's. `test_wc_xref.py` is the
  yardstick: the same seed through WC on the SNES (testing, with a ROM) and through ours.

Started 2026-11-06:
- `tools/wc_trace.py` records a WC run: every random draw (9,000-10,000 per seed, all through
  Python's random) with the line that made it, and every input byte read, by WC module;
  `--diff` finds the first draw two runs disagree on. `tools/wc_outputs.py` compares the tables
  our port reads and wc_run.py's dumps.
- `tools/wc_romfree.py` (wc_run.py with WC_ROMFREE=1): stand-ins for what WC only rewrites
  (dialogue, maps' NPCs/events/exits, enemy AI scripts, party battle scripts, font widths,
  animation flashes, menu sprites; the SHA-256 check off). WC's AI changes draw once (Rizopas'
  timer): the stand-in makes that draw.
- Result: WC run on a ROM that is zeros except 38,966 bytes of numeric data tables (characters,
  items, spells and skills, espers, enemies and their items, formations, packs, zones, chests,
  shops, rages/sketches/controls, the Coliseum, natural magic, metamorph sets, map properties'
  random-battle bits) makes the same draws, the same tables and the same event choices as on
  the real ROM: 4 of 4 flag sets (both presets, 3 seeds, and open world + random bosses +
  permadeath + NPC tips + original names + custom sprites). No graphics, text, maps, AI, code
  or music needed.
- Done (the user chose to ship the data, 2026-11-06): the generator ships `data/wc_base.bin`
  (`tools/wc_basedata.py`: those tables, plus the names WC and the porter use and the enemy AI
  scripts, so WC's AI changes run as they are and port_ai compiles them as before; 94,656 bytes,
  44 KB compressed, made from the ROM on the dev PC). `tools/wc_seed.py` runs ROM-free wherever
  there's no ROM (FF6_WC_ROMFREE=1/0 forces it): WC's input from that file in memory, its
  output compressed in the work folder (`ff6snes.rom_bytes`), never an .sfc; the porter's
  "vanilla" side is the same input. `tools/wc_parity.py` makes a seed both ways and compares the
  folders: the same for every configuration tried. The bundle has no ROM (it refuses to build
  with one).
- Why it holds: WC only draws from Python's random, and none of the stood-in content changes a
  draw or a table the porter reads; WC's code is untouched, so a website seed is our seed. A
  new WC version needs `wc_basedata.py regions` (fresh traces) and `build`, then the parity
  and cross-reference tests.

### WC without the game's files (2026-11-12, the user asked how to distribute WC)

The player zip carried 1,804 of the Pixel Remaster's own files, byte for byte: `generator/vanilla`
(1,345: master tables, messages, monster AI, map entities), `baseline/files` and
`baseline/vanilla_data` (vanilla copies of what a seed changes) and the Magicite folder's vanilla
copies. Same rule as the ROM: they're made on the player's machine from their own game now.
- `tools/pr_bundle.py`: the PR's UnityFS bundles (LZ4/LZMA blocks, SerializedFile v21, AssetBundle
  m_Container, TextAsset) with the standard library alone, because the bundled Python has no
  UnityPy. Byte-identical to `extract.py` (UnityPy) on all 2,374 text bundles (8,173 files, 30 s;
  WC's 273 in 4 s).
- `tools/wc_gamedata.py`: first (morning) `setup` ran the porter on the player's machine: 3,009 game
  files extracted, then the baseline and the cache rebuilt in fresh processes, 35-42 s. Then (the
  user: "all copies of the game have the same game data", "sure, do that") a recipe made once by
  `wc_bundle.py` (`make_recipe`): each of the 1,633 made files is a copy (468), a game monster AI
  script renamed for its slot (384), a line patch of a game file's canonical JSON (one value a line,
  re-saved compact) or text (243 + 25), a duplicate of another (385), or ours, shipped (128: key
  lists, wc_baseline.json, the cache, which setup re-stamps for the player's folder). Setup reads
  1,584 game files and makes everything in memory: 10 s with the bundled Python (5.5 extract, 4.5
  make), every file checked against its sha1; 18 s inside a busy PC's first start. `vcache.stamp`
  now skips dot-files (`.complete` in the vanilla folder had made every cache stale). The recipe is
  4.2 MB (its patches carry WC's rewritten scripts, which the old zip shipped whole).
  `selftest` (this PC: a build without its game files, set up from the game, compared file by
  file), `status`.
- Checked: setup's output = the dev build's for every file (generator/vanilla 1,345, cache 3,
  baseline 643, Magicite folder 959), and a seed made on the set-up install (bundlecheck, Moogle
  Cup) = the project generator's (wc_parity).
- The packager refuses any file byte-identical to one in pr/vanilla (BNW's zip: none but two
  Magicite key lists, which are asset names and exempt), leaves WC's made parts out (modkit.json
  `package_leave_out`, `package_magicite: false`: 12 MB instead of 18), and refuses WC until the
  plugin runs the setup (`player_setup`). The plugin does: `WcGameData.EnsureSetUp` first thing in
  `Plugin.Load` (WC loads before Magicite, so no restart), checked headless from a removed
  `.complete` (recipe setup: 18 s in Load, startup apply ok; the next starts skip it, 0 failures,
  534/534 Magicite files).

### WC startup time (2026-11-12, the user: "the startup is incredibly slow", "the command prompt before the game is the thing taking forever")

Measured headless on the PC by timestamping the BepInEx log as it's written (a watcher; the log
has no times). Before: 15.0 s of console (BepInEx preloader 2.4, Il2CppInterop 2.8, WC's Load 6.6,
Syldra 0.6, Magicite 2.5) and 32.5 s to the title, 12.8 s of it the opening logos after the data
had loaded.
- Logos (`WcBoot.cs`): SceneSplash (decompiled: `tools/ghidra` patterns for SceneSplash and
  Last.UI.SplashController) goes to the title when Update sees loadFinished, messageIsReady and
  animFinished; OnSplashAnimationFinished sets animFinished and requests the title scene, called by
  the logos' controller after the last logo. A press can't end them early (each logo's own isSkip,
  and SplashController.IsInput is inlined into the logo loop, so hooking it does nothing). A
  SceneSplash.Update prefix calls OnSplashAnimationFinished once the game has loaded; a prefix on it
  drops the logos' own later call. **Removed the same day** (the user: "remove the skip logo stuff, it
  doesn't really do anything"): the logos play again; don't bring the skip back.
- WC's Load (`[wc] load:` line, per-step ms): 6.06 s warm, of which the screen's setup 1.25 s (its
  first touch loads Unity's GUI types), the first hook 0.7 s (Harmony's IL2CPP setup), seed hooks
  0.75, registering WcRunner 0.6, test request 0.6, the gameplay hooks ~15-130 ms per group. The
  screen's setup and every gameplay hook group now run one a frame from WcRunner's first Update
  (`[wc] first frames:` line, 2.2 s over 24 frames); Update and OnGUI wait for them. Load keeps
  config, game data, flags, the logo skip, input, seeds (Init + save hooks), tests and the runner:
  2.96 s.
- Result, warm PC start: console 15.0 -> 10.3 s, title 32.5 -> 19.5 s (with the logo skip, since
  removed: the console's 10.3 s is the deferral's alone). The first start after a
  deploy re-copies the seed's 534 Magicite files (1-4 s more), players don't redeploy. Left:
  BepInEx and Il2CppInterop (~4 s), Magicite and Syldra (~3.4 s), Harmony's setup and WcRunner's
  registration (~1.3 s). The Deck isn't measured yet.

### Deck startup (2026-11-18, the user: "it just took a REALLY long time to load", after Battle Options)

Measured on the Deck (Proton 11, Wine 11) by timestamping the BepInEx log as it's written
(a python follower over ssh, plus per-thread CPU from /proc), WC + Syldra + Battle Options + Magicite:
first frames ~123 s after the process starts. BepInEx to "Chainloader initialized" 8 s, then 24 s
before the first plugin (Il2CppInterop's delegate type registration, plugin scan), WC's Load 20 s,
Syldra ~6 s, Battle Options 0.1.0 27 s, Magicite 32 s.
- Battle Options' 20 s was `AccessTools.TypeByName`: its first call loads every type of every
  interop assembly (0.45 s on the PC). The kit's `Hooks.TypeByName` now asks each assembly by name
  (Assembly.GetType) and Battle Options uses it: its hooks take 0.1 s, its Load 6.9 s (first
  frames ~105 s). Don't use AccessTools.TypeByName in a plugin.
- What remains comes in steps of ~6.6 s at a few kinds of operation: each IL2CPP class
  registration (every mod's runner: WC 6.5 s, Battle Options 6.7, Syldra ~6, Magicite's two types
  ~20), the first Harmony patches of a plugin (WC "input", "seed hooks"), Magicite's plugin
  instantiation. The PC pays ~0.6 s for each. The main thread runs flat out through them,
  alternating with wineserver at ~50% (server round trips), so it isn't waiting. It isn't the .NET
  GC (a probe logged every collection, 0-9 ms pauses) nor the IL2CPP GC (turned off: no change).
- Found (perf on the Deck's main thread: Wine's lookup_unix_name, NtQueryDirectoryFile,
  hash_short_file_name): each step is BepInEx's resolver listing every folder under BepInEx/plugins
  after a failed assembly lookup (Il2CppInterop's Il2CppSystem.Private.CoreLib at every class
  registration). WC 0.6.4 moved its data to BepInEx/WorldsCollide: the steps went to milliseconds.
- Screenshots on the Deck: `gamescopectl screenshot <path>` captures the screen over ssh. A
  Battle Options test run's harness New Game left the field and battles rendering black there
  (the intro rendered; the fade overlay hidden didn't help); in-game shots need a real save.
- Screenshots on the PC, no window: a -Graphics probe run (or `kit.py run <game> --harness
  --graphics`) and the harness's `shot <absolute png> [width [height]] [layers]`. Batch mode draws
  no camera by itself, so shot draws the frame from the game's cameras (2026-11-20 row).

## WC worklist: everything but playtesting (2026-11-05, from a flag-by-flag review)

The user asked for everything except testing to be completed. A review of every WC option
(three research passes over WC's source, the porter and the PR's code) gave this list. Tick
items as they land; P0 first (they affect every or most seeds).

**P0: bugs / every seed**
- [x] Esper learning (port_spells LearningPorter): SNES learn rate as-is, all 5 slots, more rows when needed (was BNW's 100/cost rule, 3 slots)
- [x] Natural magic learners (-nm1/-nm2): WC moves them in assembly (JSR C0/A182); the rows go to the learner's job
- [x] Ability scaling (-ase/-asr, on in standard flags; 2026-11-11: picked when the monster acts, WC's formula and odds, WcAbilityScaling + ability rows 7600-7635; was one spell per script from the unscaled level). Left: the special-attack tiers (0x5E-0x60), bosses that keep the PR's script
- [x] Skills follow commands (Random Unique commands; WcSkills, -sel/-bel rows; headless ok): SwdTech/Blitz/Lore/Dance/Rage holders get learning rows + known skills copied; WcObjectives.Learn by command holder
- [x] Boss / dragon / statue shuffles (WcBattles: Encount/EncountBoss swap, headless ok) (-bbs/-bbr/-drloc/-bmbd/-stloc/-srp3; -fer event packs; -cms): remap by party id at Encount/EncountBoss (External.Misc.CallEncount), WC's replacements exported per seed
- [x] Starting equipment (port_characters, BNW too): PR slot 3 = body, 4 = head; empty = 93/198/199/200, not 0
- [x] -etr randomizes event timers (seconds from WC's ROM), -etn removes them (now -etr removes)
- [x] Chests: EMPTY (-cce), items in vanilla-empty chests, re-bitted chests (now matched by place), -cms monster chests (their battle scripts per seed). Left: -chrm (an item chest can't start a battle without a script), ~50 SNES chests the PR merged into one
- [x] Shops: identical-stock shops pair by their town's neighbours, PR copies (WoB/WoR) take the stock too, -ssf sell fraction
- [x] Morph: WC's double duration in the WoB (TranceConditionFunction rate 0.5 -> 1)
- [x] -escr (monster_party.not_escape from enemies' no_run), -bnu (undead: monster.species bit 0 and the Undead flag 2 in monster_flag_group_id). 2026-11-12: a battle's undead is the flag group, not the species bit (EnemyCharacterParameter's constructor sets CharacterParameterBase.undead from the flags; species only feeds killer weapons: tools/decomp/undead). With species alone, -bnu's bosses (second Behemoth King, Phantom Train, Hidon, Erebus) stayed undead in battle, and the Assassin's Dagger (ASSASIN_WEAPON: HitCalcAssasinWeapon sets an undead target's HP to max on every unresisted hit, no 25% roll) made the second Behemoth King unkillable. port.flag_group_with (2 <-> 0, 8 <-> 4) for WC and BNW (29 BNW monsters change undead); older seeds: wc_seed_upgrade.py rewrites the cell

**P1: options**
- [x] Objective quests: 15 of 15 tracked (battle ones by the event's own party, so WC's swaps still count; Coliseum, auction, Suplex, Cid x2, Injured Lad, checkpoint and Zozo clock hooks; Perform In Opera by the performance's end, wc_events.opera_performance)
- [x] Objective results: Learn Rages, Magitek Upgrade, Max Morph (MagicLearningValue 255), Auto Dog Block/Life 3/Seizure/Overcast (headless ok)
- [x] -sal (joiners at the average level: growth-curve HP/MP, exp, level skills), -stl (start level, port), -eu (Umaro's Equip menu), -sn (port), -fst (Bushido ability_wait / 4), -brl (Duncan's scene waits), -slr/-srr/-sdr (start skills to the command holders), -rnl (rages after every win, no Leap), -dns (no stumble odds), -dda (dance help lists its steps). Left: -llr (level lores' levels)
- [ ] -scis (Sketch/Control always hit, controller stats), -scia (Sketch/Control abilities: tables): the PR's hit roll is battle code not located yet
- [x] -nxppd (EXP not split), -gpm in the wallet, -mpm in the esper learning (WcRewards, 2026-11-21). Left: steal gaps (Thief Glove, empty rare)
- [x] -bmkl (the Marshal's minions: WC's formation 4 ported into parties 5/596 unless -bmkl), -dgne (ports itself: WC's no-run bit and AI)
- [x] Espers: -ebs/-ebr bonuses (WcEspers: MagicStoneData lookups answer as a vanilla esper with that bonus; Speed+2 / HP+100% scaled), -ems multi-summon. Left: -eer/-eebr restrictions
- [ ] Items: done -stra and WC's item bits (Runic/Bushido weapon lists per seed). Left: -csb (the PR counts in code), -saw divisor (the PR's Atma formula is code; WC's tier move is WC's own), item-taught spells (learning type 4), metamorph items, -ccrs scaled chests, -chrm random monster chests
- [x] Auction -ari (the four normal sales give and announce the seed's items; they also count for Win An Auction), -anca (the random pick draws from one fewer and its last value becomes the next sale: the Talkobo's and airship's share spread over the rest, as WC's re-roll, with no new loop)
- [x] -scan (monster flag 1 answers no, Libra 0 MP), flash removal (-frw/-frm: script flashes kept dark; battle animations are the PR's own art), -crm and -ahtc are the PR's own (rewards with ??? for hidden ones; heals in their own color). Left: -ond, -fbs (untested whether the PR's Kefka's Tower allows the skip)
- [x] Character sprites / portraits (-cspr/-cpor): character_asset per seed (battle, field, portrait) for WC ids with PR art (0-13, Soldier, Leo, Banon, Ghost, Kefka); WC's community sprites have no PR art
- [x] Permadeath (-pd): inns/tents/story heals skip fallen characters (FullRecoveryHpAndMp / RemoveAllCondititons / CanRecovery); WC's data (no Life spells, no Fenix Downs) ports itself
- [x] Cross-machine saves: deploy --deck syncs seeds and save-slot records both ways (tools/wc_sync_seeds.py; Steam Cloud carries only the saves)

**P2: large / low value**
- [x] -rc (Zozo clock: WC's time and clues, 2026-11-06)
- [x] -npctips, Y NPC (-y*): left out by choice (cosmetic; off the options screen, 2026-11-06)
- N/A in the PR (no work): -cpal, -cspp (SNES palettes), -wmhc (PR minimap), -emi (PR has it), -rr (deprecated), SNES bug fixes -fe/-fvd/-fs/-fedc/-fr/-fj/-fc (PR's own engine)

## Worlds Collide port design (user, 2026-11-05)

The user wants WC "with options WITHIN THE GAME that let you change seed stuff as if you
were on the website", and new seeds **without restarts** (a restart-to-apply design was
rejected). Beyond Chaos and T-Edition are dropped.

- Source: `mods/worlds-collide/` (WC 1.4.3, Python, 435 files / ~50k lines, flags parsed by
  `args/`, flag metadata for a picker via `build-wc-flag-metadata.py`, presets in
  `mods/worlds-collide-presets.json`). WC turns vanilla ROM + flags + seed into a ROM.
- Generation in game: an options screen in the plugin (flag groups from WC's metadata,
  seed field, presets) runs the bundled WC generator and our porter through an embedded
  Python (Windows embeddable runtime; under Proton on the Deck it runs as a Windows
  process too) on the user's vanilla ROM, giving the same PR data our porter writes to
  `mods/WorldsCollide` (master CSV rows, messages, maps, AI scripts).
- Applying it live, no restart: master tables are `MasterManager.GetList<T>()`
  dictionaries; each changed row is rebuilt with the master type's `(string masterLine)`
  constructor (columns in the game's order, as our CSVs are) and put in place. Messages go
  into the MessageManager tables; map entity changes (chests) and AI scripts are swapped
  where they're loaded (hooks on map / monster AI loading). The seed and flags are stored
  with the save (plugin save data), so loading a save re-applies its seed.
- Done (2026-11-05): `tools/wc_seed.py --seed S --flags F [--preset P] --out DIR` runs
  WC's own wc.py (fresh Python process, seeds match the website; needs the US v1.0 ROM,
  `rom/vanilla/Final Fantasy III (USA).sfc` = WC's sha256) and our porter: **~4 s** for a
  seed (2188 rows in 16 tables + 401 map/AI files for Moogle Cup). `LiveData.cs` applies such
  a folder to the running game: every master row rebuilt with the type's own
  `CreateMaster(line)` (MasterBase.LineParse, positional columns) and set in
  `MasterManager.GetList<T>()[id]`; messages via MessageManager (headless test: 2188 rows
  applied, monster 1 HP 50 -> 40, item 1 price 500 -> 54, Terra Vigor 30 -> 33, no
  restart). `MasterBase.Parse<T>(csv)` returned nothing for our CSVs (unused).
  Magicite (strings in Magicite.dll: TextAsset_bytesPatch, ResourceManager_CheckCompleteAsset,
  ReadAllText) serves mod files when the game loads each asset, so per-seed map/AI files
  can be overwritten in the active Magicite folder (files must exist at startup).
- Done (2026-11-05, WcPlugin v0.1.0): the in-game screen, generation and live apply, see
  "Worlds Collide in game" below. Headless tests pass (two seeds made in game in 4-9 s and
  applied live, rows and files checked, vanilla restored, a save loaded through the hook).
- What data can't carry: WC's open world, objectives and recruitment are event-script
  rewrites (WC `event/`): they need PR event-script edits per map (PR events are
  per-map JSON scripts) plus plugin logic (objective tracking, unlock conditions).
  That is the bulk of the WC port.

## Worlds Collide in game (pr/plugin/WcPlugin, 2026-11-05)

How a player uses it: the title screen's menu has an entry "Mods" ("Worlds Collide" in WC
mode), between Extras and Options (F7 anywhere also opens it) -> Mod: Worlds Collide ->
restart once. (2026-11-05: the first version used a note in the screen's corner and the
Select button; the user found the note jarring, and Select isn't bound on the Deck.) Then on the title
screen: Seed page = preset, seed (type it, or left/right for a random one), the flag string
(edit as text, paste from the website with Ctrl+V or "Paste flags from clipboard"),
"Generate and play this seed"; pages 2-30 = every WC flag by WC's own sections, dropdowns
as on the website (L/R to change page). A few seconds later the seed is live: New Game.
Saves remember their seed; loading one brings its seed back. Earlier seeds are listed and
can be switched to at the title screen; "Play without a seed" = the game's own data.

- Modes (`plugin/Shared/ModMode.cs`): Magicite loads every folder in its import directory
  (`silvris.magicite.cfg` "Import Directory"), so each mode has its own: BNW =
  `StreamingAssets/Magicite`, WC = `StreamingAssets/MagiciteWC`, vanilla =
  `StreamingAssets/MagiciteVanilla`. Switching rewrites that line (takes effect at the next
  start). The BNW plugin turns itself off unless `BraveNewWorld` is in the import directory
  (the formula probe still runs in any mode).
- Flags (`tools/wc_flags_export.py` -> `plugin/WcPlugin/wc_flags.json`): read from WC's own
  argparse (sections, types, ranges, choices, nargs, help), argparse's mutually exclusive
  groups as dropdown rows (WC's own metadata script drops them), labels from long option
  names (+ a few hand names). 253 flags, 29 sections, 28 dropdowns. Model: `WcFlags.cs`
  (no Unity): parse/serialize flag strings, steps, defaults; unknown tokens kept as typed.
  `plugin/Wc.Tests` (dotnet run) + `tools/wc_flags_check.py`: presets round-trip identically
  and every flag the screen can turn on is accepted by WC's parser (442 strings, 0 problems).
- Generation (`WcGenerator.cs` -> `tools/wc_seed.py`, background process, no window): WC
  (fresh Python, its own code) on the vanilla ROM, then our porter; the seed folder
  (`plugins/WorldsCollide/seeds/<date>-<seed>/`) holds FULL tables for every table the
  porter can write (`wc_live.MANAGED_TABLES`, 20) and the full system_en text, so applying a
  seed also undoes the previous one, plus the changed chest maps / AI scripts (`files/`),
  WC's log (`wc_log.txt`: flags, spoilers with -sl) and `wc_seed.json`. ~4 MB, ~4 s.
- Bundle (`tools/wc_bundle.py`, deploy.sh runs it): `generator/` = CPython 3.12 copied from
  this PC's install (26 MB; no download), WC 1.4.3, the porter, the PR data it reads (20 MB:
  master, message, monster_ai, battle_script, battleextradata, assetspath, every map's
  entity_default.json; traced with an audit hook), the ROM, BNW's font table
  (`data/vwf.tbl`; `bnw_text` also falls back to stored table counts without BNW's asm).
  `--check` makes a seed with the bundle alone. Same Windows Python runs under Proton.
- Live apply (`WcSeeds.cs`, `Shared/LiveData.cs`): rows via each master type's
  `CreateMaster(line)` into `MasterManager.GetList<T>()`, skipping rows already set to the
  same line (first seed 15,638 rows in 0.17 s; switching seeds ~1,600 rows in 0.03 s); text
  into MessageManager, with each literal `\n` turned into `Environment.NewLine` first, as the
  game's own `MessageManager.Parse` does (seen in its disassembly; before 2026-11-12 the live
  apply skipped it and a seed showed "\n" in menu texts such as MSG_SYSTEM_183). Files: Magicite only serves files that existed at startup, so the
  WC folder ships every file a seed can change (`wc_live.write_baseline`: 156 chest maps +
  the 384 `sc_ai_bnw_NNN` AI slots, 3.8 MB) and a seed overwrites contents; the previous
  seed's files go back to `baseline/files`. Cached assets for swapped files are dropped from
  `ResourceManager.completeAssetDic`. At startup the folder is synced to the active seed
  (deploy may have mirrored the baseline over it) and its rows/text applied once data loads.
- Starting gil/items/espers (2026-11-05): `wc_seed.py` reads them from the WC ROM's start
  routines (event code called from 0xC9A4F: 84 add GP, 80 / B0 n 80 B1 add items, 86 add
  esper), as PR content ids in `wc_seed.json` "start"; the plugin grants them once on a New
  Game (leaving the title without loading a save, then the first map: WcSeeds.GrantStart).
  Headless test: granted to a loaded save in memory, gil +5000 and the 10 items.
- Reward multipliers (`WcRewards.cs`): -xpm scales EXP in BattleResultProvider.Genelate (prefix:
  Genelate pays it, each sharer's AdditionalExp), -gpm the gil in BattleAddExpPlug.GetGil (postfix,
  after WcScaling's -xg one), from the active seed's flags. 2026-11-21: -gpm used to multiply
  Genelate's gilValue too, which is only the result screen's number: BattleController.SaveGil pays
  the wallet from its own GetGil call (tools/decomp/bnw_batchc; callers.py: GetGil's only callers are
  SaveGil and StartWinResult), so the screen showed x5 and the wallet got x1. Now both calls get the
  multiplied amount, once. In-game check: check=reward_wallet. -mpm (2026-11-21): its old hook
  (BattleAddExpPlug.GetAp) had no callers and is gone. The battle's magic points are
  MonsterParty.GetValue, put by StartWinResult in BattleSkillUpInformation.MagicPoint and read by
  Genelate twice: at its start for the result's GetMp (the screen's number, after the PR's Boost),
  which it adds to each sharer's MagicLearningValue (the morph gauge), and in each sharer's
  StatusUpProvider.Execution (interface call; decompiled 2026-11-21), which adds learn rate (Learning
  type 2's value2) x MagicPoint to each spell of the equipped esper (SkillLevel, to 100). WC
  (battle/multipliers.py): magic points x -mpm, at most 255; the morph gauge gets that / -mpm. So
  WcRewards sets MagicPoint to the morph's amount for Genelate's own read (Genelate prefix), to the
  multiplied amount while each Execution runs (prefix; the postfix puts it back), restores it after
  Genelate and writes the multiplied amount (after Boost) into the result's GetMp. -mpm 0: no magic
  points, the morph gauge as without the flag. In-game check: check=reward_mp (moogle cup has -mpm 5).
  -nxppd (2026-11-06): the total times the characters it is split between.
- -eu (`WcRecruits.cs`, 2026-11-21): the PR's lock, CharacterInfomationData.IsChangeEquipmentOnlyAccessory
  ("id == 14"), shares its 7-byte machine code (RVA 0x4421B0) with three lambdas
  "v == BattleFlagType.NoARBattle" (ArBattleTopController, both LibraryInfoControllers), so its old
  Harmony postfix also answered those. Every game caller reaches it through the class's vtable
  (CharacterInfomationProvider: jmp [rax+0x1F0]), so WC points that one entry at its own function
  (as BNW's MenuRules), reading -eu per call; the shared code stays the game's. The old
  CanOpenRestrictionMenuWindow / GetDisplayRestrictionCharaIdList hooks are gone: Equipment's
  restriction list is the guests 15-32, Umaro was never in it. In-game check: check=eu_equip.
- Steal flags (`WcSteal.cs`, WC data/steal.py): -sch Level + 90 - target Level vs
  random(0..99) (fails at <= 0, sure at 128+), rare 3/8; -sca always steals, rare 3/8 when
  both. Thief Glove doubling not yet.
- Enemy scaling (`WcScaling.cs`, WC battle/scaling*.py): once a battle's monsters are up,
  level = count x F (time: minutes / F) clamped to [3, -msl], + random(-1..2) per monster;
  HP/MP = (value / master level) x HP/MP scale level; EXP/gil the same in
  BattleAddExpPlug.GetExp/GetGil. Counts: party average / highest level, characters (main
  14 owned) + espers owned, play time; dragons/bosses defeated and WC checks aren't tracked
  (0) yet. Dragons and final battles skip scaling unless -sed/-sfb (by formation, or by
  their monsters: BattlePlugManager.MonsterParty can stay null). Headless battle: Leaf
  Bunny Lv 5 / 33 HP -> Lv 3 / 18 HP under Moogle Cup's -lsced/-hmce 2.
- Saves: postfix on `SaveSlotManager.Save(int, ...)` records slot -> seed
  (`seeds/state.json`); prefix on `GotoLoadSaveData(SaveSlotData)` re-applies that slot's
  seed before the load (works, headless test). Saves made in other modes drop the mapping.
- Title menu entry (`WcTitleMenu.cs`, decompiled `tools/decomp/title`): KeyInput
  TitleMenuCommandController.Initialize(contents) puts ids < 6 in the main menu (6-11 Extra,
  12-15 Options, rest Config); picking one calls TitleWindowController.SetCommand(id, ...),
  which ignores unknown ids. We add TitleCommandContentData(id -1) before Option, set its
  label after the menu is built (its Name isn't found by the menu's text lookup), and
  SetCommand(-1) opens the screen; the Confirm press that picked it is ignored by the screen.
  Headless test: entry present, label set, choosing it opens the screen.
  2026-11-05 fix (user: "overlapping ... or pushing the menu down"): the main menu is a
  VerticalLayoutGroup (spacing 12, centred) in a fixed 313-px box sized for the game's 5 rows
  of 53; a 6th made it 378 px, ~32 px past both edges. With ours present the gaps shrink to fit
  the same box (6 rows: spacing 0, 318 px). `-WcTest "menu=1"` runs only the menu checks and
  logs the layout ("[layout]" lines; the menu is switched on just for the layout pass).
- Screen (`WcScreen.cs`): IMGUI, but this build stripped GUILayout's fields and GUI.TextField
  / Toggle (GUI.Box/Label/Button, Event, GUIStyle colours/sizes, systemCopyBuffer survive), so
  rows, cursor and text entry are our own. Input through the game's `InputListener` (its key
  config), which is switched off for the game while the screen is open (prefix on
  GetKey/GetKeyDown/GetKeyUp/AnyKey*; the screen reads with a bypass); text typed through IMGUI
  key events (the game uses the new Input System, so no legacy Input). 2026-11-05 (user: not
  navigable with a controller on the Deck): pads reach the game through Input System actions
  (KeyConfig.SetGamePadKey / InputSystemManager), and opening the screen switched the whole
  EventSystem off; now only the title canvases' GraphicRaycasters are paused (no clicks through)
  and the screen also reads `UnityEngine.InputSystem.Gamepad.current` directly (D-pad/left
  stick, A south = confirm, B east = cancel, LB/RB pages). Look: the game's own font (from its
  UI Text), FF6-style windows (blue gradient, light rounded border, 9-sliced generated
  texture), drop-shadowed text. Not seen on a screen yet (headless can't draw).
  Page list (user: "how am i supposed to navigate the left bar on controller?"): the cursor can
  sit on the page list: B from the rows (or Left on a row without a value) goes there, Up/Down
  picks a page, A/Right goes into it, B there closes; L1/R1 still step pages. Headless flow
  test (WcScreen.NavTest): "B:list p0 Down:list p1 A:rows p1 RB:rows p2 B:list p2 B:closed".
  The direct pad read switches itself off once the game's own check reports a pad press (both
  seeing one press on different frames would count it twice).
- One gamepad (`PadFix.cs`, every mod mode, config [Input] SingleGamepad; user, 2026-11-05:
  double presses on the controller in the Pixel Remaster itself, on the Deck): the game reads
  pads through Input System actions bound to any gamepad, and under Proton one controller can be
  two devices (Steam Input's XInput pad + the raw HID one). Every input device is logged ("[pad]
  input devices"); with 2+ gamepads one is kept (XInput layout first) and the rest disabled.
  Headless has no devices. Deck log (2026-11-05): ONE gamepad ("Xbox Controller",
  XInputControllerWindows, iface XInput) + Keyboard + Mouse (RawInput): not two devices.
  The game's pad layer (decompiled `tools/decomp/inputsys`, InputSystemManager.Update): per
  action a state 0 idle -> 1 down (value > 0.5) -> 2 held -> 3 up -> 0, stepped once a frame;
  GetKeyDown = state 1 (buttons or axes); only a Switch Pro processor, Xbox pads use the action
  fallback. Settings: Input System ProcessEventsInDynamicUpdate, XInput polled at 60 Hz, target
  60 fps, no vsync. Next: `InputDiag.cs` ("[press] f<frame>" lines, config [Input] LogPresses,
  600 lines a session) logs per frame the raw pad changes, keyboard keys, the game's action
  states and every InputListener.GetKeyDown that answered true, to see which layer adds the
  second press. Outcome (user: "the double press is gone" with the build that turned the direct
  pad read off once the game's own check sees the pad): the doubles were ours. WcRunner.Update
  can run before the game's InputSystemManager.Update in a frame, so the direct read
  (wasPressedThisFrame) saw a press one frame and the game's state machine the next: two steps
  per press on the WC screen. Now also a per-key debounce (a second down within 3 frames is
  the same press), which covers the first press before the switch-off. The press log stays in,
  off by default (LogPresses = false, PC and Deck configs). Laid out in virtual
  1280x720, drawn at the screen's size (rects and font sizes scaled, not GUI.matrix: crisp
  text at 1080p/4K). Generate/switch seeds only on the title screen. Each WC page says
  whether the PR port carries it (`wc_flags_export.PORT_STATUS`: yes / partly "*" / no
  = dimmed, with a note in the help line): flags of "no" pages stay in the seed's flag
  string but change nothing in the PR yet.
- Tests: `tools/probe_headless.ps1 -WcTest "seed=a;preset=moogle cup;seed2=b;load=1"` (no
  window, muted; points Magicite at MagiciteWC for that run only and restores it): makes the
  seeds through the installed bundle, checks live rows (monster HP, item prices, character
  stats) and Magicite files against each seed, restores vanilla, checks the screen's logic
  (every page and flag row stepped/toggled: 185 rows, 925 actions), loads the latest save
  through the hook, deletes its seeds. Last run: 0 failures.
- Not verified: how the screen looks and feels (drawing needs a window; first look is the
  user's), the generator under Proton on the Deck, text in other game languages (seeds only
  carry English text).
- WC events, groundwork (2026-11-05): PR event scripts are per-map JSON (`map_*/.../sc_e_NNNN.json`,
  2017 scripts, 151k instructions) on the same mnemonic VM as the AI scripts (our
  `ai_compile.py` already emits it): 111 mnemonics, the story ones being `SetFlag` /
  `ResetFlag` / `Branch` on `ScenarioFlag1..N` (+ `MapLocalVariable`), `ChangePartySlot` /
  `OrganizePartySlot` / `BranchByCharacter` (party), `SysCall` with Japanese routine names
  (recruiting, vehicles...), `GetItem` / `RemoveItem` / `RemoveGil` / `TreasureBox`,
  `Encount` / `EncountBoss`, `ChangeMap` / `ChangeScript` / `SetEntities`, `Msg` keys
  (`E0309_00_383_a_01`). WC (`mods/worlds-collide/event/`, 62 modules) instead sets SNES
  event bits at the start (`event/start.py`: story scenarios finished, Shadow's dreams seen,
  continent floating...) and rewrites each check's SNES event code into a reward.
  Plan: (1) a SNES event bit -> PR ScenarioFlag map (match each SNES event's bit writes to
  the PR script of the same event, like idmap.py did for items); (2) per check, a PR script
  template that gives WC's reward (character / esper / item, from the seed's spoiler data,
  `-sl`) and sets the same flags; (3) those scripts written per seed as managed files
  (`wc_live.managed_files` grows by the scripts WC touches: Magicite serves them like the
  chest maps); (4) objectives and the Kefka's Tower unlock tracked by the plugin (flags +
  a menu page). Start-of-game state (party, gold, items, espers) can be set by the plugin
  at New Game before any of this. Checked 2026-11-05: the PR did NOT keep SNES event bit numbers as ScenarioFlag
  indexes (WC's 201 named bits hit used ScenarioFlag1 indexes 116 times, but shifted by
  1/3/7 they hit 116-119 times: chance); flags are spread over ScenarioFlag1 (0-368),
  ScenarioFlag2 (0-259), ScenarioFlag3 (0-79), ScenarioFlag4 (132-206), TreasureFlag1/2. The
  map needs event-by-event matching (WC's start bits first).
- WC events, first milestone done (2026-11-05): **WC's start**. New Game with a seed active
  skips the opening and starts on the Blackjack's deck (Map_20170, PR map 119) in the World of
  Balance with the airship parked outside Narshe (WoB tile 84,34: PR world tiles are the SNES
  grid, Narshe's entrance object sits at pixel 1344,544 = tile 84,33), the seed's starting
  party, and WC's gil (exactly the seed's: the PR's INIT_GIL 3000 is replaced), items and
  espers. Headless test (`-WcTest "seed=..;preset=moogle cup;newgame=1"`): party [10 13 1] =
  the seed's, gil 5000, Blackjack map 1 at 84,34, deck reached in ~4 s, 0 failures.
  How it works:
  * the event VM's `SysCall` looks names up in `External.SystemCallTable` and calls them
    (`External.Misc.SystemCall`, decompiled `tools/decomp/events`); a Harmony prefix
    (`WcEvents.cs`) takes names starting "WC:" so our scripts can call into the plugin for the
    per-seed part. `WC:start` (grants, sets MapLocalVariable 6 = 1 when a seed is active),
    `WC:party` (the seed's characters join through the game's own `メンバー加入：<name>`
    calls; status ids Terra 1 .. Umaro 14 = `Current.JoinCharacter`'s);
  * `tools/wc_events.py` writes the changed scripts (static, same for every seed) into the
    WorldsCollide Magicite folder with their asset keys (`wc_live.write_baseline`).
    `sc_e_0001` (the opening) jumps from its first instruction to an appended segment: the
    attract demo and seedless New Games go back to the vanilla code; otherwise Terra/Biggs/
    Wedge leave, `WC:party`, `SetVehicle [4, 1, 84, 34, 0, 0]` (Blackjack, WoB),
    `ChangeMap [119, 1, ..] sc_e_BJinboard_1` (the game's own boarding arrival). Appending
    and jumping keeps every vanilla jump index valid (`tools/pr_script.py`: load, dump with
    indices, append, save);
  * `tools/wc_run.py` runs WC's own `wc.main()` unchanged and records what its events chose
    (patched in through an import hook: WC claims ROM space while importing, so nothing may
    be imported before it). `wc_seed.py` stores the start party and every event's rewards in
    PR ids (`wc_seed.json` start.characters, checks: 57 events with gate character and
    rewards) for the plugin;
  * self-tests never save: the deck's arrival script autosaves, and the first New Game test
    (2026-11-05 12:53) overwrote the PC's autosave slot (21) with a test game before this guard
    existed (`WcSeeds.NoSaveInTest`).
  WC's start story flags (Locke/Terra/Sabin scenarios and the Vector dinner finished,
  Shadow's dreams seen, Banon met, Genji Glove given...): done since (`START_FLAGS` in
  tools/wc_events.py, PR flags found with `tools/pr_flags.py`, which catalogs all 668 PR flags;
  flags that would also start a story sequence, such as the "continent risen" ScenarioFlag2 28,
  are left out and their maps hooked instead). The 2026-11-21 audit of every start bit (144:
  110 handled, 24 not needed in the PR, 10 bits = 6 visible differences, fixed: the dated note at
  the end) closed it; a WC New Game is WC's start state, not the opening-of-the-game towns.
- WC events, second milestone (2026-11-05, all headless-tested, 0 failures):
  * **The event VM's jumps** (traced in game): Jump [T], Branch [.., .., T] and Call [T] go on
    at T + 1; every vanilla target is a labelled Nop that never runs itself. So a Jump put in
    place of a labelled Nop is skipped by code that branches there (the first wheel attempt
    ran the vanilla menu). `pr_script.hook(script, at, name, ops)` now diverts the
    instruction AFTER a label: it moves into the appended block (label "vanilla", then a jump
    back), and blocks use `S.label(x)` / "@x" targets. Also found: the opening's
    advertise-demo call sets map-local 7 to 1 on a New Game (0 in the title's demo).
  * **World switch** (WC event/airship.py): the Blackjack's wheel (sc_e_BJoutboard) offers
    "(Fly to the World of Ruin.)", the Falcon's (sc_e_FAoutboard) "(Fly to the World of
    Balance.)", in a WC game (ScenarioFlag4 0, set by WC's start). `WC:to_wor` / `WC:to_wob`
    put the other airship (transportation 5 / 4) at this one's position, map 2 / 1, released
    (`SetDataStorageFlag`: TreasureFlag4 136 / 135, which the wheels test as "placed before");
    then ChangeMap to that world with the game's own flying arrival (sc_e_FAoutboard_1 /
    sc_e_BJoutboard_1). ScenarioFlag2 97 ("after obtaining the Falcon") set in the WoR and
    cleared in the WoB, as WC does with IN_WOR / GOT_FALCON. Tests: worlds=1 (Blackjack wheel ->
    map 2, Falcon at 84,34) and worlds=2 (Falcon wheel -> map 1, Blackjack where the Falcon was).
    The wheel lines are new message keys (WC Magicite folder: message/.../story_mes_en.txt +
    story_cha_en.txt with an empty speaker, keys/wc_message.json; checked loaded in game).
  * **Checks**: `WC:reward <module> [n]` gives the seed's n-th reward for that WC event
    (character: the game's join call; esper: AddMagicStone; item: AddOwnedItem) and sets the
    text "WC_REWARD" to its "Obtained ..." line (names from content.csv / MessageManager,
    characters as (TINA)-style tags); each check's done flag is ScenarioFlag4 n
    (`wc_events.CHECK_FLAGS`, saved with the game). `WC:offer` / `WC:pay` put a seed's price in
    a line and take it (map-local 0 = paid, like the game's RemoveGil, whose argument indexes
    a price list we don't need).
  * **Tzen** (WC event/tzen.py, check flag 1): the esper seller is in WoB Tzen from the start
    (sc_map_20190; the PR shows him after ScenarioFlag1 203, a big story flag, so the map script
    branches on the WC game flag instead), sells the seed's reward for the seed's random price
    in each world (WC logs both: "(23,273 WOB, 45,335 WOR)", parsed by wc_seed.py), and buying
    in one world sells out the other (both vanilla bought flags, ScenarioFlag1 206 / ScenarioFlag2
    57). Test tzen=1: warp to Tzen, buy: offer text names the price, gil 28273 -> 5000, Phoenix
    magicite received, flags set; the second talk sells nothing.
  * Test tools (WcTest / WcEvents, test runs only): `GotoMap(map, point)` warps the way the
    ChangeMap instruction does (`EventProcedure.EventGotoMap` with a `LoadData`);
    `RunScript(name, answers...)` starts a script on the current map
    (`MainGame.eventProcedure.EventRunScript`; refused in flight and right after it: the
    airship lands/boards itself); Select windows answered from a queue, Msg windows logged and
    skipped; a trace of Jump/Call/Branch/SysCall/ChangeMap... with the program counter
    (hooking `MainCore.Execute` itself broke the VM: it returns a Nullable<int>).
- WC events, third milestone (2026-11-05):
  * **Character gates** (WC -cg, on in both presets): every join in a WC game sets
    ScenarioFlag4 100 + status id (a postfix on `Current.JoinCharacter`, which every join call
    goes through); `WC:gate <module>` sets ScenarioFlag4 131 to 1 when the check's gate character
    has joined or the seed has no -cg. Joins of our own (start party, character rewards) call
    `Current.JoinCharacter(id, -1, 1, true, false, false)` directly: the named calls (JoinGau...)
    look the character up first and throw for one that never joined.
  * **Ancient Castle** (check flag 2, gate Edgar): the stone queen (sc_e_0890) gives the seed's
    reward instead of turning Odin into Raiden. WC's start bit GOT_ODIN (no Odin statue) = the
    PR's ScenarioFlag2 172, tested only in the castle, now set by WC's start (`START_FLAGS`); it is
    also what makes the queen's event appear. Test: gate closed until Edgar joined, then Gau joined
    from the statue (the seed's reward), check flag set.
  * **Gau's father's house** (check flag 3, gate Shadow; Map_20090 = PR map 73): the NPC (the PR's
    Shadow) is there in a WC game until the check is done and the gate is met (the house's map
    script), talking (sc_e_0097) gives the seed's reward and he leaves (ScenarioFlag1 71, read only
    by this house). Test: gate closed / open, Relm joined.
  * **Kohlingen** (check flag 4, gate Setzer; Map_20131_1 = PR map 98): the NPC (the PR's Shadow,
    hired there for 3000 gil: so RemoveGil [203] = 3000) is in the WoB inn until the check is done
    and the gate is met; talking gives the seed's reward for free (WC), ScenarioFlag1 147 = hired.
    The PR has no WoR copy of this scene. Test: gate closed / open, the Ragnarok sword received.
  * **The freeze in Gau's father's house** was `PlaySE [id, 1]`: the 1 keeps the sound's handle
    in a script-local variable, and a script that declares none (that house's, Kohlingen's)
    makes the game throw inside PlaySE, which hangs the VM (no log; found by wrapping PlaySE in a
    hook, whose wrapper logged the exception instead). Vanilla uses [id, 1] only in scripts that
    declare one (1784 uses, 0 without). `pr_script.append` now gives every script it changes a
    local ("なし" = none, value 0) if it has none.
  * Learned on the way: the VM sometimes runs one instruction twice in a row (traced "ran 2x"); a
    reward that joined a character twice hung the game. Every `WC:` call now ignores an immediate
    repeat of the same script line, and joins skip characters already in the party. A script
    started from a test (`EventRunScript`) can't be started again in the same game session, even
    after a warp (the interpreter isn't busy and the text is there; not found why): each test runs
    a check's scene once and checks the closed gate through `WC:gate` itself. The autosave a test
    skips now tells its caller "done" (else the caller waits forever). Messages close themselves in
    tests; a test waits for the script's main core to Exit and polls for the check flag.
- WC events, fourth milestone (2026-11-05):
  * **The PR's merged towns**: the PR keeps one map for a town's WoB and WoR states (Narshe,
    Figaro... 40 maps) and switches them on ScenarioFlag2 41 ("after the world's collapse", set
    when the world collapses). The airships' world switch now sets it in the WoR and clears it in
    the WoB, with the Falcon flag: WC's IN_WOR.
  * **Tritoch** (check flag 5, no gate; the PR's Valigarmanda on Narshe's cliffs, map 20): the
    post-battle scene (sc_e_0780_1) gives the seed's reward instead of his magicite; the rest of the
    scene as vanilla (`pr_script.copy_ops` copies vanilla instructions into the block). The cliffs
    already show him in the WoR (vanilla: after the Falcon). Test (WoR flags set, warp, the
    post-battle scene without the battle): the Genji Glove.
  * **Zozo** (check flag 6, gate Terra; the top of the tower, map 395): only Ramuh is there in a WC
    game (WC removes esper-Terra and the extra magicite: the PR's Siren / Kirin / Cait Sith pickups,
    sc_e_0304-0306, are left out), his talk as vanilla, then the seed's reward instead of his
    magicite. WC's random clock puzzle isn't ported (the PR's clock answer and its NPC hints stay
    vanilla, so they agree). Test: Cyan joined.
  * The PR's 17 "Obtained ... magicite!" scenes, mapped to WC checks for the next ports: Fenrir =
    Mobliz WoR (map 212), Lakshmi = Owzer's Mansion (446), Midgardsormr = Umaro's cave (496),
    Alexander = Doma WoR (304), Ifrit/Shiva = Magitek Factory (398), Golem/Zona Seeker = Jidoor's
    auction (111), Crusader = Kefka's Tower (551, the dragons), Bahamut = Doom Gaze.
  * Next: the **Track menu** in the field menu: `Last.UI.CommandMenuController.Initalize(List<
    CommandMenuContentData>)` builds the main menu's entries (MenuCommandId: Item ... Save) and its
    confirm handler (`<UpdateController>b__12_0`) raises OnKeyAction(id); an entry with an unused
    id caught there can open a WC screen (Checks by gate character greyed when done, from the
    ScenarioFlag4 check flags; Progress counts; the seed's flags; objectives later).
- Test runs this round (all 0 failures): newgame+worlds=1, tzen=1, check=ancient_castle (also
  with Relm forced as the reward: `reward=character 9`), check=gau_father_house (Relm),
  check=kohlingen (an item), check=tritoch, check=zozo. `tools/wc_test_reset.py` puts the install back (no seed active,
  test seeds removed) when a test is killed before its own cleanup.
- Next for WC, in order (2026-11-05; (1) done since, see above): (1) the start's story flags; (2) each check's event:
  replace the vanilla reward with `SysCall WC:reward <check>` (the plugin gives the seed's
  character/esper/item and sets that check's done flag), strip the cutscenes WC strips, gate
  by character like WC; done flags in a ScenarioFlag range the PR never uses (ScenarioFlag4
  0-131 has no use in vanilla scripts), so they live in the save; (3) objectives (WC's 25
  condition types, results such as the Kefka's Tower unlock) tracked by the plugin from those
  flags; (4) the **Track menu** the user asked for (WC adds "Track" to the main menu:
  Objectives with progress, Checks grouped by gating character and greyed once done,
  Progress counts (checks, characters, espers, dragons, bosses), the seed's Flags): a
  field-menu entry like the title-menu one, opening a plugin screen in the WC screen's style,
  controller-navigable; (5) WC's **hints**: the Track menu's check lists, objective texts,
  and the event hints flags turn on (e.g. `-adeh`, the auction house door NPC naming the
  esper on sale); (6) cross-machine saves: Steam Cloud carries saves between PC and Deck but
  not the seed folders or the slot records, so a save made on one machine finds no seed on
  the other; a fix is to keep the seed text + flags with the slot and regenerate the seed
  when it's missing (generation is deterministic).
- Not ported yet (the bulk of WC): its rewritten events (open world, character gating,
  recruitment, 68 reward checks, 25 objectives, Kefka's Tower unlock), and the flags that are
  assembly patches rather than data (bug fixes, challenges, -move, -warp, -scan, -npctips...).
  A seed today = WC's randomized data and AI on the vanilla story.
- Airship (2026-11-21, WC event/airship.py): the Falcon's wheel offers **(Search the skies.)**
  (key WC_HELM_SKIES; WC's order Lift-off / World of Balance / Search The Skies / Not just yet)
  while Doom Gaze's gate is met (WC: Setzer recruited, with -cg; not "in the party") and his check
  flag (ScenarioFlag4 19) isn't set. Picked: ScenarioFlag4 128 (pending) + the wheel's own take-off;
  the arrival in the sky (sc_e_FAoutboard_1) clears it and runs sc_e_0964's EncountBoss 464 (the
  seed's boss swapped in by WcBattles), so what follows is the sky fight's own: sc_e_0964_1 (he
  flies off with his HP kept: the choice stays) or the reward scene sc_e_0964_2 (the seed's
  reward, the check flag: the choice is gone). WC fights him from the deck; the PR's fight and
  its after-battle scripts belong to the sky, hence the take-off first. -eu: the unequip NPC's
  two services (all three copies) run `WC:eu_unequip reserve|all` after the PR's own call, which
  skips only Umaro (tools/decomp/unequip); WcRecruits.EuUnequip takes his equipment (recruited;
  "those not in the party": out of it); Gogo is the PR call's already. Headless: check=search_skies,
  check=airship_crew, check=eu_unequip; newgame=1 now counts the start items and espers.
- Start state (2026-11-21, the start-bits audit: every `init_event_bits` WC's start runs, 144 bits,
  compared with the port; the 6 visible differences fixed, WC games only, tools/wc_events.py):
  (1) WoB Thamasa (`thamasa_burning_map`): Leo's grave, his sword and the three townsfolk
  (ev_e_0552_2 + reo_sword, SNES map 340's THAMASA_CITIZENS), no path blocker (ev_e_0552_1), no
  post-Zozo townsfolk (ev_e_0413); ScenarioFlag1 203 / ScenarioFlag2 26 not set (they arm other
  scenes); while the burning house is open its crowd (ev_e_0523) stands in town and the grave
  layer's man and boy (174, 175) are hidden. (2) Thamasa's inn (`thamasa_inn`): the after-fire
  innkeeper (ev_e_0540, product group 115 = 1 gil, as SNES WC with MET_STRAGO_RELM; vanilla's
  stranger is group 114 = 1500); with -cg until Strago is recruited both inns (WoB, WoR) give WC's
  stranger price (WC_THAMASA_INN, 100,000,000 gil: not enough money). (3) Mt. Zozo
  (`mt_zozo_books`): no key on the cliff, Cyan's chest stays "It's locked." (the PR draws it in the
  room's tiles: WC has no chest there at all). (4) Doma (`story_scenes`): Cyan's wife and son not
  lying in their room (sc_map_20101_6). (5) Phantom Forest (`phantom_forest_map`,
  `ghost_shop_files`): after the Phantom Train check a ghost (the train shopkeeper's entity,
  wandering a MoveArea) runs a copy of the train's shop (sc_e_1112, listed in map_30421's
  package.json); it's in the room's entity_default, hidden on arrival until then (vanilla's
  Kohlingen hides its old women the same way; a layer of our own can't be made by a player's
  setup: tools/wc_gamedata.py's recipe has no source for it), at one of WC's nine spots, map 0x85
  (13, 10) = Map_30421_2 (14, 10); WC picks the spot per seed and the seed folder doesn't record
  it (wc_run.py's `added` NPCs have no x/y), so it's always that one. (6) -cg: Siegfried
  (ev_e_0666) blocks the WoR Figaro cave's one-tile passage while the figaro_castle_wor gate is
  closed and only says WC's "Pretty dangerous from here on. Wait here." (`figaro_cave_siegfried`).
  Two unverified ones: (7) Morph: WC's start enables it, the port deals SNES Morph as the PR's
  command 9 (Revert, listed only in Trance) and only Zozo's Maduin scene adds Trance (8)
  (Current.AddComamndTrance = OwnedCharacterClient.AddCommand(Terra, 8), decompiled): no one could
  Morph in a WC game. WcEvents.GiveMorph (WcObjectives.Poll) gives 8 to every owned holder of 9,
  usable at morph points > 7 as in vanilla; a non-Terra holder (-com deals Morph to one character)
  Morphing in battle is untested. (8) The forest's walk to the healing spring (ev_e_0158) is cut,
  as WC deletes it (the spring itself stays). Not matched: SNES WC starts the burning house by
  sleeping at Thamasa's inn (in either world); here it stays the house's door. Offline:
  tests/test_wc.py (8 new tests). In-game check requests (`seed=kittest3;preset=moogle cup;
  newgame=1;check=<name>`): thamasa_town, thamasa_inn, mt_zozo_books, doma_family, ghost_shop,
  siegfried_gate, morph_start, forest_spring (not yet run).

#!/usr/bin/env bash
# Install one of the project's mods into the game on this PC and push it to the Steam Deck.
# Brave New World and Worlds Collide are separate mods, installed one at a time (the user,
# 2026-11-07: "WC/BNW need to be separate mods entirely"): installing one sets the other aside.
#
#   pr/tools/deploy.sh [--bnw | --wc] [--pc | --deck | --full]
#     --bnw / --wc  the mod to install (default: the one installed now; Brave New World if neither)
#     (no target)   build + install on the PC, then push to the Deck
#     --pc          PC only (Deck asleep / away)
#     --deck        only push what's installed on the PC to the Deck
#     --full        also resend BepInEx itself (core, interop, .NET) to the Deck
#   pr/tools/deploy.sh --deck-log   print the Deck's BepInEx log
#
# An installed mod is BepInEx/plugins/<Mod> + FINAL FANTASY VI_Data/StreamingAssets/Magicite/<Mod>,
# with Magicite's import directory the standard %StreamingAssets%/Magicite. The other mod's
# plugin folder moves to BepInEx/mod-shelf/<Mod> (BepInEx loads nothing there): Worlds Collide's
# seeds stay with it and come back with --wc. The old shared layout (MagiciteWC, MagiciteVanilla,
# the in-game Mods screen) is folded in on the first run.
#
# The Deck runs the same Windows build through Proton; BepInEx loads there only if Wine
# prefers the game's winhttp.dll. deploy.sh sets that in FF6's Proton prefix
# (tools/deck_winhttp_override.py), equivalent to the Steam launch option
# WINEDLLOVERRIDES="winhttp=n,b" %command%
# Neither game may be running while it deploys (files would be replaced under it).
set -euo pipefail

PC_GAME="/d/SteamLibrary/steamapps/common/FINAL FANTASY VI PR"
DECK=deck   # ssh host (keys in pageant)
DECK_GAME="/home/deck/.local/share/Steam/steamapps/common/FINAL FANTASY VI PR"
PR="$(cd "$(dirname "$0")/.." && pwd)"
MAGICITE="FINAL FANTASY VI_Data/StreamingAssets/Magicite"
SHELF="BepInEx/mod-shelf"
HD2D="BepInEx/plugins/HD2D"
MAGICITE_CFG="BepInEx/config/silvris.magicite.cfg"
# Worlds Collide's generator, baseline and seeds live in BepInEx/WorldsCollide, outside the plugin
# folder (WcDataDir.cs: BepInEx lists every folder under plugins at each failed assembly lookup,
# ~6.5 s each on the Deck with them there). wc_data_script moves an older install's copies there.
WC_DATA="BepInEx/WorldsCollide"

mod=""; do_pc=1; do_deck=1; full=0
for a in "$@"; do
  case "$a" in
    --bnw) mod=BraveNewWorld ;;
    --wc) mod=WorldsCollide ;;
    --deck) do_pc=0 ;;
    --pc) do_deck=0 ;;
    --full) full=1 ;;
    --deck-log) ssh -o ConnectTimeout=10 "$DECK" "cat '$DECK_GAME/BepInEx/LogOutput.log'"; exit ;;
    *) sed -n '2,17p' "$0"; exit 1 ;;
  esac
done
if [ -z "$mod" ]; then
  if [ -d "$PC_GAME/BepInEx/plugins/WorldsCollide" ] && [ ! -d "$PC_GAME/BepInEx/plugins/BraveNewWorld" ]; then mod=WorldsCollide; else mod=BraveNewWorld; fi
fi
other=BraveNewWorld; [ "$mod" = BraveNewWorld ] && other=WorldsCollide
PLUGIN="BepInEx/plugins/$mod"
OTHER_PLUGIN="BepInEx/plugins/$other"

say() { echo "[deploy] $*"; }

# the game's side of a switch, as a shell script run in the game folder (PC and Deck): the other
# mod's plugin folder to the shelf, its Magicite folder and the old layout's folders gone, this
# mod's shelved folder back (WC's seeds), Magicite's import directory the standard one
switch_script() {
  cat <<EOF
set -e
mkdir -p '$SHELF' '$MAGICITE'
if [ -d '$OTHER_PLUGIN' ]; then rm -rf '$SHELF/$other'; mv '$OTHER_PLUGIN' '$SHELF/$other'; echo "[deploy]   $other set aside ($SHELF/$other)"; fi
rm -rf '$MAGICITE/$other'
rm -rf 'FINAL FANTASY VI_Data/StreamingAssets/MagiciteWC' 'FINAL FANTASY VI_Data/StreamingAssets/MagiciteVanilla'
if [ ! -d '$PLUGIN' ] && [ -d '$SHELF/$mod' ]; then mv '$SHELF/$mod' '$PLUGIN'; echo "[deploy]   $mod back from the shelf"; fi
cfg='$MAGICITE_CFG'
if [ -f "\$cfg" ]; then
  if grep -q '^Import Directory' "\$cfg"; then sed -i 's|^Import Directory = .*|Import Directory = %StreamingAssets%/Magicite|' "\$cfg"
  else printf '\n[Magicite Paths]\nImport Directory = %%StreamingAssets%%/Magicite\n' >> "\$cfg"; fi
fi
EOF
}

# the game's side of the WC data move, as a shell script run in the game folder (PC and Deck):
# generator, baseline and seeds under the plugin folder (or its shelf) move to BepInEx/WorldsCollide
# where it has none yet; a part it already has leaves the old copy in BepInEx/wc-seed-backups.
# Nothing is deleted (deploy then mirrors generator and baseline over the moved copies).
wc_data_script() {
  cat <<'EOF'
set -e
mkdir -p BepInEx/WorldsCollide
stamp=$(date +%Y%m%d-%H%M%S)
for p in BepInEx/plugins/WorldsCollide BepInEx/mod-shelf/WorldsCollide; do
  for part in generator baseline seeds; do
    [ -d "$p/$part" ] || continue
    if [ ! -d "BepInEx/WorldsCollide/$part" ]; then
      mv "$p/$part" "BepInEx/WorldsCollide/$part"; echo "[deploy]   WC $part moved to BepInEx/WorldsCollide"
    else
      mkdir -p "BepInEx/wc-seed-backups/plugin-folder-$stamp"
      mv "$p/$part" "BepInEx/wc-seed-backups/plugin-folder-$stamp/$part"; echo "[deploy]   old WC $part kept in BepInEx/wc-seed-backups/plugin-folder-$stamp"
    fi
  done
done
EOF
}

if [ $do_pc = 1 ]; then
  if tasklist //FI "IMAGENAME eq FINAL FANTASY VI.exe" 2>/dev/null | grep -qi "FINAL FANTASY"; then
    say "the game is running on this PC: close it first"; exit 1
  fi
  say "PC: installing $mod ($other set aside)"
  (cd "$PC_GAME" && switch_script | bash)
  if [ "$mod" = BraveNewWorld ]; then
    (cd "$PR/plugin/BnwPlugin" && dotnet build -c Release -p:Deploy=true -nologo -v q | grep -E " error |Build succeeded" || true)
    src="$PR/mods/BraveNewWorld"
  else
    (cd "$PR" && .venv/Scripts/python tools/wc_flags_export.py >/dev/null)
    (cd "$PR/plugin/WcPlugin" && dotnet build -c Release -p:Deploy=true -nologo -v q | grep -E " error |Build succeeded" || true)
    (cd "$PR" && .venv/Scripts/python tools/wc_bundle.py | tail -1)
    (cd "$PC_GAME" && wc_data_script | bash)
    set +e
    for part in generator baseline; do
      MSYS_NO_PATHCONV=1 robocopy "$(cygpath -w "$PR/build/WorldsCollide/plugin/$part")" "$(cygpath -w "$PC_GAME/$WC_DATA/$part")" /MIR /NJH /NJS /NP /NFL /NDL >/dev/null
      [ $? -lt 8 ] || { say "robocopy failed ($part)"; exit 1; }
    done
    set -e
    src="$PR/build/WorldsCollide/magicite/WorldsCollide"
  fi
  # HD-2D (pr/hd2d, pr/plugin/Hd2dPlugin) is shelved and uninstalled (2026-11-05); to put it back:
  #   cd pr/plugin/Hd2dPlugin && dotnet build -c Release -p:Deploy=true
  dotnet build-server shutdown >/dev/null 2>&1 || true
  say "PC: mirroring $mod's Magicite folder"
  set +e
  MSYS_NO_PATHCONV=1 robocopy "$(cygpath -w "$src")" "$(cygpath -w "$PC_GAME/$MAGICITE/$mod")" /MIR /NJH /NJS /NP /NFL /NDL >/dev/null
  rc=$?
  set -e
  [ $rc -lt 8 ] || { say "robocopy failed ($rc)"; exit 1; }
fi

if [ $do_deck = 1 ]; then
  if [ ! -d "$PC_GAME/$PLUGIN" ]; then say "$mod isn't installed on the PC: deploy it there first"; exit 1; fi
  if ! ssh -o ConnectTimeout=10 -o BatchMode=yes "$DECK" true 2>/dev/null; then
    say "Deck not reachable (asleep or off the network?): PC is done, run --deck later"; exit 1
  fi
  if ssh "$DECK" "pgrep -f 'FINAL FANTASY VI[.]exe' >/dev/null"; then
    say "the game is running on the Deck: close it first"; exit 1
  fi
  if [ $full = 1 ] || ! ssh "$DECK" "[ -d '$DECK_GAME/BepInEx/core' ]"; then
    say "Deck: sending BepInEx (core, interop, .NET runtime, loader)"
    tar -cf - -C "$PC_GAME" --exclude=BepInEx/LogOutput.log --exclude=BepInEx/ErrorLog.log \
        --exclude=BepInEx/plugins --exclude="$SHELF" --exclude=BepInEx/config BepInEx dotnet winhttp.dll doorstop_config.ini \
      | ssh "$DECK" "tar -xf - -C '$DECK_GAME'"
    # config files only where the Deck has none (its own settings stay, even with --full)
    tar -cf - -C "$PC_GAME" BepInEx/config | ssh "$DECK" "tar -xf - --skip-old-files -C '$DECK_GAME'"
  fi
  # BepInEx's loader (winhttp.dll) only runs if Wine is told to prefer the game's copy
  ssh "$DECK" python3 - < "$PR/tools/deck_winhttp_override.py" || say "Deck: winhttp override not set (see above)"
  say "Deck: installing $mod ($other set aside)"
  switch_script | ssh "$DECK" "cd '$DECK_GAME' && bash"
  # this mod's plugin and Magicite folders replace the Deck's (WC's seeds stay: synced below)
  ssh "$DECK" "cd '$DECK_GAME' && find '$PLUGIN' -mindepth 1 -maxdepth 1 ! -name seeds -exec rm -rf {} + 2>/dev/null; rm -rf '$MAGICITE/$mod' '$HD2D'"
  extra=""; [ -d "$PC_GAME/$HD2D" ] && extra="$HD2D"
  if [ "$mod" = WorldsCollide ]; then
    # WC's generator and baseline from the PC's data folder, over the Deck's (its seeds: synced below)
    wc_data_script | ssh "$DECK" "cd '$DECK_GAME' && bash"
    extra="$extra $WC_DATA/generator $WC_DATA/baseline"
  fi
  tar -cf - -C "$PC_GAME" --exclude="$PLUGIN/seeds" "$PLUGIN" $extra "$MAGICITE/$mod" \
    | ssh "$DECK" "tar -xf - -C '$DECK_GAME'"
  # BepInEx/config: only files the Deck doesn't have yet (a mod's first .cfg). Settings chosen on
  # the Deck (in-game options such as Battle Options' Pause) are never replaced by the PC's.
  tar -cf - -C "$PC_GAME" --exclude="$MAGICITE_CFG" BepInEx/config \
    | ssh "$DECK" "tar -xf - --skip-old-files -C '$DECK_GAME'"
  if [ "$mod" = WorldsCollide ]; then
    # Worlds Collide seeds and their save slots, both ways (Steam Cloud carries only the saves)
    (cd "$PR" && MSYS_NO_PATHCONV=1 .venv/Scripts/python tools/wc_sync_seeds.py "$(cygpath -w "$PC_GAME/$WC_DATA/seeds")" "$DECK" "$DECK_GAME/$WC_DATA/seeds") \
      || say "Deck: seed sync failed (see above)"
  fi
  ssh "$DECK" "cd '$DECK_GAME' && echo \"[deploy] Deck now has: \$(ls BepInEx/plugins | tr '\n' ' ')| \$(find '$MAGICITE/$mod' -type f | wc -l) $mod mod files\""
fi
say "done ($mod)"

"""The public Worlds Collide repository (ff6pr-worlds-collide): this project's WC parts, copied
into their own git tree, with the modding kit as its kit/ submodule.

    .venv/Scripts/python tools/wc_publish.py [OUT]     (default: ../../ff6pr-worlds-collide)

Copies the WC plugin, WC's tools and tests and its presets into OUT with the project's layout
(pr/..., mods/...), writes its README, .gitignore, requirements and manifest, and records two
submodules: WC itself (github.com/ff6wc/WorldsCollide, mods/worlds-collide) at the commit this port
uses, and the kit (ff6pr-kit, kit/) at the kit repository's current commit, so publish and commit
the kit first (tools/kit_publish.py). Nothing of Brave New World's (the user, 2026-11-19: "make sure
WC and BNW are different repositories"). OUT's .git is left alone: commit and push from there.
The checks every public copy gets are tools/repo_publish.py's.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import kitpath  # noqa: E402,F401  (the modding kit's tools, when it's this repository's kit/ submodule)
import repo_publish as R  # noqa: E402

DEFAULT_OUT = os.path.join(os.path.dirname(R.PROJECT), "ff6pr-worlds-collide")
WC = os.path.join(R.PROJECT, "mods", "worlds-collide")
WC_URL = "https://github.com/ff6wc/WorldsCollide.git"

PARTS = [
    ("pr/plugin/WcPlugin", ["**"]),
    ("pr/plugin/Wc.Tests", ["**"]),
    ("pr/plugin/WcAi.Tests", ["**"]),
    ("pr/tools", R.WC_TOOLS + ["wc_publish.py"]),
    ("pr/tests", ["test_wc.py", "test_wc_romfree.py", "test_wc_xref.py"]),
    ("pr/docs", ["wc.md"]),
    ("mods", ["worlds-collide-presets.json"]),
]

README = """# Worlds Collide for FINAL FANTASY VI Pixel Remaster

[Worlds Collide](https://github.com/ff6wc/WorldsCollide) {wc_version}, the FF6 open-world randomizer,
played in the Steam Pixel Remaster: make seeds in the game with every option of the website
(presets, pasted flag strings, a seed field), play them, switch between them; saves remember
their seed. Seeds match the website's for the same seed and flags.

## Play

Download `WorldsCollide-PR.zip` from the [latest release](../../releases/latest) and follow its
README: BepInEx 6 (IL2CPP, bleeding edge) and Magicite first, then extract the zip into the
game folder. Steam Deck / Linux: add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game's
launch options. The first start takes about ten seconds longer: the mod makes the game data it
works from out of your own copy of the game (no game files are distributed).

On the title screen choose **Worlds Collide**, pick a preset or paste flags, then
**Generate and play this seed** and start a New Game.

## How it works

- `pr/plugin/WcPlugin`: the BepInEx plugin (the in-game seed screen, live data apply, WC's
  events, objectives, the track menu, WC's battle rules and the monster AI WC writes).
- `pr/tools`: the generator, run in the game with a bundled Python: WC's own code (the
  `mods/worlds-collide` submodule, untouched) on the ROM's data tables, then the kit's porter
  that turns its output into Pixel Remaster data.
- `kit/`: the [modding kit](https://github.com/thomasjteachey/ff6pr-kit) this mod builds on (a
  submodule): the porter, the build/package/deploy tools, the harnesses. `kit/pr/PORTING.md`
  is the project's status and index, `kit/pr/MODDING.md` the tooling guide.
- `pr/docs/wc.md`: this port's development log (what was built, tested and decided, by date).
- No ROM and no game files: `pr/tools/wc_gamedata.py` + the kit's `pr_bundle.py` make the copies
  the mod works from on the player's machine at first start.

## Build

Needs Windows, FF6 PR with BepInEx 6 and Magicite installed, .NET 6 SDK, Python 3.12, and the US
v1.0 SNES ROM (`rom/vanilla/Final Fantasy III (USA).sfc`, for WC's data tables; never shipped).

    git clone --recurse-submodules <this repo>
    cd pr && python -m venv .venv && .venv/Scripts/pip install -r requirements.txt
    .venv/Scripts/python ../kit/pr/tools/extract.py --game "<your FF6 PR folder>"   # pr/vanilla
    bash ../kit/pr/tools/deploy.sh --wc --pc                                       # build + install
    .venv/Scripts/python ../kit/pr/tools/package_mod.py wc                         # build/dist zip
    .venv/Scripts/python -m unittest tests.test_wc                                 # offline tests

The plugin builds against your game's BepInEx (`-p:GameDir=<folder>` if it isn't
`D:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR`).

## Credits

Worlds Collide by AtmaTek and its contributors (MIT). BepInEx, Il2CppInterop, HarmonyX,
Magicite by Silvris. FINAL FANTASY VI is Square Enix's; this is an unofficial fan mod.
"""

GITIGNORE = R.GITIGNORE_COMMON + "pr/data/wc_base.bin\n"

# Publishing a GitHub release (not a pre-release) uploads its tested WorldsCollide-PR.zip to Nexus mod 165, the way
# Ivalice ReMixed's publish-nexus.yml does (2026-11-25: the user chose releases without a browser). The key is the
# repository's Actions secret NEXUSMODS_API_KEY, which the user sets (gh secret set); nobody else handles it.
# v3's changelog endpoint takes the mod's uid, not 165 (BNW 0.26.7's first run: "Mod not found"). Run it by hand with
# changelog_only to post a release's notes without uploading again.
NEXUS_WORKFLOW = """name: Publish Nexus release

on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      version:
        description: Release version to upload (for example, 0.6.13)
        required: true
        type: string
      changelog_only:
        description: Only post the release notes as that version's Nexus changelog (no upload)
        required: false
        default: false
        type: boolean

permissions:
  contents: read

concurrency:
  group: nexus-mod-165
  cancel-in-progress: false

jobs:
  publish:
    if: >-
      github.event_name == 'workflow_dispatch' ||
      (github.event.release.draft == false && github.event.release.prerelease == false)
    runs-on: ubuntu-latest

    steps:
      - name: Resolve release version
        id: version
        shell: bash
        env:
          DISPATCH_VERSION: ${{ inputs.version }}
          RELEASE_TAG: ${{ github.event.release.tag_name }}
        run: |
          version="${DISPATCH_VERSION:-$RELEASE_TAG}"
          version="${version#v}"
          if [[ ! "$version" =~ ^[0-9]+\\.[0-9]+\\.[0-9]+$ ]]; then
            echo "Invalid release version: $version" >&2
            exit 1
          fi
          echo "version=$version" >> "$GITHUB_OUTPUT"

      - name: Download the tested release zip
        shell: bash
        env:
          GH_TOKEN: ${{ github.token }}
          VERSION: ${{ steps.version.outputs.version }}
        run: |
          mkdir -p release
          gh release download "v$VERSION" --repo "$GITHUB_REPOSITORY" --pattern "WorldsCollide-PR.zip" --dir release
          test -f release/WorldsCollide-PR.zip

      - name: Check the zip (the player package, no ROM)
        shell: python
        run: |
          import pathlib
          import zipfile

          with zipfile.ZipFile("release/WorldsCollide-PR.zip") as archive:
              names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
          roots = {pathlib.PurePosixPath(name).parts[0] for name in names}
          if roots != {"BepInEx", "WorldsCollide-README.txt"}:
              raise SystemExit(f"Unexpected top-level entries: {sorted(roots)}")
          for name in names:
              path = pathlib.PurePosixPath(name)
              if path.is_absolute() or ".." in path.parts:
                  raise SystemExit(f"Unsafe archive path: {name}")
              if path.suffix.lower() in (".smc", ".sfc", ".swc", ".fig"):
                  raise SystemExit(f"A ROM in the zip: {name}")
          if "BepInEx/plugins/WorldsCollide/WorldsCollide.PR.dll" not in names:
              raise SystemExit("The zip has no Worlds Collide plugin")
          print(f"{len(names)} files ok")

      - name: Read the release notes for the Nexus changelog
        id: notes
        shell: bash
        env:
          GH_TOKEN: ${{ github.token }}
          VERSION: ${{ steps.version.outputs.version }}
        run: |
          gh release view "v$VERSION" --repo "$GITHUB_REPOSITORY" --json body --jq .body |
            sed -e '/^#/d' -e 's/\\*\\*//g' -e 's/^- /* /' | sed -e '/./,$!d' > notes.txt
          {
            echo "changelog<<NOTES_END"
            cat notes.txt
            echo "NOTES_END"
          } >> "$GITHUB_OUTPUT"

      - name: Find the Nexus file to update
        id: nexus_file
        shell: bash
        env:
          NEXUSMODS_API_KEY: ${{ secrets.NEXUSMODS_API_KEY }}
        run: |
          mod_uid="$(curl --fail --silent --show-error \\
            --header "apikey: $NEXUSMODS_API_KEY" \\
            --header "User-Agent: ff6pr-worlds-collide/release-workflow" \\
            https://api.nexusmods.com/v1/games/finalfantasy6pixelremaster/mods/165.json |
            jq -er '.uid')"
          response="$(curl --fail --silent --show-error \\
            --header "apikey: $NEXUSMODS_API_KEY" \\
            --header "User-Agent: ff6pr-worlds-collide/release-workflow" \\
            "https://api.nexusmods.com/v3/mods/$mod_uid/files")"
          file_id="$(jq -er '.data.mod_files | map(select(.is_active == true)) |
            if length == 1 then .[0].id else error("Expected exactly one active file, got \\(length)") end' <<<"$response")"
          echo "file_id=$file_id" >> "$GITHUB_OUTPUT"
          echo "mod_uid=$mod_uid" >> "$GITHUB_OUTPUT"

      - name: Post the changelog only
        if: inputs.changelog_only == true
        shell: bash
        env:
          NEXUSMODS_API_KEY: ${{ secrets.NEXUSMODS_API_KEY }}
          MOD_UID: ${{ steps.nexus_file.outputs.mod_uid }}
          VERSION: ${{ steps.version.outputs.version }}
        run: |
          jq -n --arg version "$VERSION" --rawfile changelog notes.txt '{version: $version, changelog: $changelog}' |
            curl --fail-with-body --silent --show-error --request POST \\
              --header "apikey: $NEXUSMODS_API_KEY" \\
              --header "Content-Type: application/json" \\
              --header "User-Agent: ff6pr-worlds-collide/release-workflow" \\
              --data @- "https://api.nexusmods.com/v3/mods/$MOD_UID/changelogs"

      - name: Upload Worlds Collide to Nexus Mods
        if: inputs.changelog_only != true
        uses: Nexus-Mods/upload-action@98532f7670c27cbecc78fae03fa3e278147827e2 # v1.0.0-beta.11
        with:
          api_key: ${{ secrets.NEXUSMODS_API_KEY }}
          file_id: ${{ steps.nexus_file.outputs.file_id }}
          filename: release/WorldsCollide-PR.zip
          version: ${{ steps.version.outputs.version }}
          display_name: Worlds Collide ${{ steps.version.outputs.version }}
          description: Worlds Collide for FINAL FANTASY VI Pixel Remaster. The install steps are on the mod page.
          category: main
          archive_existing_version: "true"
          primary_mod_manager_download: "true"
          allow_mod_manager_download: "false"
          show_requirements_pop_up: "false"
          update_mod_version: "true"
          changelog: ${{ steps.notes.outputs.changelog }}
          mod_id: ${{ steps.nexus_file.outputs.mod_uid }}
"""


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT)
    entries = list(R.collect(PARTS))
    wc_commit = R.head(WC)
    kit_commit = R.head(R.KIT_REPO)
    version = "1.4.3"
    try:
        sys.path.insert(0, WC)
        import version as wcv  # noqa: E402  (WC's own version.py)
        version = wcv.__version__
    except Exception:
        pass
    texts = {"README.md": README.format(wc_version=version), ".gitignore": GITIGNORE,
             "pr/requirements.txt": R.REQUIREMENTS, "pr/modkit.json": R.manifest(keep=("wc",)),
             ".github/workflows/publish-nexus.yml": NEXUS_WORKFLOW}
    done = R.write(out, entries, texts, submodules=[("kit", R.KIT_URL, kit_commit),
                                                    ("mods/worlds-collide", WC_URL, wc_commit)])
    print(f"WC publish: {done} (WC {version}) -> {out}")


if __name__ == "__main__":
    main()

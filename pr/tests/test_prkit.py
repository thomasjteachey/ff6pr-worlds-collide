"""The modding kit's tooling (tools/prkit): game registry, Magicite writer/validator, manifest,
packaging, deploy (into a fake game folder), BepInEx setup, scaffolding. Fast and offline: no game
is started, nothing in the real game folders or Deck is touched.

    .venv/Scripts/python -m unittest tests.test_prkit -v
    PRKIT_BUILD=1 ...   also scaffolds a mod and builds it with dotnet (a minute)
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "tools"))
from prkit import PR, bepinex, data, deploy, games, headless, magicite, mods, package, scaffold  # noqa: E402

FF6_DATA = data.GameData("ff6")


def touch(path, content=b"x"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(content if isinstance(content, bytes) else content.encode("utf-8"))


def fake_game(root, spec_id="ff6", bepinex_installed=True):
    """A game folder shaped like a PR install (exe, _Data, BepInEx core, Magicite plugin)."""
    g = games.BY_ID[spec_id]
    install = os.path.join(root, g.installdir)
    touch(os.path.join(install, g.title + ".exe"))
    os.makedirs(os.path.join(install, g.title + "_Data", "StreamingAssets", "aa", "StandaloneWindows64"), exist_ok=True)
    if bepinex_installed:
        for f in ["BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/core/BepInEx.Core.dll", "BepInEx/plugins/Magicite.dll",
                  "BepInEx/plugins/Magicite.bundle", "dotnet/coreclr.dll", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"]:
            touch(os.path.join(install, *f.split("/")))
    return install


class Registry(unittest.TestCase):
    def tearDown(self):
        games.forget()

    def test_ids_and_aliases(self):
        self.assertEqual([s.id for s in games.SPECS], ["ff1", "ff2", "ff3", "ff4", "ff5", "ff6"])
        for name, want in [("ff5", "ff5"), ("5", "ff5"), ("FFV", "ff5"), ("final fantasy vi", "ff6"), ("FF1", "ff1")]:
            self.assertEqual(games.resolve_id(name), want, name)
        with self.assertRaises(KeyError):
            games.resolve_id("ff7")
        self.assertEqual(len({s.appid for s in games.SPECS}), 6)

    def test_steam_library_discovery(self):
        with tempfile.TemporaryDirectory() as lib:
            touch(os.path.join(lib, "steamapps", "appmanifest_1173810.acf"),
                  '"AppState"\n{\n\t"appid"\t\t"1173810"\n\t"installdir"\t\t"FINAL FANTASY V PR"\n}\n')
            install = fake_game(os.path.join(lib, "steamapps", "common"), "ff5")
            with mock.patch.dict(os.environ, {"PRKIT_STEAM_LIBRARIES": lib}):
                games.forget()
                g = games.get("ff5")
                self.assertTrue(g.installed)
                self.assertEqual(os.path.normcase(g.install), os.path.normcase(install))
                self.assertEqual(g.process, "FINAL FANTASY V")
                self.assertTrue(g.data_dir.endswith("FINAL FANTASY V_Data"))
                self.assertTrue(g.bundles.replace("\\", "/").endswith("StreamingAssets/aa/StandaloneWindows64"))
                self.assertEqual(os.path.normcase(g.magicite_dir), os.path.normcase(os.path.join(g.streaming_assets, "Magicite")))
                self.assertEqual(g.deck_install, "/home/deck/.local/share/Steam/steamapps/common/FINAL FANTASY V PR")
                self.assertEqual(g.deck_prefix, "/home/deck/.local/share/Steam/steamapps/compatdata/1173810/pfx")
                self.assertEqual(os.path.normcase(g.vanilla), os.path.normcase(os.path.join(PR, "games", "ff5", "vanilla")))
                s = g.status()
                self.assertTrue(s["bepinex"] and s["magicite"])
                self.assertFalse(s["interop"])
                self.assertFalse(games.get("ff4").installed)
                self.assertIn("<PrGameDir_ff5>", games.props_text())

    def test_vdf_paths(self):
        text = '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"D:\\\\SteamLibrary"\n\t}\n}'
        self.assertEqual(games._vdf_paths(text), [r"C:\Program Files (x86)\Steam", r"D:\SteamLibrary"])

    def test_magicite_import_dir_setting(self):
        with tempfile.TemporaryDirectory() as root:
            install = fake_game(root)
            touch(os.path.join(install, "BepInEx", "config", "silvris.magicite.cfg"),
                  "[Magicite Paths]\n# comment\nImport Directory = %StreamingAssets%/MagiciteWC\n")
            g = games.Game(games.BY_ID["ff6"], install, "FINAL FANTASY VI PR")
            self.assertTrue(g.magicite_dir.replace("\\", "/").endswith("StreamingAssets/MagiciteWC"))

    def test_ff6_on_this_pc(self):
        g = games.get("ff6")
        if not g.installed:
            self.skipTest("FF6 PR isn't installed here")
        self.assertEqual(g.process, "FINAL FANTASY VI")
        self.assertTrue(os.path.isdir(g.bundles))
        self.assertEqual(os.path.normcase(g.vanilla), os.path.normcase(os.path.join(PR, "vanilla")))


@unittest.skipUnless(FF6_DATA.extracted, "FF6's data isn't extracted (kit.py extract ff6)")
class GameDataAndMagicite(unittest.TestCase):
    def test_tables_text_addresses(self):
        items = FF6_DATA.table("item")
        self.assertEqual(items.columns[0], "id")
        self.assertIn(1, items)
        self.assertIn("monster", FF6_DATA.table_names())
        self.assertEqual(FF6_DATA.addresses("master")["item"], "Assets/GameAssets/Serial/Data/Master/item")
        self.assertTrue(any(k.startswith("MSG_") for k in FF6_DATA.messages("system")))

    def test_write_then_validate(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "MyMod")
            mod = magicite.MagiciteMod(out, FF6_DATA, keys_name="mymod")
            items = mod.table("item")
            items.set(1, "buy", 7)
            items.set(2, "buy", items[2]["buy"])          # unchanged: not written
            new = items.add({"buy": 99}, copy_of=1)
            mod.text()["MSG_TEST_KEY"] = "Hello"
            mod.asset("battleextradata", "Assets/GameAssets/Serial/Data/BattleExtraData/battle_extra_data.json", {"a": 1})
            written = mod.write()
            self.assertEqual(written, {"master": 1, "message": 1, "battleextradata": 1})
            csv_path = os.path.join(out, "master", *data.MASTER_PATH.split("/"), "item.csv")
            with open(csv_path, encoding="utf-8") as f:
                rows = f.read().splitlines()
            self.assertEqual(rows[0], ",".join(FF6_DATA.table("item").columns))
            self.assertEqual(sorted(int(r.split(",")[0]) for r in rows[1:]), [1, new])
            with open(os.path.join(out, "battleextradata", "keys", "mymod_battleextradata.json"), encoding="utf-8") as f:
                self.assertEqual(json.load(f)["keys"], ["battle_extra_data"])   # the game's address
            problems, notes = magicite.validate(out, FF6_DATA)
            self.assertEqual(problems, [])
            self.assertTrue(any("adds 1 rows" in n for n in notes))
            self.assertNotIn(new, FF6_DATA.table("item"))          # the game's own table is untouched

            # what the validator must catch
            with open(csv_path, "w", encoding="utf-8") as f:
                f.write("id,buy\n1,7\n")
            msg = os.path.join(out, "message", *data.MESSAGE_PATH.split("/"), "system_en.txt")
            with open(msg, "a", encoding="utf-8") as f:
                f.write("no tab here\n")
            touch(os.path.join(out, "master", *data.MASTER_PATH.split("/"), "stray.csv"), "id\n")
            os.remove(os.path.join(out, "battleextradata", "keys", "mymod_battleextradata.json"))
            problems, _ = magicite.validate(out, FF6_DATA)
            text = "\n".join(problems)
            self.assertIn("item.csv columns differ", text)
            self.assertIn("no tab", text)
            self.assertIn("stray.csv: no keys entry", text)
            self.assertIn("battle_extra_data.json: no keys entry", text)
            shutil.rmtree(os.path.join(out, "battleextradata", "keys"))
            problems, _ = magicite.validate(out, FF6_DATA)
            self.assertIn("battleextradata: 1 files but no keys/ folder", "\n".join(problems))

    def test_shipping_mods_validate(self):
        for m in mods.all_mods_list():
            if m.magicite and os.path.isdir(m.path(m.magicite)):
                problems, _ = magicite.validate(m.path(m.magicite), FF6_DATA)
                self.assertEqual(problems, [], m.id)


OLD_README_BNW = """Brave New World 3.0 for FINAL FANTASY VI Pixel Remaster
=======================================================

Install (once): BepInEx 6 for IL2CPP games and the Magicite mod loader in the game folder.
Then extract this zip into the game folder (the one with FINAL FANTASY VI.exe), keeping its
folders. Remove the mod again by deleting BepInEx/plugins/BraveNewWorld and
FINAL FANTASY VI_Data/StreamingAssets/Magicite/BraveNewWorld.

This is a mod of its own: don't install it beside the project's other mod (Brave New World and
Worlds Collide each change the same game data). Start a new game after installing.
"""


class Manifest(unittest.TestCase):
    def test_project_manifest(self):
        found = mods.load()
        for mid in ("bnw", "wc", "hd2d"):
            self.assertIn(mid, found)
        bnw, wc = found["bnw"], found["wc"]
        self.assertEqual([m.id for m in bnw.rivals()], ["wc"])   # never installed together
        self.assertEqual(wc.keep, ["seeds"])
        self.assertEqual(mods.get("BraveNewWorld").id, "bnw")
        for m in found.values():
            self.assertTrue(m.game_id in games.BY_ID, m.id)
            for src, _ in m.plugin_files:
                self.assertFalse(src.endswith((".sfc", ".smc")), m.id)

    def test_readme_matches_the_old_packager(self):
        self.assertEqual(package.readme(mods.get("bnw")), OLD_README_BNW)

    def test_dumps_round_trip(self):
        with open(mods.MANIFEST, encoding="utf-8") as f:
            doc = json.load(f)
        self.assertEqual(json.loads(mods.dumps(doc)), doc)


class ScaffoldPackageDeploy(unittest.TestCase):
    """A mod made by the scaffold in a temp project, packaged, and deployed into a fake game."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="prkit_test_")
        self.root = os.path.join(self.tmp, "pr")
        os.makedirs(self.root)
        self.log = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)
        games.forget()

    def make(self, name, **kw):
        return scaffold.new_mod(name, "ff6", root=self.root, kit_dir=os.path.join(PR, "plugin", "PrKit"),
                                out=self.log.append, **kw)

    def test_scaffold(self):
        mid = self.make("FiveJobs", title='Five "Jobs"')
        self.assertEqual(mid, "fivejobs")
        p = os.path.join(self.root, "plugin", "FiveJobsPlugin")
        for f in ("FiveJobsPlugin.csproj", "Plugin.cs", "ExampleFeature.cs"):
            self.assertTrue(os.path.exists(os.path.join(p, f)), f)
        self.assertTrue(os.path.exists(os.path.join(self.root, "tools", "data_fivejobs.py")))
        self.assertTrue(os.path.exists(os.path.join(self.root, "tests", "test_fivejobs.py")))
        with open(os.path.join(p, "FiveJobsPlugin.csproj"), encoding="utf-8") as f:
            proj = f.read()
        self.assertIn("<PrGame>ff6</PrGame>", proj)
        self.assertIn("PrKit.props", proj)
        with open(os.path.join(p, "Plugin.cs"), encoding="utf-8") as f:
            cs = f.read()
        self.assertIn("class FiveJobsRunner : MonoBehaviour", cs)   # its own runner name
        self.assertIn("Game.Require(PrGame.FF6)", cs)
        self.assertIn('MagiciteDir.Has("FiveJobs")', cs)
        m = mods.get("fivejobs", self.root)
        self.assertEqual(m.magicite, "build/FiveJobs/magicite")
        self.assertEqual(m.commands("build")[0][0], sys.executable)
        with self.assertRaises(KeyError):
            self.make("FiveJobs")
        with self.assertRaises(ValueError):
            self.make("five jobs")
        self.make("JustCode", with_data=False)
        self.assertIsNone(mods.get("justcode", self.root).magicite)
        with open(os.path.join(self.root, "plugin", "JustCodePlugin", "Plugin.cs"), encoding="utf-8") as f:
            self.assertNotIn("MagiciteDir.Has", f.read())

    def build_fake(self, mid, name):
        """Stand-ins for a mod's built parts (no dotnet here)."""
        touch(os.path.join(self.root, "plugin", f"{name}Plugin", "bin", "Release", "net6.0", f"{name}.PR.dll"), name)
        touch(os.path.join(self.root, "build", name, "magicite", "master", "keys", f"{mid}_master.json"), '{"keys": [], "values": []}')

    def test_package_layout(self):
        self.make("Pkg")
        self.build_fake("pkg", "Pkg")
        touch(os.path.join(self.root, "build", "Pkg", "magicite", "rom.sfc"))   # never shipped
        path, n = package.package("pkg", os.path.join(self.tmp, "dist"), root=self.root)
        self.assertTrue(path.endswith("Pkg-PR-0.1.0.zip"))
        names = zipfile.ZipFile(path).namelist()
        self.assertIn("BepInEx/plugins/Pkg/Pkg.PR.dll", names)
        self.assertIn("FINAL FANTASY VI_Data/StreamingAssets/Magicite/Pkg/master/keys/pkg_master.json", names)
        self.assertIn("Pkg-README.txt", names)
        self.assertFalse([x for x in names if x.endswith(".sfc")])

    def test_deploy_into_a_fake_game(self):
        install = fake_game(os.path.join(self.tmp, "common"))
        for name in ("Alpha", "Beta"):
            self.make(name)
            self.build_fake(name.lower(), name)
        manifest = os.path.join(self.root, "modkit.json")
        with open(manifest, encoding="utf-8") as f:
            doc = json.load(f)
        for mid in ("alpha", "beta"):
            doc["mods"][mid]["exclusive"] = "same-data"
            doc["mods"][mid]["keep"] = ["saves"]
        with open(manifest, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        g = games.Game(games.BY_ID["ff6"], install, "FINAL FANTASY VI PR")
        with mock.patch.object(games.Game, "running", return_value=False), \
                mock.patch.object(mods.Mod, "game", new_callable=mock.PropertyMock, return_value=g):
            a, b = mods.get("alpha", self.root), mods.get("beta", self.root)
            deploy.Deployer(a, out=self.log.append).install_pc()
            plugins, mg, shelf = g.plugins_dir, g.magicite_dir, g.shelf_dir
            self.assertTrue(os.path.exists(os.path.join(plugins, "Alpha", "Alpha.PR.dll")))
            self.assertTrue(os.path.isdir(os.path.join(mg, "Alpha", "master")))
            touch(os.path.join(plugins, "Alpha", "saves", "slot1.json"))            # the player's data
            touch(os.path.join(plugins, "Alpha", "stale.dll"))
            deploy.Deployer(b, out=self.log.append).install_pc()                    # sets Alpha aside
            self.assertFalse(os.path.exists(os.path.join(plugins, "Alpha")))
            self.assertFalse(os.path.exists(os.path.join(mg, "Alpha")))
            self.assertTrue(os.path.exists(os.path.join(shelf, "Alpha", "saves", "slot1.json")))
            self.assertTrue(os.path.isdir(os.path.join(shelf, "Alpha.magicite")))
            self.assertTrue(os.path.exists(os.path.join(plugins, "Beta", "Beta.PR.dll")))
            deploy.Deployer(a, out=self.log.append).install_pc()                    # Alpha back, Beta aside
            self.assertTrue(os.path.exists(os.path.join(plugins, "Alpha", "saves", "slot1.json")), "kept folder")
            self.assertFalse(os.path.exists(os.path.join(plugins, "Alpha", "stale.dll")), "old files go")
            self.assertTrue(os.path.exists(os.path.join(plugins, "Alpha", "Alpha.PR.dll")))
            self.assertTrue(os.path.isdir(os.path.join(shelf, "Beta")))
            script = deploy.Deployer(a, out=self.log.append).deck_script(full=True)
        self.assertIn("--exclude=BepInEx/plugins/Alpha/saves", script)
        self.assertIn("mv BepInEx/plugins/Beta BepInEx/mod-shelf/Beta", script)
        self.assertNotIn("D:/", script.split("REMOTE")[0].split("tar -cf")[1] if "tar -cf" in script else "")
        try:
            bash = deploy.bash_exe()
        except deploy.DeployError:
            return
        r = subprocess.run([bash, "-n"], input=script, text=True, capture_output=True)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_msys_path(self):
        self.assertEqual(deploy.msys_path(r"D:\SteamLibrary\x y"), "/d/SteamLibrary/x y")
        self.assertEqual(deploy.msys_path("/home/deck"), "/home/deck")

    def test_own_deploy_is_forwarded(self):
        out = []
        self.assertEqual(deploy.deploy("bnw", pc=True, deck=False, dry_run=True, out=out.append), 0)
        self.assertIn("tools/deploy.sh --bnw --pc", out[0])


class SetupAndRun(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="prkit_test_")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)
        games.forget()

    def test_setup_copies_the_framework(self):
        src = fake_game(os.path.join(self.tmp, "a"), "ff6")
        dst = fake_game(os.path.join(self.tmp, "b"), "ff5", bepinex_installed=False)
        work = os.path.join(self.tmp, "ws")
        with mock.patch.dict(os.environ, {"PRKIT_FF6_DIR": src, "PRKIT_FF5_DIR": dst}), \
                mock.patch.object(games.Game, "running", return_value=False), \
                mock.patch.object(games.Game, "workspace", new_callable=mock.PropertyMock, return_value=work):
            games.forget()
            log = []
            self.assertEqual(bepinex.setup("ff5", "ff6", out=log.append), 0)
            for f in ["BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/plugins/Magicite.dll", "dotnet/coreclr.dll", "winhttp.dll"]:
                self.assertTrue(os.path.exists(os.path.join(dst, *f.split("/"))), f)
            with open(os.path.join(work, "install-files.txt"), encoding="utf-8") as f:
                self.assertIn("./BepInEx/core/BepInEx.Unity.IL2CPP.dll", f.read())
            log.clear()
            self.assertEqual(bepinex.setup("ff5", "ff6", out=log.append), 0)
            self.assertIn("already has BepInEx", log[0])

    def test_run_dry(self):
        g = games.get("ff6")
        if not g.installed or not g.status().get("bepinex"):
            self.skipTest("FF6 PR with BepInEx isn't installed here")
        log = []
        self.assertEqual(headless.run("ff6", harness=True, minutes=5, settings={"x": "1"}, dry_run=True, out=log.append), 0)
        self.assertIn("harness=1", log[0])
        self.assertIn("minutes=5", log[0])
        self.assertIn("-batchmode -nographics", log[0])
        self.assertTrue(os.path.exists(g.exe))

    def test_run_refuses_without_the_game(self):
        with mock.patch.dict(os.environ, {"PRKIT_FF5_DIR": os.path.join(self.tmp, "nowhere")}):
            games.forget()
            log = []
            self.assertEqual(headless.run("ff5", dry_run=True, out=log.append), 1)
            self.assertIn("isn't installed", log[0])


@unittest.skipUnless(os.environ.get("PRKIT_BUILD") == "1", "PRKIT_BUILD=1 builds a scaffolded mod with dotnet")
class ScaffoldBuilds(unittest.TestCase):
    def test_dotnet_build(self):
        if not games.get("ff6").status().get("interop"):
            self.skipTest("no FF6 interop to build against")
        with tempfile.TemporaryDirectory(prefix="prkit_build_") as tmp:
            scaffold.new_mod("BuildCheck", "ff6", root=tmp, kit_dir=os.path.join(PR, "plugin", "PrKit"), out=lambda s: None)
            r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"],
                               cwd=os.path.join(tmp, "plugin", "BuildCheckPlugin"), capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stdout[-3000:])
            self.assertTrue(os.path.exists(os.path.join(tmp, "plugin", "BuildCheckPlugin", "bin", "Release", "net6.0", "BuildCheck.PR.dll")))


if __name__ == "__main__":
    unittest.main()

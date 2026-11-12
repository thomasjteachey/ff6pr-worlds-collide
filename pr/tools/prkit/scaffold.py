"""Start a new mod: kit.py new-mod <Name> --game ff5 [--title "..."] [--id x] [--no-data] [--no-plugin]

Makes, under pr/:
    plugin/<Name>Plugin/<Name>Plugin.csproj   the plugin (imports plugin/PrKit/PrKit.props)
    plugin/<Name>Plugin/Plugin.cs             its entry point: Kit, FeatureHost, the runner
    plugin/<Name>Plugin/ExampleFeature.cs     a feature to copy (config switch, hook, check)
    tools/data_<id>.py                        its game data -> build/<Name>/magicite (Magicite)
    tests/test_<id>.py                        its Magicite folder loads (pr/tests runs it)
and adds <id> to pr/modkit.json, so kit.py deploy/package/check work at once.
"""
import os
import re

from . import PR, games, mods

PLUGIN_CS = r'''using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PrKit;
using UnityEngine;

namespace {ns}
{{
    // {title}: a {game_title} Pixel Remaster mod made with PrKit (pr/MODDING.md).
    // Its parts: this plugin (plugin/{name}Plugin), its game data (tools/data_{id}.py writes the
    // Magicite folder build/{name}/magicite) and its entry in pr/modkit.json.
    //   build + install on the PC:  .venv/Scripts/python tools/kit.py deploy {id} --pc
    //   player zip:                 .venv/Scripts/python tools/kit.py package {id}
    [BepInPlugin(Guid, "{title}", Version)]
    public class Plugin : BasePlugin
    {{
        public const string Guid = "prmod.{id}";
        public const string Version = "0.1.0";
        internal static FeatureHost Features;

        public override void Load()
        {{
            Kit.Init(Log, "{title}");
            // built against {game_title}'s code: in another PR it stays off
            if (!Game.Require(PrGame.{game_enum})) return;
{magicite_check}            Features = new FeatureHost(Config, new Harmony(Guid));
            Features.Add(new ExampleFeature());
            // Features.Add(new SingleGamepad());   // the kit's Steam Deck double-press fix
            Features.ApplyAll();
            AddComponent<{name}Runner>();
        }}
    }}

    // Unity calls this every frame; it ticks the features. Every plugin needs its own runner class
    // with its own name: IL2CPP registers injected classes by name, two of one name would clash.
    public class {name}Runner : MonoBehaviour
    {{
        public {name}Runner(IntPtr ptr) : base(ptr) {{ }}
        void Update() => Plugin.Features?.Update();
        void OnGUI() => Plugin.Features?.OnGUI();
    }}
}}
'''

MAGICITE_CHECK = '''            // its rules belong on its own data (tools/data_{id}.py): without its Magicite folder, stay off
            if (!MagiciteDir.Has("{name}"))
            {{
                Kit.Warn($"{name}'s data isn't installed (no {name} folder in {{MagiciteDir.ImportDir}}): off");
                return;
            }}
'''

FEATURE_CS = r'''using HarmonyLib;
using Last.Data.Master;
using PrKit;

namespace {ns}
{{
    // A feature: one switch in BepInEx/config/prmod.{id}.cfg ([General] Example), its hooks, its
    // per-frame work and a check that logs what it found once the game's data has loaded. Copy this
    // file for each thing the mod changes and add it in Plugin.Load (Features.Add).
    class ExampleFeature : Feature
    {{
        public ExampleFeature() : base("General", "Example", true,
            "An example: logs the first monster's HP once the game's data has loaded.") {{ }}

        public override void Apply(Harmony harmony)
        {{
            // Hook a game method (find one in the game's API dump, kit.py api {game}):
            // Hooks.Patch(harmony, typeof(Last.Battle.BattleProgressATB), "ActExectionEndDelegate",
            //     typeof(ExampleFeature), postfix: nameof(ActEnd_Postfix));
        }}

        // public static void ActEnd_Postfix(Last.Battle.BattleProgressATB __instance)
        // {{
        //     if (!Hooks.IsA<Last.Battle.BattleProgressATB>(__instance)) return;   // always: shared code
        //     ...
        // }}

        public override string Check()
        {{
            var monsters = Master.All<Monster>();
            var first = Master.Get<Monster>(1);
            if (monsters == null || first == null) return null;   // not loaded yet: asked again
            return $"{{monsters.Count}} monsters; monster 1 '{{GameText.Get(first.MesIdName)}}' has {{first.Hp}} HP";
        }}
    }}
}}
'''

CSPROJ = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PrGame>{game}</PrGame>
    <AssemblyName>{name}.PR</AssemblyName>
    <RootNamespace>{ns}</RootNamespace>
  </PropertyGroup>
  <!-- the kit: plugin/PrKit/*.cs, the game's BepInEx + interop references (pr/MODDING.md) -->
  <Import Project="{props}" />
</Project>
'''

DATA_PY = '''"""{title}: the game data this mod changes, as a Magicite folder (build/{name}/magicite).

    .venv/Scripts/python tools/data_{id}.py       (kit.py deploy / package run it first)

Change the game's tables and text below: only rows that change are written. The game's own data is
in {vanilla_rel} (kit.py extract {game} makes it); pr/MODDING.md lists the usual tables.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prkit import PR, data, magicite  # noqa: E402

OUT = os.path.join(PR, "build", "{name}", "magicite")


def build(game):
    mod = magicite.MagiciteMod(OUT, game, keys_name="{id}")
    # Examples:
    # items = mod.table("item")
    # items.set(1, "buy", 25)                          # item 1 costs 25 gil
    # mod.text()["MSG_ITEM_NAME_1"] = "Potion+"        # names live in message/system_en.txt
    # monsters = mod.table("monster")
    # for row in monsters:                             # every monster: double HP
    #     monsters.set(int(row["id"]), "hp", int(row["hp"]) * 2)
    return mod


def main():
    game = data.GameData("{game}")
    mod = build(game)
    written = mod.write()
    problems, notes = magicite.validate(OUT, game if game.extracted else None)
    print(f"{{OUT}}: {{written or 'nothing changed yet'}}")
    for p in problems:
        print("PROBLEM", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
'''

TEST_PY = '''"""{title}: its parts are where pr/modkit.json says and its Magicite folder loads."""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
from prkit import data, magicite, mods  # noqa: E402


class {cls}(unittest.TestCase):
    def setUp(self):
        self.mod = mods.get("{id}")

    def test_magicite_folder_loads(self):
        if not self.mod.magicite:
            self.skipTest("no Magicite data")
        folder = self.mod.path(self.mod.magicite)
        if not os.path.isdir(folder):
            self.skipTest("not built yet: .venv/Scripts/python tools/data_{id}.py")
        game = data.GameData(self.mod.game_id)
        problems, _ = magicite.validate(folder, game if game.extracted else None)
        self.assertEqual(problems, [])

    def test_plugin_project(self):
        if self.mod.plugin_project:
            self.assertTrue(os.path.isdir(self.mod.path(self.mod.plugin_project)))


if __name__ == "__main__":
    unittest.main()
'''


def check_name(name):
    if not re.fullmatch(r"[A-Z][A-Za-z0-9]*", name or ""):
        raise ValueError("the mod's name is its folder, assembly and namespace: letters and digits, "
                         "starting with a capital (e.g. FiveJobFiesta)")
    return name


def new_mod(name, game_id, title=None, mod_id=None, with_data=True, with_plugin=True, root=PR, kit_dir=None, out=print):
    name = check_name(name)
    g = games.get(game_id)
    mod_id = (mod_id or name.lower()).strip()
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", mod_id):
        raise ValueError("the id is lower-case letters, digits, - and _")
    if mod_id in mods.load(root):
        raise KeyError(f"{mod_id} is already in modkit.json")
    title = title or f"{name} for {g.title} Pixel Remaster"
    plugin_dir = os.path.join(root, "plugin", f"{name}Plugin")
    files = {}
    if with_plugin:
        if os.path.exists(plugin_dir):
            raise FileExistsError(plugin_dir)
        kit = kit_dir or os.path.join(root, "plugin", "PrKit")
        props = os.path.relpath(os.path.join(kit, "PrKit.props"), plugin_dir).replace("/", "\\")
        fmt = dict(name=name, ns=name, id=mod_id, title=title.replace('"', "'"), game=g.id, game_title=g.title,
                   game_enum=g.id.upper(), props=props)
        fmt["magicite_check"] = MAGICITE_CHECK.format(**fmt) if with_data else ""
        files[os.path.join(plugin_dir, f"{name}Plugin.csproj")] = CSPROJ.format(**fmt)
        files[os.path.join(plugin_dir, "Plugin.cs")] = PLUGIN_CS.format(**fmt)
        files[os.path.join(plugin_dir, "ExampleFeature.cs")] = FEATURE_CS.format(**fmt)
    if with_data:
        vanilla_rel = "pr/" + os.path.relpath(g.vanilla, PR).replace("\\", "/")
        files[os.path.join(root, "tools", f"data_{mod_id.replace('-', '_')}.py")] = DATA_PY.format(
            name=name, id=mod_id, title=title, game=g.id, vanilla_rel=vanilla_rel)
    cls = "".join(p.capitalize() for p in re.split(r"[-_]", mod_id)) + "Mod"
    files[os.path.join(root, "tests", f"test_{mod_id.replace('-', '_')}.py")] = TEST_PY.format(title=title, id=mod_id, cls=cls)
    for path in files:
        if os.path.exists(path):
            raise FileExistsError(path)
    for path, text in files.items():
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        out(f"[new-mod] wrote {os.path.relpath(path, root)}")
    doc = {"name": name, "game": g.id, "title": title, "version": "0.1.0"}
    if with_plugin:
        doc["plugin"] = {"project": f"plugin/{name}Plugin",
                         "files": [[f"plugin/{name}Plugin/bin/Release/net6.0/{name}.PR.dll", f"{name}.PR.dll"]]}
    if with_data:
        doc["magicite"] = f"build/{name}/magicite"
        doc["build"] = [["{python}", f"tools/data_{mod_id.replace('-', '_')}.py"]]
    mods.add(mod_id, doc, root)
    out(f"[new-mod] added {mod_id} to modkit.json")
    out(f"[new-mod] next: .venv/Scripts/python tools/kit.py deploy {mod_id} --pc   (builds it, installs it; the game closed)")
    if not g.installed:
        out(f"[new-mod] {g.title} PR isn't installed here yet: kit.py games / setup {g.id} first")
    return mod_id

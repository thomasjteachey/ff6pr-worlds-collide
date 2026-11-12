"""ONE-OFF (delete after use): move WcPlugin onto the modding kit (plugin/PrKit), 2026-11-11.

Agreed with the session working on WC, which asked for it to wait until it says "WcPlugin free"
(it edits WcSeeds.cs/WcTest.cs across turns; no git, so overlapping edits clobber). Then:

    .venv/Scripts/python tools/prkit/migrate_wc.py plugin/WcPlugin --check
    .venv/Scripts/python tools/prkit/migrate_wc.py plugin/WcPlugin
    cd plugin/WcPlugin && dotnet build -c Release      (then Wc.Tests, WcAi.Tests, pr/tests)
    delete plugin/Shared (nothing compiles it after this) and this file; pr/MODDING.md "Where BNW
    and WC stand" and PORTING.md's Plugin notes get the result.

Every change is found by its exact text; anything not found stops the script before a file is
written (--check only reports). LiveData.cs and PadFix.cs are deleted last.
"""
import os
import re
import sys

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PrGame>ff6</PrGame>
    <AssemblyName>WorldsCollide.PR</AssemblyName>
  </PropertyGroup>
  <!-- the modding kit (plugin/PrKit/*.cs: LiveData, MagiciteDir, SingleGamepad, ...) and the game's
       BepInEx + interop references; GameDir comes from plugin/PrKit/Games.props -->
  <Import Project="..\\PrKit\\PrKit.props" />
  <ItemGroup>
    <None Include="wc_flags.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <!-- dotnet build -c Release -p:Deploy=true  (game must be closed). The generator bundle and
       the Magicite baseline come from tools/wc_bundle.py (deploy.sh runs it). -->
  <Target Name="Deploy" AfterTargets="Build" Condition="'$(Deploy)' == 'true'">
    <Copy SourceFiles="$(TargetPath);wc_flags.json" DestinationFolder="$(GameDir)\\BepInEx\\plugins\\WorldsCollide" />
  </Target>
</Project>
"""

PADFIX_BIND = re.compile(
    r'            PadFix\.Enabled = Config\.Bind\("Input", "SingleGamepad", true,\n'
    r'(?:.*\n)*?.*"\)\.Value;\n')
MUTE = re.compile(r"        static void Mute\(\)\n        \{\n"
                  r"            try \{ SEAD\.API\.seadSetMasterVolume\(0f, 0f\); \} catch \{ \}\n"
                  r"            try \{ AudioListener\.volume = 0f; \} catch \{ \}\n        \}\n")


class Edits:
    def __init__(self, folder):
        self.folder = folder
        self.files = {}
        self.original = {}
        self.nl = {}
        self.problems = []

    def text(self, name):
        if name not in self.files:
            with open(os.path.join(self.folder, name), encoding="utf-8", newline="") as f:
                raw = f.read()
            self.nl[name] = "\r\n" if "\r\n" in raw else "\n"
            self.files[name] = self.original[name] = raw.replace("\r\n", "\n")
        return self.files[name]

    def changed(self):
        """Only the files an edit changed: the others are never rewritten (another session edits them)."""
        return {n: t for n, t in self.files.items() if t != self.original[n]}

    def replace(self, name, old, new, count=1):
        t = self.text(name)
        if t.count(old) != count:
            self.problems.append(f"{name}: expected {count}x {old.strip()[:70]!r}, found {t.count(old)}")
            return
        self.files[name] = t.replace(old, new)

    def sub(self, name, pattern, new):
        t = self.text(name)
        new_t, n = pattern.subn(new, t)
        if n != 1:
            self.problems.append(f"{name}: pattern {pattern.pattern[:60]!r} matched {n} times")
            return
        self.files[name] = new_t


def main(folder, check=False):
    e = Edits(folder)
    # Plugin.cs: the kit's namespace, its log, the version, SingleGamepad as a kit feature
    e.replace("Plugin.cs", "using Ff6Mod.Shared;\n", "using PrKit;\n")
    e.replace("Plugin.cs", "            LiveData.Info = s => L.LogInfo(s);\n            LiveData.Warn = s => L.LogWarning(s);\n",
              "            Kit.Init(Log, \"Worlds Collide\");   // the modding kit (plugin/PrKit): LiveData, MagiciteDir, SingleGamepad\n")
    e.sub("Plugin.cs", PADFIX_BIND, "")
    e.replace("Plugin.cs", "        internal static bool WcMode;\n",
              "        internal static bool WcMode;\n        internal static FeatureHost Features;   // the kit's features (SingleGamepad)\n")
    e.replace("Plugin.cs", "            var harmony = new Harmony(Guid);\n",
              "            var harmony = new Harmony(Guid);\n"
              "            // one controller, one input on the Steam Deck ([Input] SingleGamepad; was WC's PadFix)\n"
              "            Features = new FeatureHost(Config, harmony);\n"
              "            Features.Add(new SingleGamepad());\n"
              "            Features.ApplyAll();\n")
    e.replace("Plugin.cs", '                PadFix.Update(); t = WcPerf.Lap(0, "padfix", t);\n',
              '                Plugin.Features?.Update(); t = WcPerf.Lap(0, "kit", t);\n')
    # (WC's version stays the WC session's to bump)
    # the other files: the kit's namespace; WcTest's device check and mute through the kit
    for name in ("WcScreen.cs", "WcSeeds.cs", "WcTest.cs"):
        e.replace(name, "using Ff6Mod.Shared;\n", "using PrKit;\n")
    e.replace("WcTest.cs", "PadFix.Check()", "(Plugin.Features?.Get<SingleGamepad>()?.Scan() ?? \"\")")
    e.sub("WcTest.cs", MUTE, "        static void Mute() => TestRun.Mute();   // the modding kit's\n")
    # nothing else may still use what's going away
    for name in os.listdir(folder):
        if not name.endswith(".cs") or name in ("LiveData.cs", "PadFix.cs"):
            continue
        t = e.files.get(name) or e.text(name)
        for gone in ("PadFix.", "Ff6Mod.Shared", "LiveData.Info", "LiveData.Warn"):
            if gone in t:
                e.problems.append(f"{name}: still uses {gone}")
    if e.problems:
        print("NOT applied:\n  " + "\n  ".join(e.problems))
        return 1
    changed = e.changed()
    if check:
        print(f"check: all edits apply ({', '.join(sorted(changed))}, the csproj; LiveData.cs and PadFix.cs to delete)")
        return 0
    for name, t in changed.items():
        with open(os.path.join(folder, name), "w", encoding="utf-8", newline="") as f:
            f.write(t.replace("\n", e.nl[name]))
    with open(os.path.join(folder, "WcPlugin.csproj"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CSPROJ)
    for gone in ("LiveData.cs", "PadFix.cs"):
        os.remove(os.path.join(folder, gone))
    print(f"applied: {', '.join(sorted(changed))}, WcPlugin.csproj; deleted LiveData.cs, PadFix.cs")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], "--check" in sys.argv))

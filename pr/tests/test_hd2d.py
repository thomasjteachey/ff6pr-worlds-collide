"""Offline checks for the HD-2D shader bundle (pr/hd2d/out/hd2d.bundle).

The game is Unity 2019.4.26f1; a bundle from another minor version, a missing pass or a
missing D3D11 program would only show up in game as a pink screen or a load error.

    pr/.venv/Scripts/python -m unittest pr/tests/test_hd2d.py -v
"""
import os
import unittest

import UnityPy
from UnityPy.enums import ClassIDType

HERE = os.path.dirname(os.path.abspath(__file__))
BUNDLE = os.path.join(HERE, "..", "hd2d", "out", "hd2d.bundle")
SHADER_SRC = os.path.join(HERE, "..", "hd2d", "unity", "Assets", "HD2D", "HD2DPost.shader")
PLUGIN_SRC = os.path.join(HERE, "..", "plugin", "Hd2dPlugin", "Hd2dRenderer.cs")

D3D11, GLCORE, VULKAN = 4, 15, 18  # UnityEditor.Rendering.ShaderCompilerPlatform
PASSES = ["Upscale", "Down", "Final", "Backdrop"]  # in shader order; the plugin's PassX constants


@unittest.skipUnless(os.path.exists(BUNDLE), "build it first: pr/hd2d/build_bundle.sh")
class Hd2dBundle(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.env = UnityPy.load(BUNDLE)
        cls.shaders = [o.read_typetree() for o in cls.env.objects if o.type == ClassIDType.Shader]

    def test_unity_version_matches_game(self):
        for f in self.env.files.values():
            for sf in getattr(f, "files", {}).values():
                if hasattr(sf, "unity_version"):
                    self.assertTrue(sf.unity_version.startswith("2019.4."), sf.unity_version)

    def shader(self, name):
        found = [s for s in self.shaders if s["m_ParsedForm"]["m_Name"] == name]
        self.assertEqual(1, len(found), f"{name} in the bundle")
        return found[0]

    def test_shaders_named_like_the_plugin_expects(self):
        names = sorted(s["m_ParsedForm"]["m_Name"] for s in self.shaders)
        self.assertEqual(["Hidden/HD2D/Post", "Hidden/HD2D/Shadow"], names)

    def test_post_has_its_passes(self):
        sub = self.shader("Hidden/HD2D/Post")["m_ParsedForm"]["m_SubShaders"]
        self.assertEqual(1, len(sub))
        self.assertEqual(len(PASSES), len(sub[0]["m_Passes"]))

    def test_shadow_draws_between_ground_and_characters(self):
        sub = self.shader("Hidden/HD2D/Shadow")["m_ParsedForm"]["m_SubShaders"][0]
        tags = {t[0]: t[1] for t in sub["m_Tags"]["tags"]}
        self.assertEqual("AlphaTest+2", tags.get("QUEUE"))  # the game's own field shadows' queue
        self.assertEqual(1, len(sub["m_Passes"]))
        state = sub["m_Passes"][0]["m_State"]
        self.assertEqual(0, state["zWrite"]["val"])
        self.assertEqual(4, state["zTest"]["val"])  # LEqual: hidden under roofs like the characters

    def test_compiled_for_d3d11(self):
        for s in self.shaders:
            platforms = s["platforms"]
            self.assertIn(D3D11, platforms)  # Windows, and the Deck through Proton/DXVK
            self.assertIn(VULKAN, platforms)
            self.assertIn(GLCORE, platforms)
            for i, size in enumerate(s["compressedLengths"]):
                sizes = size if isinstance(size, list) else [size]
                self.assertTrue(all(n > 0 for n in sizes), f"empty program blob for platform {platforms[i]}")

    def test_pass_numbers_match_plugin(self):
        # the plugin refers to passes by number; keep both sides in sync
        with open(SHADER_SRC, encoding="utf-8") as f:
            shader = f.read()
        with open(PLUGIN_SRC, encoding="utf-8") as f:
            plugin = f.read()
        for n, name in enumerate(PASSES):
            self.assertIn(f"#pragma fragment Frag{name}", shader)
            self.assertIn(f"Pass{name} = {n}", plugin)


if __name__ == "__main__":
    unittest.main()

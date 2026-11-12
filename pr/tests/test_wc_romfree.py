"""The ROM-free Worlds Collide generator against the real-ROM one (PORTING.md "WC without an
SNES ROM"): tools/wc_seed.py with FF6_WC_ROMFREE=1 (WC's input from tools/wc_basedata.py's
shipped data, tools/wc_romfree.py's stand-ins for what WC only rewrites) must give the seed
folder the real ROM gives, and never write a ROM. Needs the vanilla ROM (skipped without it).

  pr/.venv/Scripts/python -m unittest discover -s pr/tests -p "test_wc_romfree.py" -v
"""
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
TOOLS = os.path.join(PR, "tools")
WC = os.path.join(PR, "..", "mods", "worlds-collide")
sys.path.insert(0, TOOLS)
import prdata as P  # noqa: E402
import wc_basedata  # noqa: E402
import wc_parity  # noqa: E402


@unittest.skipUnless(os.path.exists(P.VANILLA_ROM), "no vanilla ROM")
class WcRomFree(unittest.TestCase):
    def test_shipped_data_is_the_roms(self):
        """data/wc_base.bin holds the ROM's own bytes in every region it ships (built from it)."""
        self.assertTrue(os.path.exists(wc_basedata.PATH), "run tools/wc_basedata.py build")
        self.assertEqual(wc_basedata.check(), 0)

    def test_same_seed_folder_as_with_the_rom(self):
        """Both presets: every table, message, file, the seed info and WC's log the same."""
        for seed, preset, flags in (("romfree1", "moogle cup", []), ("romfree2", "atma series s1", ["-sl"])):
            tmp = tempfile.mkdtemp(prefix="wc_romfree_")
            try:
                a, b = os.path.join(tmp, "rom"), os.path.join(tmp, "free")
                wc_parity.make(seed, preset, flags, a, False)
                wc_parity.make(seed, preset, flags, b, True)
                self.assertEqual(wc_parity.compare(a, b), [], f"{seed} / {preset}")
            finally:
                shutil.rmtree(tmp, ignore_errors=True)

    def test_writes_no_rom(self):
        """WC's ROM-free run leaves its working bytes compressed (not an .sfc), and nothing else
        it writes is a ROM."""
        tmp = tempfile.mkdtemp(prefix="wc_romfree_out_")
        try:
            flags = json.load(open(os.path.join(PR, "..", "mods", "worlds-collide-presets.json"), encoding="utf-8"))["moogle cup"]
            out = os.path.join(tmp, "wc_out.bin")
            env = dict(os.environ, WC_ROMFREE="1", WC_BASE=wc_basedata.PATH, PYTHONIOENCODING="utf-8")
            p = subprocess.run([sys.executable, os.path.join(TOOLS, "wc_run.py"), os.path.join(tmp, "dump.json"),
                                "-i", wc_basedata.PATH, "-o", out, "-s", "romfree3"] + flags.split(),
                               cwd=WC, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
            self.assertEqual(p.returncode, 0, p.stderr[-500:])
            with open(out, "rb") as f:
                self.assertEqual(f.read(8), b"FF6WCO1\0")
            self.assertEqual(glob.glob(os.path.join(tmp, "**", "*.sfc"), recursive=True)
                             + glob.glob(os.path.join(tmp, "**", "*.smc"), recursive=True), [])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()

"""The project's tooling for every Pixel Remaster: .venv/Scripts/python tools/kit.py --help

(tools/prkit is the package; pr/MODDING.md the guide.)
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prkit.__main__ import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())

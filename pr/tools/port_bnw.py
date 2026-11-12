"""Brave New World port (kept for convenience): same as `tools/port.py bnw`."""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import port  # noqa: E402
import profiles  # noqa: E402

if __name__ == "__main__":
    port.main(profiles.PROFILES["bnw"])

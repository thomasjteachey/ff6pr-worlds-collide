"""Put the modding kit's tools (prdata, the porter, ...) on sys.path for a mod repository's own tools.

The mod repositories (ff6pr-worlds-collide, ff6pr-brave-new-world) pin the kit
(github.com/thomasjteachey/ff6pr-kit) as the git submodule kit/, so its tools are in
kit/pr/tools next to the repository's own pr/tools. In the project they share one folder, and in
the Worlds Collide generator bundle every tool sits in generator/tools: then this does nothing.
Every Worlds Collide and Brave New World tool imports it before any kit module.
"""
import os
import sys

TOOLS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "kit", "pr", "tools"))
if not os.path.isdir(TOOLS):
    TOOLS = None
elif TOOLS not in sys.path:
    sys.path.insert(1, TOOLS)

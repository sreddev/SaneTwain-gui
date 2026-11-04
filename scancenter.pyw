"""Launcher used by the shortcut and by "Start with Windows" (runs without a console)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sanescan.__main__ import main  # noqa: E402

main()

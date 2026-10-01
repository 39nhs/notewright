#!/usr/bin/env python3
"""notewright plugin entry point: python notewright.py -h"""
import sys
from pathlib import Path

if sys.version_info < (3, 9):
    sys.exit("notewright needs Python 3.9 or newer")
sys.path.insert(0, str(Path(__file__).resolve().parent))

from notewright.cli import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())

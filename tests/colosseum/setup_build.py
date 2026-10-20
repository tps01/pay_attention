"""Suite setup: build Release binaries and prepare an isolated app-data root."""

from __future__ import annotations

import os

import colosseum as col
from colosseum.logging import get_logger

from support_captures import ensure_built, load_gui, make_appdata

_log = get_logger("pay_attention.colosseum")


def main() -> None:
    from support_captures import require_live_execution

    require_live_execution()
    ensure_built()
    root = make_appdata()
    os.environ["PAY_ATTENTION_APPDATA"] = str(root)
    load_gui()
    _log.info("appdata=%s", root)


if __name__ == "__main__":
    main()
    col.endex()

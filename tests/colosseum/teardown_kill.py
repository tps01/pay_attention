"""Suite teardown: stop child processes and remove temp app data."""

from __future__ import annotations

import colosseum as col

from support_captures import cleanup_appdata


def main() -> None:
    try:
        from colosseum_gui.connections import close_all

        close_all()
    except Exception:  # noqa: BLE001
        pass
    cleanup_appdata()


if __name__ == "__main__":
    main()
    col.endex()

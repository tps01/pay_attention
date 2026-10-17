"""FlaUI launch with ``args`` as a TOML list, including a title that contains spaces.

The desktop backend quotes that list for ``Application.Launch``. A broken
quoter splits the title and the window never appears under the expected name.

Suite scripts share one process, so this restores ``gui.toml`` and the loaded
config before returning. Later cases still launch the PaFixture harness.
"""

from __future__ import annotations

import os

import colosseum as col
from colosseum_gui.connections import close_all, get_desktop

from support_captures import (
    _toml_path,
    appdata,
    ensure_built,
    harness_exe,
    make_appdata,
)

TITLE = "Pa List Args"
PHRASE = "spaced launch phrase"


def _toml_string(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def main() -> None:
    if "PAY_ATTENTION_APPDATA" not in os.environ:
        make_appdata()
        os.environ["PAY_ATTENTION_APPDATA"] = str(appdata())
    ensure_built()

    argv = [
        "fixture",
        "--phrase",
        PHRASE,
        "--title",
        TITLE,
        "--x",
        "80",
        "--y",
        "80",
        "--w",
        "420",
        "--h",
        "320",
        "--easy",
    ]
    cfg = appdata() / "gui.toml"
    previous = cfg.read_text(encoding="utf-8") if cfg.is_file() else None
    args_toml = ", ".join(_toml_string(part) for part in argv)
    try:
        cfg.write_text(
            "\n".join(
                [
                    "[[gui.desktop]]",
                    "desktop_id = 2",
                    'driver = "flaui"',
                    f'exe = "{_toml_path(harness_exe())}"',
                    f"args = [{args_toml}]",
                    f"title = {_toml_string(TITLE)}",
                    "timeout_s = 30",
                    "",
                ],
            ),
            encoding="utf-8",
        )
        col.config.load_config(str(cfg), force=True)
        col.gui.desktop.connect(desktop_id=2)
        col.gui.desktop.wait_stable(desktop_id=2, timeout_s=5.0)
        window_name = str(get_desktop(2)._window.Name or "")
        if window_name != TITLE:
            raise AssertionError(
                f"FlaUI window title {window_name!r} != {TITLE!r}; "
                "list args were not passed as one command line",
            )
    finally:
        close_all()
        if previous is not None:
            cfg.write_text(previous, encoding="utf-8")
            col.config.load_config(str(cfg), force=True)


if __name__ == "__main__":
    main()
    col.endex()

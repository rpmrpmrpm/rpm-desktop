"""
Catppuccin Mocha.

Not invented here, deliberately. A diagnostic tool is read for a long time under bad conditions, and
the thing being judged on screen must be the data rather than somebody's colour sense. Mocha is a
published, widely used dark palette with the contrast ratios already worked out and, more usefully
here, a set of accents that stay distinguishable from each other at the size of a two-pixel bar.

https://github.com/catppuccin/catppuccin — Mocha flavour.

Roles are assigned once, below, so a lane's colour is a decision recorded in one place rather than a
hex code chosen at each call site.
"""

from __future__ import annotations

# --- the flavour, verbatim -----------------------------------------------------------------

CRUST = "#11111b"
MANTLE = "#181825"
BASE = "#1e1e2e"
SURFACE0 = "#313244"
SURFACE1 = "#45475a"
SURFACE2 = "#585b70"
OVERLAY0 = "#6c7086"
OVERLAY1 = "#7f849c"
OVERLAY2 = "#9399b2"
SUBTEXT0 = "#a6adc8"
SUBTEXT1 = "#bac2de"
TEXT = "#cdd6f4"

ROSEWATER = "#f5e0dc"
FLAMINGO = "#f2cdcd"
PINK = "#f5c2e7"
MAUVE = "#cba6f7"
RED = "#f38ba8"
MAROON = "#eba0ac"
PEACH = "#fab387"
YELLOW = "#f9e2af"
GREEN = "#a6e3a1"
TEAL = "#94e2d5"
SKY = "#89dceb"
SAPPHIRE = "#74c7ec"
BLUE = "#89b4fa"
LAVENDER = "#b4befe"

# --- what each one means here --------------------------------------------------------------

#: The page, the panels, and the lines between them.
GROUND = BASE
PANEL = MANTLE
EDGE = SURFACE0
GRID = SURFACE0

#: Text, in three weights of attention.
SAID = TEXT
QUIET = SUBTEXT0
FAINT = OVERLAY1

#: The thing the tool is for. A stall is the loudest colour on the screen and nothing else uses it.
STALL = RED

#: Somebody being made to wait, and the thing making them wait.
WAITING = MAUVE
HOLDING = PEACH

#: The user. Deliberately the coolest accent, because it is a reference mark rather than a fault.
USER = SKY

#: The runtime's own doings.
SUSPEND = YELLOW
LOCK = LAVENDER
NETWORK = TEAL
THROW = MAROON

#: Anything the app said about itself that has no louder meaning.
WORK = BLUE

#: Selection.
PICKED = GREEN

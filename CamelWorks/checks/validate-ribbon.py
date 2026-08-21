#!/usr/bin/env python3
"""Check that the ribbon's three lists agree.

A command lives in three places: the ribbon markup that draws the button, the [Command] attribute
that registers it with the host, and the catalogue that routes it and feeds Find a Tool. Nothing in
the compiler connects them — a button present in two of the three is a button that either does
nothing, never appears, or cannot be found by search, and each of those looks like a different bug.

Also checks that every route a command points at is a workspace and tab that actually exist, since
a typo there sends a ribbon button to the wrong screen and nothing complains.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "CamelWorks.UI"

EXPECTED_COMMANDS = 25
EXPECTED_TABS = 16
EXPECTED_SWITCHER = 6


def read(path):
    return (UI / path).read_text(encoding="utf-8")


def fail(message):
    print("FAIL: " + message, file=sys.stderr)
    return 1


def main():
    problems = 0

    xaml = set(re.findall(r'Id="(ID_CW_\w+)"', read("CamelWorks.xaml")))
    plugin = set(re.findall(r'\[Command\("(ID_CW_\w+)"', read("CamelWorksPlugin.cs")))
    catalogue_source = read("Shell/CommandCatalogue.cs")
    catalogue = set(re.findall(r'"(ID_CW_\w+)"', catalogue_source))

    if len(xaml) != EXPECTED_COMMANDS:
        problems |= fail(f"the ribbon markup has {len(xaml)} buttons, expected {EXPECTED_COMMANDS}")

    for name, ids in (("ribbon markup", xaml), ("[Command] attributes", plugin), ("catalogue", catalogue)):
        missing = (xaml | plugin | catalogue) - ids
        if missing:
            problems |= fail(f"{name} is missing: {', '.join(sorted(missing))}")

    # Every route must name a workspace and a tab that exist.
    workspaces_source = read("Shell/Workspaces.cs")

    workspaces = re.findall(r'new Workspace\("(\w+)", "([^"]+)", (\w+), hasTabStrip: (true|false),(.*?)\)\),\n',
                            workspaces_source, re.S)

    tabs = {}
    switcher = 0
    tab_total = 0

    for wid, _title, pane, has_strip, body in workspaces:
        ids = re.findall(r'new WorkspaceTab\("([^"]+)"', body)
        tabs[wid] = ids
        if pane == "MainPane":
            switcher += 1
        if has_strip == "true":
            tab_total += len(ids)

    if switcher != EXPECTED_SWITCHER:
        problems |= fail(f"{switcher} switcher entries in the main pane, expected {EXPECTED_SWITCHER}")

    if tab_total != EXPECTED_TABS:
        problems |= fail(f"{tab_total} tabs across the tabbed workspaces, expected {EXPECTED_TABS}")

    for route in re.findall(r'CommandKind\.Pane, "([^"]+)"', catalogue_source):
        workspace, _, tab = route.partition("/")
        if workspace not in tabs:
            problems |= fail(f'route "{route}" names no workspace')
        elif tab and tab not in tabs[workspace]:
            problems |= fail(f'route "{route}" names no tab in {workspace}')

    # The catalogue's Pane entries carry their route as the third argument of the Pane helper, so
    # pull them from there too — a Pane command with no route would silently open nothing.
    pane_ids = re.findall(r'Pane\("(ID_CW_\w+)", "[^"]+", "([^"]+)"', catalogue_source)
    for cid, route in pane_ids:
        workspace, _, tab = route.partition("/")
        if workspace not in tabs:
            problems |= fail(f"{cid} routes to a workspace that does not exist: {route}")

    if problems:
        return 1

    print(f"ribbon consistent: {len(xaml)} commands, {switcher} switcher entries, {tab_total} tabs")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""
Regenerates the catalog-derived documentation:

  docs/COMMAND_INVENTORY.md   from catalog/milestonepstools-catalog.json
  SUPPORTED_COMMANDS.md       from the catalog plus catalog/gui-support.json

Usage: python3 tools/catalog/generate_docs.py [<repo-root>]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import report  # noqa: E402


def main(argv):
    root = os.path.abspath(argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(__file__), "..", ".."))
    with open(os.path.join(root, "catalog", "milestonepstools-catalog.json"), encoding="utf-8") as f:
        catalog = json.load(f)
    with open(os.path.join(root, "catalog", "gui-support.json"), encoding="utf-8") as f:
        support = json.load(f)
    names = {c["name"] for c in catalog["commands"]}
    listed = set(support.get("wizards", {})) | set(support.get("screens", {})) | set(support.get("appManagedImplemented", []))
    unknown = sorted(listed - names)
    if unknown:
        print("gui-support.json names commands that are not in the catalog: " + ", ".join(unknown), file=sys.stderr)
        return 1
    report.write_report(catalog, os.path.join(root, "docs", "COMMAND_INVENTORY.md"))
    report.write_supported_commands(catalog, support, os.path.join(root, "SUPPORTED_COMMANDS.md"))
    print("Wrote docs/COMMAND_INVENTORY.md and SUPPORTED_COMMANDS.md")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

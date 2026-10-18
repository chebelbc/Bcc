#!/usr/bin/env python3
"""
Classify a raw runtime catalog produced by tools/discovery/Export-PSToolsCatalog.ps1 and write the
inventory report, using the same rules as the static extractor.

Usage: python3 classify_catalog.py <raw-catalog.json> <classified-out.json> [<report.md>]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import classification  # noqa: E402
import report  # noqa: E402


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    with open(argv[1], encoding="utf-8-sig") as f:
        catalog = json.load(f)
    classification.apply_classification(catalog)
    with open(argv[2], "w", encoding="utf-8") as f:
        json.dump(catalog, f, indent=1, ensure_ascii=False)
        f.write("\n")
    if len(argv) > 3:
        report.write_report(catalog, argv[3])
    print(f"Classified {len(catalog['commands'])} commands")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

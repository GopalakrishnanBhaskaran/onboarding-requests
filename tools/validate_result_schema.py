#!/usr/bin/env python3
"""Validate review result JSON files against src/core/schema/review-result.v2.json.

Usage:
  python3 tools/validate_result_schema.py tests/fixtures/valid-result.json
  python3 tools/validate_result_schema.py --expect-invalid tests/fixtures/invalid-*.json

Exit code 0 when every file matches the expectation, 1 otherwise.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

SCHEMA = Path(__file__).resolve().parent.parent / "src" / "core" / "schema" / "review-result.v2.json"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="+", type=Path)
    ap.add_argument("--expect-invalid", action="store_true", help="succeed only if every file is INVALID")
    args = ap.parse_args()

    try:
        from jsonschema import Draft202012Validator
    except ImportError:
        print("pip install -r tools/requirements.txt", file=sys.stderr)
        return 2

    validator = Draft202012Validator(json.loads(SCHEMA.read_text(encoding="utf-8")))
    ok = True
    for path in args.files:
        try:
            errors = sorted(validator.iter_errors(json.loads(path.read_text(encoding="utf-8"))), key=lambda e: list(e.path))
            messages = [f"{'/'.join(map(str, e.path)) or '<root>'}: {e.message}" for e in errors]
        except json.JSONDecodeError as e:
            messages = [f"not valid JSON: {e.msg}"]
        valid = not messages
        status = "VALID" if valid else "INVALID"
        matched = valid != args.expect_invalid
        ok &= matched
        print(f"{'ok  ' if matched else 'FAIL'} {status:7} {path}")
        for m in messages[:5]:
            print(f"       {m}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

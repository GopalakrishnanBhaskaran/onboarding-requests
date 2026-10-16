#!/usr/bin/env python3
"""Build docs/PILOT_REPORT.md from the acceptance catalogue, recorded evidence and runner results.

Pass/fail comes ONLY from the test runner (CTRF JSON). Evidence lines are what each test observed.
A case with no runner result is reported as NOT RUN, never as passed.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import re
from pathlib import Path


def load_ctrf(path: Path) -> dict[str, str]:
    """Map case id (AC01...) or test name -> status from a CTRF report."""
    data = json.loads(path.read_text(encoding="utf-8"))
    out: dict[str, str] = {}
    for t in data.get("results", {}).get("tests", []):
        name = t.get("name", "")
        m = re.search(r"\b(AC\d{2})_", name)
        out[m.group(1) if m else name] = t.get("status", "unknown")
    return out


def ctrf_summary(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8")).get("results", {}).get("summary", {})


def load_evidence(path: Path) -> dict[str, dict]:
    ev: dict[str, dict] = {}
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                rec = json.loads(line)
                ev[rec["id"]] = rec
    return ev


def md(text: str) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cases", type=Path, required=True)
    ap.add_argument("--evidence", type=Path, required=True)
    ap.add_argument("--results", type=Path, required=True)
    ap.add_argument("--unit", type=Path)
    ap.add_argument("--e2e", type=Path)
    ap.add_argument("--commit", default="unknown")
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args()

    cases = json.loads(args.cases.read_text(encoding="utf-8"))["cases"]
    status = load_ctrf(args.results) if args.results.exists() else {}
    evidence = load_evidence(args.evidence)

    rows, gate_fail, any_fail = [], [], []
    for c in cases:
        st = status.get(c["id"], "not run")
        verdict = "PASS" if st == "passed" else "FAIL" if st == "failed" else "NOT RUN"
        if verdict != "PASS":
            any_fail.append(c["id"])
            if c["gate"]:
                gate_fail.append(c["id"])
        ev = evidence.get(c["id"], {})
        details = "; ".join(ev.get("evidence", []))
        rows.append(
            f"| {c['id']} | {md(c['case'])}{' **(gate)**' if c['gate'] else ''} | {md(c['expected'])} | "
            f"{md(ev.get('actual', '—'))} | {md(details) or '—'} | **{verdict}** |"
        )

    now = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    gates = [c for c in cases if c["gate"]]
    lines = [
        "# Pilot Report — PMO Request Review (zero-cost pilot)",
        "",
        f"Generated {now} from commit `{args.commit}` by `tools/run_acceptance.sh`. Environment: **local, in-process, SAMPLE data only** "
        "(in-memory store with Cosmos semantics; RuleOnly reviewer; Azure OpenAI path exercised only through a mocked HTTP handler). "
        "Nothing here was run against Azure.",
        "",
        "## Verdict",
        "",
        f"- Acceptance cases: **{len(cases) - len(any_fail)}/{len(cases)} passed**.",
        f"- Critical gates: **{len(gates) - len(gate_fail)}/{len(gates)} passed**"
        + (f" — failing: {', '.join(gate_fail)}" if gate_fail else "") + ".",
    ]
    if args.unit and args.unit.exists():
        s = ctrf_summary(args.unit)
        lines.append(f"- Unit, contract and security tests: **{s.get('passed', 0)}/{s.get('tests', 0)} passed** ({s.get('failed', 0)} failed, {s.get('skipped', 0)} skipped).")
    if args.e2e and args.e2e.exists():
        e2e = json.loads(args.e2e.read_text(encoding="utf-8")).get("stats", {})
        lines.append(f"- Browser end-to-end (Playwright via SWA CLI emulator, two mock users): **{e2e.get('expected', 0)} passed, {e2e.get('unexpected', 0)} failed**.")
    else:
        lines.append("- Browser end-to-end: not included in this run (run with `E2E=1` while `tools/run_local.sh` is up).")

    lines += [
        "",
        "## Critical gates",
        "",
        "| Gate | Covered by |",
        "|---|---|",
        "| Zero cross-project disclosure | AC10, AC11 |",
        "| Zero fulfilment actions | AC21 |",
        "| All seeded missing fields detected | AC22 |",
        "| Every citation resolves to a pinned version | AC18, AC23 |",
        "| Injection / forged approval non-executable | AC08, AC09, AC24 |",
        "| No result tampering | AC12 |",
        "",
        "## Cases",
        "",
        "| Id | Case | Expected | Actual | Evidence | Result |",
        "|---|---|---|---|---|---|",
        *rows,
        "",
        "## Not measured in this pilot",
        "",
        "- **90% reviewer agreement** and **25% time saving** targets need real reviewers and the AI model on real (non-SAMPLE) requests. "
        "They are measured after promotion to the paid environment, not here.",
        "- Live Azure behaviour (SWA Free gateway, Cosmos free tier, Entra sign-in, cost) is verified in Phase 6 after deployment approval.",
        "",
    ]
    args.out.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {args.out} — {len(cases) - len(any_fail)}/{len(cases)} passed, gates failing: {gate_fail or 'none'}")
    return 0 if not any_fail else 1


if __name__ == "__main__":
    raise SystemExit(main())

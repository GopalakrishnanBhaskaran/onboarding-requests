#!/usr/bin/env python3
"""Give Microsoft Entra groups a role on a Microsoft Fabric workspace, to test Entra-group access in Fabric.

Fabric reads Entra ID directly: no provisioning, sync app or account setup. A group with a workspace role gives every
member (also nested members) that role; the onboarding flow's group membership (agent or ID team) is all a joiner needs.
No Azure resource: the workspace runs on the free 60-day Fabric trial capacity (F122); a paid F capacity needs approval.

Without --approved-by it only reads: the workspace, its current role assignments and the groups (Entra, read-only),
then prints the plan. With --approved-by: adds each missing group with its role (Viewer by default; Contributor or Member
when asked; never Admin, which people give by hand), then reads the assignments back. It never changes or removes an
existing assignment (a group that already has a role keeps it).

  python3 tools/fabric_groups.py --workspace "PMO Fabric test" --group PH-SUPPORT-READER-DEV [--group <name|objectId>=Contributor]
  python3 tools/fabric_groups.py ... --approved-by "<name> <date>"
  python3 tools/fabric_groups.py --self-test

Needs: `az login` as a member account of the tenant with the Member or Admin role on that workspace (F121).
"""
from __future__ import annotations

import argparse
import os
import sys
import urllib.parse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from entra_rest import GUID, ApiError, entra_group, http, parse_group, record_approval, token  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FABRIC = "https://api.fabric.microsoft.com"
ROLES = ("Viewer", "Contributor", "Member")  # Admin is never given by this tool


def pages(url: str, bearer: str) -> list[dict]:
    """All `value` items of a Fabric list call (follows continuationUri)."""
    items: list[dict] = []
    while url:
        r = http("GET", url, bearer)
        items += r.get("value") or []
        url = r.get("continuationUri") or ""
    return items


def find_workspace(workspaces: list[dict], key: str) -> dict:
    hits = [w for w in workspaces if w.get("id", "").lower() == key.lower() or w.get("displayName", "").lower() == key.lower()]
    if len(hits) != 1:
        names = ", ".join(sorted(w.get("displayName", "") for w in workspaces)) or "none"
        raise SystemExit(f"Workspace {key!r}: {len(hits)} match(es). Workspaces you can see: {names}")
    return hits[0]


def plan_lines(groups: list[tuple[dict, str]], assignments: list[dict]) -> tuple[list[str], list[tuple[dict, str]]]:
    """(plan lines, groups to add). A group that already holds any role keeps it."""
    by_id = {str((a.get("principal") or {}).get("id", "")).lower(): a.get("role") for a in assignments}
    lines, add = [], []
    for g, role in groups:
        cur = by_id.get(g["id"].lower())
        if cur:
            note = "" if cur == role else f" (asked {role}; change it by hand if needed)"
            lines.append(f"  {g['name']} ({g['id']}): keep {cur}{note}")
        else:
            lines.append(f"  {g['name']} ({g['id']}): add {role}" + ("" if g.get("security") else "  WARNING: not a security group"))
            add.append((g, role))
    return lines, add


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--workspace", help="Fabric workspace name or id")
    ap.add_argument("--group", action="append", default=[], help="Entra group name or object id, optionally =Viewer (default), =Contributor or =Member")
    ap.add_argument("--approved-by", help='"<name> <date>" after an explicit approval of the dry run')
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        self_test()
        return 0
    if not a.workspace or not a.group:
        ap.error("--workspace and at least one --group are required")
    wanted = [parse_group(g, ROLES, "Viewer") for g in a.group]

    tok = token(FABRIC)  # in memory only
    try:
        ws = find_workspace(pages(f"{FABRIC}/v1/workspaces", tok), a.workspace)
    except ApiError as e:
        print(f"Fabric refused the call: {e}\n  Sign in once at https://app.fabric.microsoft.com with this account (Fabric licence, free is enough).")
        return 2
    ra = f"{FABRIC}/v1/workspaces/{urllib.parse.quote(ws['id'])}/roleAssignments"
    assignments = pages(ra, tok)
    print(f"Workspace {ws['displayName']} ({ws['id']}, capacity {ws.get('capacityId') or 'none: assign it to the trial capacity'})")
    print(f"Current role assignments: {', '.join(sorted(str((x.get('principal') or {}).get('displayName')) + '=' + str(x.get('role')) for x in assignments)) or 'none'}")

    groups = [(entra_group(key), role) for key, role in wanted]
    lines, add = plan_lines(groups, assignments)
    print("Plan (Entra groups -> Fabric workspace roles):")
    print("\n".join(lines))
    if not a.approved_by:
        print('Dry run: nothing changed. After an explicit approval re-run with --approved-by "<name> <date>".')
        return 0

    record_approval(ROOT, "fabric groups", a.approved_by)
    for g, role in add:
        http("POST", ra, tok, {"principal": {"id": g["id"], "type": "Group"}, "role": role})
        print(f"  {g['name']}: added {role}")
    after = {str((x.get("principal") or {}).get("id", "")).lower(): x.get("role") for x in pages(ra, tok)}
    ok = True
    print(f"Verification (read back; approved by {a.approved_by}):")
    for g, role in groups:
        cur = after.get(g["id"].lower())
        ok &= bool(cur)
        print(f"  {'PASS' if cur else 'FAIL'} {g['name']}: {cur or 'no role'}")
    print("Test: add a test user to the group in Entra, sign in as that user at https://app.fabric.microsoft.com and open the workspace.")
    return 0 if ok else 1


def self_test() -> None:
    ws = [{"id": "11111111-1111-1111-1111-111111111111", "displayName": "PMO Fabric test"}, {"id": "2", "displayName": "Other"}]
    assert find_workspace(ws, "pmo fabric TEST")["id"].startswith("1111")
    assert find_workspace(ws, "11111111-1111-1111-1111-111111111111")["displayName"] == "PMO Fabric test"
    try:
        find_workspace(ws, "missing")
        raise AssertionError("missing workspace accepted")
    except SystemExit as e:
        assert "Other, PMO Fabric test" in str(e)
    assignments = [{"principal": {"id": "AAA", "displayName": "PH-SUPPORT-READER-DEV", "type": "Group"}, "role": "Viewer"}]
    lines, add = plan_lines([({"id": "aaa", "name": "PH-SUPPORT-READER-DEV", "security": True}, "Contributor"),
                             ({"id": "bbb", "name": "PH-DATAENGINEERS-DEV", "security": True}, "Contributor"),
                             ({"id": "ccc", "name": "M365-TEAM", "security": False}, "Viewer")], assignments)
    text = "\n".join(lines)
    assert "PH-SUPPORT-READER-DEV (aaa): keep Viewer (asked Contributor; change it by hand if needed)" in text, text
    assert "PH-DATAENGINEERS-DEV (bbb): add Contributor" in text and "M365-TEAM (ccc): add Viewer  WARNING: not a security group" in text, text
    assert [g["id"] for g, _ in add] == ["bbb", "ccc"]
    assert parse_group("PH-X", ROLES, "Viewer") == ("PH-X", "Viewer")
    assert parse_group("PH-X=member", ROLES, "Viewer") == ("PH-X", "Member")
    try:
        parse_group("PH-X=Admin", ROLES, "Viewer")
        raise AssertionError("Admin accepted")
    except ValueError:
        pass
    assert GUID.match(ws[0]["id"])
    print("fabric_groups: self-test passed")


if __name__ == "__main__":
    sys.exit(main())

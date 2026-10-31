#!/usr/bin/env python3
"""Configure the pilot's serverless Azure Databricks workspace for Microsoft Entra groups (automatic identity management).

Run after `tools/deploy_free.sh databricks <subscriptionId> --approved-by …` (policy allowed, workspace created).
Automatic identity management (F73-F75) keeps users and group membership in Entra: no SCIM app, no Databricks-side
user list. This tool only puts each Entra group on the workspace once; after that the onboarding flow's group membership
(agent or ID team) is all a joiner needs, synced by Databricks at sign-in.

Without --approved-by it only reads: the workspace (ARM), its current workspace assignments (account API, proves the
caller is an account admin) and the groups (Entra, read-only), then prints the plan.
With --approved-by: for each group, Databricks resolves it from Entra by object id (account external-groups API, Beta;
creates the account group when missing) and the group is assigned to the workspace (Workspace Assignment API: USER, or
ADMIN only when asked for that group), then the assignments are read back. It never removes or lowers a permission.

  python3 tools/databricks_setup.py --account-id <id> --group PH-DATAENGINEERS-DEV [--group <name|objectId>=ADMIN]
  python3 tools/databricks_setup.py ... --approved-by "<name> <date>"
  python3 tools/databricks_setup.py --self-test

Needs: `az login` as a member account of the tenant that is a Databricks account admin (first time: an Entra Global
Administrator signs in to https://accounts.azuredatabricks.net, which makes them account admin; F77). The account id
is in the account console, user menu (top right).
"""
from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from entra_rest import GUID, ApiError, az, entra_group, http, parse_group, record_approval, token  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATABRICKS_RESOURCE = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d"  # Azure Databricks, all Azure clouds (F77)
ACCOUNTS = "https://accounts.azuredatabricks.net"
ARM_API = "2026-01-01"
RANK = {"USER": 1, "ADMIN": 2}
NOT_ACCOUNT_ADMIN = (
    "The signed-in user is not a Databricks account admin for this account (or the account id is wrong).\n"
    "  Once: an Entra Global Administrator of this tenant (a member account, not the Gmail guest) signs in to\n"
    "  https://accounts.azuredatabricks.net; that sign-in makes them account admin. They can then give you the role\n"
    "  (User management > your user > Roles > Account admin). Copy the account id from the user menu (top right).")
NO_AIM = (
    "Databricks refused to resolve the group from Entra. Check that automatic identity management is on: account console >\n"
    "  Security > Identity provider setup > Automatic identity management = Enabled (on by default for accounts created\n"
    "  after 1 Aug 2025; changes take 5-10 minutes).")


def change(current: list[str], wanted: str) -> list[str] | None:
    """Permissions to PUT for a group that has `current` and should have `wanted`, or None when nothing changes.
    Never lowers: a group that is ADMIN stays ADMIN. The PUT replaces the set, so it always carries the wanted level."""
    have = max((RANK.get(p, 0) for p in current), default=0)
    return None if have >= RANK[wanted] else [wanted]


def current_for(assignments: list[dict], *, principal_id: str | None = None, group_name: str | None = None) -> list[str]:
    """Workspace permissions of a group, by Databricks principal id (exact) or else by group name (case-insensitive)."""
    for a in assignments:
        p = a.get("principal") or {}
        if principal_id is not None and str(p.get("principal_id")) == str(principal_id):
            return list(a.get("permissions") or [])
        if principal_id is None and group_name and str(p.get("group_name") or "").lower() == group_name.lower():
            return list(a.get("permissions") or [])
    return []


def plan_lines(groups: list[tuple[dict, str]], assignments: list[dict]) -> list[str]:
    lines = []
    for g, perm in groups:
        cur = current_for(assignments, group_name=g["name"])
        new = change(cur, perm)
        what = f"keep {'/'.join(cur)} (already assigned; never lowered)" if new is None else (
            f"assign {perm}" if not cur else f"raise {'/'.join(cur)} -> {perm}")
        flags = ("" if g.get("security") else "  WARNING: not a security group") + ("  PRIVILEGED: workspace admin" if perm == "ADMIN" else "")
        lines.append(f"  {g['name']} ({g['id']}): {what}{flags}")
    return lines


def workspace(rg: str, name: str | None) -> dict:
    ids = az("resource", "list", "-g", rg, "--resource-type", "Microsoft.Databricks/workspaces", "--query", "[].id", "-o", "tsv").split()
    if name:
        ids = [i for i in ids if i.rsplit("/", 1)[-1].lower() == name.lower()]
    if len(ids) != 1:
        raise SystemExit(f"Expected one Databricks workspace in {rg}, found {len(ids)}. Run tools/deploy_free.sh databricks first.")
    w = json.loads(az("rest", "--method", "get", "--url", f"https://management.azure.com{ids[0]}?api-version={ARM_API}"))
    p = w.get("properties") or {}
    return {"name": w["name"], "url": p.get("workspaceUrl"), "id": str(p.get("workspaceId")), "computeMode": p.get("computeMode"),
            "sku": (w.get("sku") or {}).get("name"), "state": p.get("provisioningState")}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--account-id", help="Databricks account id (account console, user menu)")
    ap.add_argument("--group", action="append", default=[], help="Entra group name or object id, optionally =USER (default) or =ADMIN")
    ap.add_argument("--workspace", help="workspace name (default: the one workspace in the resource group)")
    ap.add_argument("--resource-group", default="rg-pmo-review-pilot")
    ap.add_argument("--approved-by", help='"<name> <date>" after an explicit approval of the dry run')
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        self_test()
        return 0
    if not a.account_id or not GUID.match(a.account_id) or not a.group:
        ap.error("--account-id <guid> and at least one --group are required")
    wanted = [parse_group(g, ("USER", "ADMIN"), "USER") for g in a.group]

    ws = workspace(a.resource_group, a.workspace)
    print(f"Workspace {ws['name']}: https://{ws['url']} (id {ws['id']}, {ws['sku']}, {ws['computeMode']}, {ws['state']})")
    if ws["computeMode"] != "Serverless" or str(ws["sku"]).lower() != "premium":
        print("  WARNING: expected a Premium serverless workspace (infra/modules/databricks.bicep).")
    tok = token(DATABRICKS_RESOURCE)  # in memory only
    base = f"{ACCOUNTS}/api/2.0/accounts/{a.account_id}"
    listing = f"{base}/workspaces/{ws['id']}/permissionassignments"
    try:
        assignments = http("GET", listing, tok).get("permission_assignments") or []
    except ApiError as e:
        if e.status in (401, 403, 404):
            print(NOT_ACCOUNT_ADMIN)
            return 2
        raise
    groups_now = sorted(str((x.get("principal") or {}).get("group_name")) for x in assignments if (x.get("principal") or {}).get("group_name"))
    print(f"Current workspace assignments: {len(assignments)} principal(s); groups: {', '.join(groups_now) or 'none'}")

    groups = [(entra_group(key), perm) for key, perm in wanted]
    print("Plan (Entra groups -> workspace; membership stays in Entra, synced at sign-in):")
    print("\n".join(plan_lines(groups, assignments)))
    if not a.approved_by:
        print('Dry run: nothing changed. After an explicit approval re-run with --approved-by "<name> <date>".')
        return 0

    record_approval(ROOT, "databricks groups", a.approved_by)
    done: dict[str, tuple[str, str]] = {}
    for g, perm in groups:
        try:
            ext = http("GET", f"{ACCOUNTS}/api/2.0/identity/accounts/{a.account_id}/external-groups/{g['id']}", tok)
        except ApiError as e:
            print(f"  {g['name']}: {e}\n{NO_AIM}")
            return 1
        internal = str(ext.get("internal_id") or "")
        if not internal:
            print(f"  {g['name']}: no internal id returned ({ext}).\n{NO_AIM}")
            return 1
        new = change(current_for(assignments, principal_id=internal), perm)
        if new is not None:
            http("PUT", f"{base}/workspaces/{ws['id']}/permissionassignments/principals/{internal}", tok, {"permissions": new})
        done[g["name"]] = (internal, perm)
        print(f"  {g['name']}: {'assigned ' + perm if new else 'already assigned'} (Databricks group {internal})")

    after = http("GET", listing, tok).get("permission_assignments") or []
    ok = True
    print(f"Verification (read back; approved by {a.approved_by}):")
    for name, (internal, perm) in done.items():
        cur = current_for(after, principal_id=internal)
        good = change(cur, perm) is None
        ok &= good
        print(f"  {'PASS' if good else 'FAIL'} {name}: {'/'.join(cur) or 'not assigned'}")
    print(f"Test: add a test user to one of the groups in Entra (or let the onboarding flow do it), sign in as that user at\n"
          f"  https://{ws['url']} and check the workspace opens. Group membership refreshes at browser sign-in after 5 minutes (F75).")
    return 0 if ok else 1


def self_test() -> None:
    assert change([], "USER") == ["USER"]
    assert change(["USER"], "USER") is None
    assert change(["USER"], "ADMIN") == ["ADMIN"]
    assert change(["ADMIN"], "USER") is None  # never lowered
    assert change(["UNKNOWN"], "USER") == ["USER"]
    rows = [{"principal": {"principal_id": 11, "group_name": "PH-DataEngineers-DEV"}, "permissions": ["USER"]},
            {"principal": {"principal_id": 12, "user_name": "a@x"}, "permissions": ["ADMIN"]},
            {"principal": {"principal_id": 13, "group_name": "PH-ADMINS-DEV"}, "permissions": ["ADMIN"]}]
    assert current_for(rows, group_name="ph-dataengineers-dev") == ["USER"]
    assert current_for(rows, principal_id="13") == ["ADMIN"]
    assert current_for(rows, principal_id="99", group_name="PH-ADMINS-DEV") == []  # an id never falls back to the name
    lines = "\n".join(plan_lines([({"id": "g1", "name": "PH-DATAENGINEERS-DEV", "security": True}, "USER"),
                                  ({"id": "g2", "name": "PH-ADMINS-DEV", "security": True}, "USER"),
                                  ({"id": "g3", "name": "NEW-GRP", "security": False}, "ADMIN")], rows))
    assert "PH-DATAENGINEERS-DEV (g1): keep USER" in lines, lines
    assert "PH-ADMINS-DEV (g2): keep ADMIN (already assigned; never lowered)" in lines, lines
    assert "NEW-GRP (g3): assign ADMIN  WARNING: not a security group  PRIVILEGED: workspace admin" in lines, lines
    assert parse_group("PH-X", ("USER", "ADMIN"), "USER") == ("PH-X", "USER")
    assert parse_group("PH-X=admin", ("USER", "ADMIN"), "USER") == ("PH-X", "ADMIN")
    for bad in ("PH-X=OWNER", "=USER", ""):
        try:
            parse_group(bad, ("USER", "ADMIN"), "USER")
            raise AssertionError(bad)
        except ValueError:
            pass
    print("databricks_setup: self-test passed")


if __name__ == "__main__":
    sys.exit(main())

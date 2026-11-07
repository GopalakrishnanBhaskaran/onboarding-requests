#!/usr/bin/env python3
"""Clean-up of test onboarding data (Gokul, 7 Nov 2026): Entra accounts and groups, their Azure role assignments, and the
requests in Cosmos DB — so the same people can be requested again with no "rehire" refusal and no taken sign-in name.

  COSMOS_CONNECTION_STRING=... python3 tools/cleanup_test_data.py [--project PEST --project VESTACY]          # plan, read-only
  COSMOS_CONNECTION_STRING=... python3 tools/cleanup_test_data.py ... --apply --approved-by "Gokul 2026-11-07"   # really delete

What it deletes (the plan lists every item first):
  Entra   - every account named on a request (its domain-specific ID) and every account the agent created, then purges it
            from deleted items (DELETE /directory/deletedItems/{id}; Learn: cannot be restored afterwards), so the sign-in
            name can be created again at once. Accounts an offboarding run already soft-deleted are purged too.
          - every group the AI agent created (its description starts "PMO onboarding mapping row"), deleted and purged.
            Groups that existed before (the mapping sheet's) are kept; deleting an account removes its memberships.
  Azure   - every role assignment of those accounts and groups.
  Cosmos  - per project: every request, review run, escalation and offboarding run; the mails and issues of those requests;
            the response-document records of those requests (records imported from the workbook, with no request, stay);
            the people-inventory rows of every person named on a request or an offboarding run, also when the account is no
            longer in Entra (a Leaver row or an offboarding run is what refuses a rehire; a kept account's row stays);
            the group-resolution cache of the deleted groups; mapping rows of a deleted agent group go back to
            "to be created". One audit event per project records the clean-up.
Never deleted (hard-coded, no flag): the signed-in account, any account with an Entra directory role, any account
registered in the app (userAccess), guests, accounts outside the project's domain suffix, role-assignable groups, any group
the agent did not create. Kept: mapping sheet rows (apart from the revert), contact inventory, people inventory (apart from the
deleted accounts), role registrations, the audit trail. Never prints a token or connection string.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import urllib.parse
from dataclasses import dataclass, field
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

AGENT_GROUP_MARK = "PMO onboarding mapping row"  # WorkflowAgent.Provisioning: the description of a group the agent creates
GRAPH = "https://graph.microsoft.com/v1.0"
REQUEST_BOUND = ("notifications", "issues")       # deleted when they belong to a deleted request
ALL_OF_PROJECT = ("requests", "reviewRuns", "escalations", "offboardingRuns")


@dataclass
class Plan:
    users: list[dict] = field(default_factory=list)      # {id, upn, why}
    purge_only: list[dict] = field(default_factory=list) # already soft-deleted: {id, upn}
    groups: list[dict] = field(default_factory=list)     # {id, name}
    kept: list[str] = field(default_factory=list)        # why something is not deleted
    assignments: list[dict] = field(default_factory=list)  # {id, principal, role, scope}
    cosmos: dict[str, list[tuple[str, str]]] = field(default_factory=dict)  # container -> [(id, projectId)]
    mapping_reverts: list[dict] = field(default_factory=list)
    projects: list[str] = field(default_factory=list)


def low(s) -> str:
    return (s or "").strip().lower()


def graph_get(gcall, path: str) -> dict | None:
    """GET; None when not found."""
    try:
        return gcall("GET", path)
    except LookupError:
        return None


def build_plan(db, gcall, assignments_all: list[dict], projects: list[str], me: dict) -> Plan:
    """Read-only. `db`: Cosmos database client (or a fake), `gcall(method, path)`: Graph (raises LookupError on 404)."""
    p = Plan(projects=projects)
    me_names = {low(me.get("userPrincipalName")), low(me.get("mail"))} - {""}
    registered = set()
    for row in db.get_container_client("userAccess").query_items("SELECT c.userId FROM c", enable_cross_partition_query=True):
        uid = low(row.get("userId"))
        if uid.startswith("signin:"):
            registered.add(uid[len("signin:"):])

    candidates: dict[str, dict] = {}   # key -> {upn?, id?, project, suffix}
    agent_groups: dict[str, str] = {}  # object id -> project
    requests: dict[str, list[dict]] = {}
    for project in projects:
        proj = db.get_container_client("projects").read_item(project, partition_key=project)
        suffix = low(proj.get("domainIdSuffix"))
        reqs = list(db.get_container_client("requests").query_items(
            "SELECT * FROM c WHERE c.projectId = @p", parameters=[{"name": "@p", "value": project}], partition_key=project))
        requests[project] = reqs
        for r in reqs:
            work = (((r.get("flow") or {}).get("idTeamTask") or {}).get("agent")) or {}
            upn = low(r.get("domainId"))
            key = work.get("userObjectId") if work.get("userCreated") and work.get("userObjectId") else upn
            if key:
                c = candidates.setdefault(low(key), {"project": project, "suffix": suffix})
                c.setdefault("upn", upn)
                c.setdefault("mails", set()).add(low(r.get("enterpriseId")))
                if work.get("userCreated") and work.get("userObjectId"):
                    c["id"] = work["userObjectId"]
            if work.get("groupCreated") and work.get("groupObjectId"):
                agent_groups[work["groupObjectId"]] = project
        for run in db.get_container_client("offboardingRuns").query_items(
                "SELECT c.enterpriseId, c.objectId FROM c WHERE c.projectId = @p", parameters=[{"name": "@p", "value": project}], partition_key=project):
            if run.get("objectId"):
                c = candidates.setdefault(low(run["objectId"]), {"project": project, "suffix": suffix})
                c.setdefault("upn", low(run.get("enterpriseId")))
                c["id"] = run["objectId"]

    seen_ids: set[str] = set()
    protected: set[str] = set()   # names of accounts that are kept: their people-inventory rows stay too
    for key, c in sorted(candidates.items()):
        u = graph_get(gcall, f"/users/{urllib.parse.quote(c.get('id') or c['upn'])}?$select=id,userPrincipalName,mail,userType,displayName")
        if u is None:
            if c.get("id") and graph_get(gcall, f"/directory/deletedItems/{c['id']}") is not None:
                p.purge_only.append({"id": c["id"], "upn": c.get("upn") or c["id"]})
            continue
        if u["id"] in seen_ids:
            continue
        seen_ids.add(u["id"])
        upn, mail = low(u.get("userPrincipalName")), low(u.get("mail"))
        why = (
            "the signed-in account" if upn in me_names or mail in me_names
            else "registered in the app (userAccess)" if upn in registered or mail in registered
            else "a guest" if u.get("userType") == "Guest" or "#ext#" in upn
            else f"outside the project's domain {c['suffix']}" if c["suffix"] and not upn.endswith(c["suffix"])
            else None)
        if why is None:
            roles = (gcall("GET", f"/users/{u['id']}/transitiveMemberOf/microsoft.graph.directoryRole?$select=displayName") or {}).get("value") or []
            if roles:
                why = "holds an Entra directory role (" + ", ".join(r.get("displayName", "?") for r in roles) + ")"
        if why:
            p.kept.append(f"account {upn}: {why}")
            protected.update({upn, mail, low(c.get("upn"))} | set(c.get("mails", set())))
        else:
            p.users.append({"id": u["id"], "upn": upn, "project": c["project"], "mails": sorted(c.get("mails", set()) - {""})})

    for gid, project in sorted(agent_groups.items()):
        g = graph_get(gcall, f"/groups/{gid}?$select=id,displayName,description,isAssignableToRole")
        if g is None:
            if graph_get(gcall, f"/directory/deletedItems/{gid}") is not None:
                p.purge_only.append({"id": gid, "upn": f"group {gid}"})
            continue
        if g.get("isAssignableToRole"):
            p.kept.append(f"group {g.get('displayName')}: role-assignable")
        elif not (g.get("description") or "").startswith(AGENT_GROUP_MARK):
            p.kept.append(f"group {g.get('displayName')}: not created by the agent (description)")
        else:
            p.groups.append({"id": gid, "name": g.get("displayName") or gid, "project": project})

    principals = {x["id"] for x in p.users} | {x["id"] for x in p.groups} | {x["id"] for x in p.purge_only}
    p.assignments = [{"id": a["id"], "principal": a.get("principalId"), "role": a.get("roleDefinitionName") or a.get("roleDefinitionId"), "scope": a.get("scope")}
                     for a in assignments_all if a.get("principalId") in principals]

    deleted_upns = {x["upn"] for x in p.users} | {x["upn"] for x in p.purge_only} | {m for x in p.users for m in x.get("mails", [])}
    # Every person named on a request or an offboarding run, also when the account is not (or no longer) in Entra: a Leaver
    # row left for such a name would refuse the next request for it as a rehire. Never a kept account's row.
    named = {low(c.get("upn")) for c in candidates.values()} | {m for c in candidates.values() for m in c.get("mails", set())}
    inventory_names = (deleted_upns | named) - protected - {""}
    group_names = {low(x["name"]) for x in p.groups}
    group_ids = {x["id"] for x in p.groups} | {x["id"] for x in p.purge_only}
    for project in projects:
        req_ids = {r["id"] for r in requests[project]}
        def add(container, doc):
            p.cosmos.setdefault(container, []).append((doc["id"], project))
        q = {"parameters": [{"name": "@p", "value": project}], "partition_key": project}
        for container in ALL_OF_PROJECT:
            for d in db.get_container_client(container).query_items("SELECT c.id FROM c WHERE c.projectId = @p", **q):
                add(container, d)
        for container in REQUEST_BOUND:
            for d in db.get_container_client(container).query_items("SELECT c.id, c.requestId FROM c WHERE c.projectId = @p", **q):
                if d.get("requestId") in req_ids:
                    add(container, d)
        for d in db.get_container_client("responseDocument").query_items("SELECT c.id, c.requestId FROM c WHERE c.projectId = @p", **q):
            if d.get("requestId") in req_ids:  # imported workbook records (no request) stay
                add("responseDocument", d)
        for d in db.get_container_client("peopleInventory").query_items("SELECT c.id, c.enterpriseId FROM c WHERE c.projectId = @p", **q):
            if low(d.get("enterpriseId")) in inventory_names:
                add("peopleInventory", d)
        for d in db.get_container_client("groupResolutions").query_items("SELECT c.id, c.objectId FROM c WHERE c.projectId = @p", **q):
            if d.get("objectId") in group_ids:
                add("groupResolutions", d)
        for d in db.get_container_client("mappingSheet").query_items("SELECT * FROM c WHERE c.projectId = @p", **q):
            if low(d.get("entraGroup")) in group_names and d.get("groupState") == "Exists":
                p.mapping_reverts.append(d)
    return p


def print_plan(p: Plan) -> None:
    print(f"Projects: {', '.join(p.projects)}")
    print(f"Entra accounts to delete and purge ({len(p.users)}):")
    for u in p.users: print(f"  {u['upn']}  ({u['project']})")
    print(f"Entra groups the agent created, to delete and purge ({len(p.groups)}):")
    for g in p.groups: print(f"  {g['name']}  ({g['project']})")
    print(f"Already in deleted items, to purge ({len(p.purge_only)}):")
    for u in p.purge_only: print(f"  {u['upn']}")
    print(f"Azure role assignments to delete ({len(p.assignments)}):")
    for a in p.assignments: print(f"  {a['role']} at {a['scope']}")
    print(f"Kept ({len(p.kept)}):")
    for k in p.kept: print(f"  {k}")
    print("Cosmos documents to delete:")
    for container in ALL_OF_PROJECT + REQUEST_BOUND + ("responseDocument", "peopleInventory", "groupResolutions"):
        print(f"  {container}: {len(p.cosmos.get(container, []))}")
    print(f"Mapping rows back to 'to be created' ({len(p.mapping_reverts)}): {', '.join(d['id'] for d in p.mapping_reverts) or '-'}")


def apply_plan(db, gcall, az_delete_assignment, p: Plan, approved_by: str) -> list[str]:
    """Deletes in a safe order; continues past a failure and returns the failures."""
    failed: list[str] = []
    def attempt(label, fn):
        try:
            fn()
            print(f"  done: {label}")
        except LookupError:
            print(f"  gone already: {label}")
        except Exception as e:  # noqa: BLE001 - report and go on
            failed.append(f"{label}: {e}")
            print(f"  FAILED: {label}: {str(e)[:300]}")
    print("Azure role assignments:")
    for a in p.assignments: attempt(f"{a['role']} at {a['scope']}", lambda a=a: az_delete_assignment(a["id"]))
    print("Entra groups:")
    for g in p.groups: attempt(f"delete group {g['name']}", lambda g=g: gcall("DELETE", f"/groups/{g['id']}"))
    print("Entra accounts:")
    for u in p.users: attempt(f"delete account {u['upn']}", lambda u=u: gcall("DELETE", f"/users/{u['id']}"))
    print("Purge from deleted items (cannot be restored):")
    for x in p.groups + p.users + p.purge_only:
        attempt(f"purge {x.get('upn') or x.get('name')}", lambda x=x: gcall("DELETE", f"/directory/deletedItems/{x['id']}"))
    print("Cosmos:")
    for container, docs in p.cosmos.items():
        c = db.get_container_client(container)
        ok = 0
        for doc_id, project in docs:
            try:
                c.delete_item(doc_id, partition_key=project); ok += 1
            except Exception as e:  # noqa: BLE001
                if type(e).__name__ != "CosmosResourceNotFoundError":
                    failed.append(f"{container}/{doc_id}: {e}")
        print(f"  {container}: {ok} deleted")
    sheet = db.get_container_client("mappingSheet")
    now = datetime.now(timezone.utc)
    for d in p.mapping_reverts:
        d2 = {k: v for k, v in d.items() if not k.startswith("_")}
        d2.update(groupState="ToBeCreated", groupExistsInEntra=False, groupObjectId=None, version=int(d.get("version") or 1) + 1,
                  updatedBy=f"cleanup ({approved_by})", updatedAt=now.isoformat())
        attempt(f"mapping row {d['id']} -> to be created", lambda d2=d2: sheet.upsert_item(d2))
    audit = db.get_container_client("auditEvents")
    for project in p.projects:
        audit.create_item({
            "id": f"aud-{now:%Y%m%d%H%M%S%f}-cleanup-{project.lower()}", "projectId": project, "at": now.isoformat(), "actor": approved_by,
            "action": "test-data-cleanup", "target": f"project {project}",
            "details": (f"tools/cleanup_test_data.py: {len([u for u in p.users if u['project'] == project])} account(s), "
                        f"{len([g for g in p.groups if g['project'] == project])} agent group(s) deleted and purged; "
                        + ", ".join(f"{k} {len([1 for _, pr in v if pr == project])}" for k, v in p.cosmos.items())),
            "result": "failed: " + "; ".join(failed)[:900] if failed else "done", "schemaVersion": 1,
        })
    return failed


def graph_caller():
    from entra_rest import ApiError, http, token
    bearer = token("https://graph.microsoft.com")
    def call(method: str, path: str):
        try:
            return http(method, GRAPH + path, bearer)
        except ApiError as e:
            if e.status == 404:
                raise LookupError(path) from None
            raise
    return call


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", action="append", help="project id (repeatable); default: every project that is not SAMPLE")
    ap.add_argument("--database", default="pmoreview")
    ap.add_argument("--apply", action="store_true", help="really delete (needs --approved-by)")
    ap.add_argument("--approved-by", help='"<name> <date>", after an explicit approval of the plan')
    args = ap.parse_args()
    if args.apply and not args.approved_by:
        raise SystemExit("--apply needs --approved-by \"<name> <date>\" (after the plan was read and approved)")
    cs = os.environ.get("COSMOS_CONNECTION_STRING")
    if not cs:
        raise SystemExit("COSMOS_CONNECTION_STRING is not set")
    from azure.cosmos import CosmosClient
    db = CosmosClient.from_connection_string(cs).get_database_client(args.database)
    projects = args.project or [d["id"] for d in db.get_container_client("projects").query_items(
        "SELECT c.id, c.isSample FROM c", enable_cross_partition_query=True) if not d.get("isSample")]
    me = json.loads(subprocess.run(["az", "ad", "signed-in-user", "show", "--query", "{userPrincipalName:userPrincipalName, mail:mail}", "-o", "json"],
                                   capture_output=True, text=True, check=True).stdout)
    assignments = json.loads(subprocess.run(["az", "role", "assignment", "list", "--all", "--query",
                                             "[].{id:id, principalId:principalId, roleDefinitionName:roleDefinitionName, scope:scope}", "-o", "json"],
                                            capture_output=True, text=True, check=True).stdout)
    gcall = graph_caller()
    plan = build_plan(db, gcall, assignments, projects, me)
    print_plan(plan)
    if not args.apply:
        print("Plan only: nothing changed. After an explicit approval re-run with --apply --approved-by \"<name> <date>\".")
        return 0
    print(f"Applying (approved by {args.approved_by}) ...")
    def az_delete(assignment_id: str):
        subprocess.run(["az", "role", "assignment", "delete", "--ids", assignment_id, "-o", "none"], capture_output=True, text=True, check=True)
    failed = apply_plan(db, gcall, az_delete, plan, args.approved_by)
    print(f"Finished: {len(failed)} failure(s)." + ("" if not failed else " Re-run the same command to retry them."))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

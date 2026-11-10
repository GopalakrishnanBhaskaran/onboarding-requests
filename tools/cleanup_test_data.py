#!/usr/bin/env python3
"""Clean-up of test onboarding data (Gokul, 7 Nov 2026): Entra accounts and groups, their Azure role assignments, and the
requests in Cosmos DB — so the same people can be requested again with no "rehire" refusal and no taken sign-in name.

  COSMOS_CONNECTION_STRING=... python3 tools/cleanup_test_data.py --project PEST [--project VESTACY]        # plan, read-only
  COSMOS_CONNECTION_STRING=... python3 tools/cleanup_test_data.py ... --apply --plan-digest <digest> --approved-by "Gokul 2026-11-07"

Production guard (independent review 10 Nov 2026): every --project must be named (no default), must name its tenant, have a
domain suffix and be marked as a test tenant (isProductionTenant false), and so must every other project in that tenant
(a project with no tenant id counts as sharing it). Otherwise nothing is planned. --apply needs the digest the plan printed:
if anything changed in between (a new request, a new account), it stops and changes nothing.

Scope by default: simulations only (--include-real-requests adds the project's other requests) and only accounts the AI agent
created (--include-hand-made-accounts adds accounts named on those requests that a person created).

What it deletes (the plan lists every item first):
  Entra   - those accounts, then purges them from deleted items (DELETE /directory/deletedItems/{id}; Learn: cannot be
            restored afterwards), so the sign-in name can be created again at once. Such an account an offboarding run
            already soft-deleted is purged too (same checks).
          - every group the AI agent created for those requests (its description starts "PMO onboarding mapping row"),
            deleted and purged. Groups that existed before (the mapping sheet's) are kept.
  Azure   - every role assignment of those accounts and groups (in the subscriptions az lists).
  Cosmos  - per project: those requests (not one whose account is kept), their review runs, escalations, mails, issues and
            response-document records (imported records stay); the offboarding runs and people-inventory rows of the
            deleted accounts only (a real leaver's row and run stay: they refuse a rehire); the group-resolution cache of
            the deleted groups; mapping rows of a deleted agent group go back to "to be created". One audit event per project.
            If any Entra or Azure step fails, Cosmos is left as it is, so a re-run finds the same items again.
Never deleted (hard-coded, no flag): the signed-in account, any account with an Entra directory role (active or eligible, any
scope), any account with a privileged Azure role (Owner, Contributor, User Access Administrator, RBAC Administrator), any
account registered in the app (userAccess), guests, accounts outside the project's domain suffix, role-assignable groups, any
group the agent did not create. Never prints a token or connection string.
"""
from __future__ import annotations

import argparse
import hashlib
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
ALL_OF_PROJECT = ("requests", "reviewRuns", "escalations", "offboardingRuns")  # printed in this order


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
    warnings: list[str] = field(default_factory=list)


def low(s) -> str:
    return (s or "").strip().lower()


def graph_get(gcall, path: str) -> dict | None:
    """GET; None when not found."""
    try:
        return gcall("GET", path)
    except LookupError:
        return None


PRIVILEGED_AZURE_ROLES = {"owner", "contributor", "user access administrator", "role based access control administrator"}


class Refused(Exception):
    """The plan is refused (production guard); nothing was read beyond the projects, nothing changed."""


def suffix_of(raw) -> str:
    s = low(raw)
    return "" if not s else s if s.startswith("@") else "@" + s


def check_projects(db, projects: list[str]) -> list[str]:
    """Why the projects may not be cleaned up (empty: allowed). An unmarked tenant counts as production."""
    if not projects:
        return ["name every project with --project (there is no default)"]
    all_projects = list(db.get_container_client("projects").query_items("SELECT * FROM c", enable_cross_partition_query=True))
    by_id = {d.get("id"): d for d in all_projects}
    why = []
    for project in projects:
        proj = by_id.get(project)
        if proj is None:
            why.append(f"{project}: no such project"); continue
        tenant = low(proj.get("tenantId"))
        if proj.get("isProductionTenant") is not False:
            why.append(f"{project}: not marked as a test tenant (isProductionTenant must be false)")
        if not tenant:
            why.append(f"{project}: names no tenant id")
        if not suffix_of(proj.get("domainIdSuffix")):
            why.append(f"{project}: has no domain suffix, so accounts of other domains could not be told apart")
        for other in all_projects:
            if other.get("id") == project or other.get("isSample"):
                continue
            if tenant and (low(other.get("tenantId")) in ("", tenant)) and other.get("isProductionTenant") is not False:
                why.append(f"{project}: project {other.get('id')} is in the same tenant (or names none) and is not marked as a test tenant")
    return why


def build_plan(db, gcall, assignments_all: list[dict], projects: list[str], me: dict,
               include_real: bool = False, include_hand_made: bool = False) -> Plan:
    """Read-only. `db`: Cosmos database client (or a fake), `gcall(method, path)`: Graph (raises LookupError on 404)."""
    refused = check_projects(db, projects)
    if refused:
        raise Refused("; ".join(refused))
    p = Plan(projects=projects)
    me_names = {low(me.get("userPrincipalName")), low(me.get("mail"))} - {""}
    registered = set()
    for row in db.get_container_client("userAccess").query_items("SELECT c.userId FROM c", enable_cross_partition_query=True):
        uid = low(row.get("userId"))
        if uid.startswith("signin:"):
            registered.add(uid[len("signin:"):])
        elif uid:
            p.warnings.append(f"a person is registered by app user id ({uid[:6]}…): the tool cannot match it to an account; "
                              "only accounts the agent created are safe to delete")
    privileged_azure = {low(a.get("principalId")) for a in assignments_all if low(a.get("roleDefinitionName")) in PRIVILEGED_AZURE_ROLES}

    candidates: dict[str, dict] = {}   # key -> {upn, id?, project, suffix, mails, requests, agent}
    agent_groups: dict[str, str] = {}  # object id -> project
    requests: dict[str, list[dict]] = {}
    for project in projects:
        proj = db.get_container_client("projects").read_item(project, partition_key=project)
        suffix = suffix_of(proj.get("domainIdSuffix"))
        reqs = [r for r in db.get_container_client("requests").query_items(
            "SELECT * FROM c WHERE c.projectId = @p", parameters=[{"name": "@p", "value": project}], partition_key=project)
            if r.get("simulated") or include_real]
        requests[project] = reqs
        for r in reqs:
            work = (((r.get("flow") or {}).get("idTeamTask") or {}).get("agent")) or {}
            upn = low(r.get("domainId"))
            by_agent = bool(work.get("userCreated") and work.get("userObjectId"))
            if by_agent or include_hand_made:
                key = low(work["userObjectId"]) if by_agent else upn
            else:
                key = "named:" + upn  # an account a person made (or none): never deleted, its records stay
            if key and key != "named:":
                c = candidates.setdefault(key, {"project": project, "suffix": suffix, "upn": upn, "agent": by_agent})
                c.setdefault("mails", set()).add(low(r.get("enterpriseId")))
                c.setdefault("requests", set()).add(r["id"])
                if by_agent:
                    c["id"] = work["userObjectId"]
            if work.get("groupCreated") and work.get("groupObjectId"):
                agent_groups[work["groupObjectId"]] = project

    def check_account(u: dict, c: dict) -> str | None:
        upn, mail = low(u.get("userPrincipalName")), low(u.get("mail"))
        names = {upn, mail} | {low(x) for x in u.get("otherMails") or []} | set(c.get("mails", set()))
        if upn in me_names or mail in me_names:
            return "the signed-in account"
        if names & registered:
            return "registered in the app (userAccess)"
        if u.get("userType") == "Guest" or "#ext#" in upn:
            return "a guest"
        if not upn.endswith(c["suffix"]):
            return f"outside the project's domain {c['suffix']}"
        if low(u.get("id")) in privileged_azure:
            return "holds a privileged Azure role"
        # Active and eligible directory roles at any scope (built-in, custom, administrative unit). A failed read stops the plan.
        active = (gcall("GET", f"/roleManagement/directory/roleAssignments?$filter=principalId eq '{u['id']}'") or {}).get("value") or []
        if active:
            return "holds an Entra directory role (active assignment)"
        try:
            eligible = (gcall("GET", f"/roleManagement/directory/roleEligibilityScheduleInstances?$filter=principalId eq '{u['id']}'") or {}).get("value") or []
        except Exception as e:  # noqa: BLE001 - e.g. no Entra ID P2: no eligible roles to read
            eligible = []
            p.warnings.append(f"PIM eligibility could not be read ({str(e)[:80]}); active roles were checked")
        if eligible:
            return "holds an Entra directory role (eligible, PIM)"
        return None

    seen: dict[str, bool] = {}      # object id -> deleted (True) / kept (False)
    protected: set[str] = set()     # names of kept accounts: their records and people-inventory rows stay
    kept_requests: set[str] = set()
    for key, c in sorted(candidates.items()):
        if key.startswith("named:"):
            p.kept.append(f"account {c['upn']}: not created by the AI agent (use --include-hand-made-accounts after checking it)")
            protected.update({c["upn"]} | set(c.get("mails", set())))
            kept_requests.update(c.get("requests", set()))
            continue
        u = graph_get(gcall, f"/users/{urllib.parse.quote(c.get('id') or c['upn'])}?$select=id,userPrincipalName,mail,otherMails,userType,displayName")
        if u is None:
            if c.get("id"):
                d = graph_get(gcall, f"/directory/deletedItems/{c['id']}/microsoft.graph.user?$select=id,userPrincipalName,mail,otherMails,userType")
                if d is not None:
                    why = check_account(d, c)
                    if why:
                        p.kept.append(f"deleted account {c['upn']}: {why}")
                        protected.update({c["upn"]} | set(c.get("mails", set()))); kept_requests.update(c.get("requests", set()))
                    else:
                        p.purge_only.append({"id": c["id"], "upn": c["upn"], "project": c["project"], "mails": sorted(c.get("mails", set()) - {""}),
                                             "requests": sorted(c.get("requests", set()))})
                    continue
            continue  # never created or gone for good: only its request's records go
        if u["id"] in seen:
            if not seen[u["id"]]:
                protected.update({c["upn"]} | set(c.get("mails", set()))); kept_requests.update(c.get("requests", set()))
            else:
                for x in p.users:
                    if x["id"] == u["id"]:
                        x["requests"] = sorted(set(x["requests"]) | c.get("requests", set()))
                        x["mails"] = sorted(set(x["mails"]) | (c.get("mails", set()) - {""}))
            continue
        why = check_account(u, c)
        seen[u["id"]] = why is None
        if why:
            p.kept.append(f"account {low(u.get('userPrincipalName'))}: {why}")
            protected.update({low(u.get("userPrincipalName")), low(u.get("mail")), c["upn"]} | set(c.get("mails", set())))
            kept_requests.update(c.get("requests", set()))
        else:
            p.users.append({"id": u["id"], "upn": low(u.get("userPrincipalName")), "project": c["project"],
                            "mails": sorted(c.get("mails", set()) - {""}), "requests": sorted(c.get("requests", set()))})

    for gid, project in sorted(agent_groups.items()):
        g = graph_get(gcall, f"/groups/{gid}?$select=id,displayName,description,isAssignableToRole")
        if g is None:
            if graph_get(gcall, f"/directory/deletedItems/{gid}") is not None:
                p.purge_only.append({"id": gid, "upn": f"group {gid}", "project": project, "mails": [], "requests": []})
            continue
        if g.get("isAssignableToRole"):
            p.kept.append(f"group {g.get('displayName')}: role-assignable")
        elif not (g.get("description") or "").startswith(AGENT_GROUP_MARK):
            p.kept.append(f"group {g.get('displayName')}: not created by the agent (description)")
        elif low(gid) in privileged_azure:
            p.kept.append(f"group {g.get('displayName')}: holds a privileged Azure role")
        else:
            p.groups.append({"id": gid, "name": g.get("displayName") or gid, "project": project})

    principals = {x["id"] for x in p.users} | {x["id"] for x in p.groups} | {x["id"] for x in p.purge_only}
    p.assignments = [{"id": a["id"], "principal": a.get("principalId"), "role": a.get("roleDefinitionName") or a.get("roleDefinitionId"), "scope": a.get("scope")}
                     for a in assignments_all if a.get("principalId") in principals]

    gone = p.users + [x for x in p.purge_only if x.get("requests") is not None]
    account_ids = {low(x["id"]) for x in gone}
    inventory_names = ({x["upn"] for x in gone} | {m for x in gone for m in x.get("mails", [])}) - protected - {""}
    group_ids = {x["id"] for x in p.groups} | {x["id"] for x in p.purge_only}
    for project in projects:
        req_ids = {r["id"] for r in requests[project]} - kept_requests
        def add(container, doc):
            p.cosmos.setdefault(container, []).append((doc["id"], project))
        q = {"parameters": [{"name": "@p", "value": project}], "partition_key": project}
        for r in requests[project]:
            if r["id"] in req_ids:
                add("requests", r)
        for container in ("reviewRuns", "escalations") + REQUEST_BOUND:
            for d in db.get_container_client(container).query_items("SELECT c.id, c.requestId FROM c WHERE c.projectId = @p", **q):
                if d.get("requestId") in req_ids:
                    add(container, d)
        for d in db.get_container_client("offboardingRuns").query_items("SELECT c.id, c.objectId FROM c WHERE c.projectId = @p", **q):
            if low(d.get("objectId")) in account_ids:  # a real leaver's run stays (rehire control, evidence)
                add("offboardingRuns", d)
        for d in db.get_container_client("responseDocument").query_items("SELECT c.id, c.requestId FROM c WHERE c.projectId = @p", **q):
            if d.get("requestId") in req_ids:  # imported workbook records (no request) stay
                add("responseDocument", d)
        for d in db.get_container_client("peopleInventory").query_items("SELECT c.id, c.enterpriseId FROM c WHERE c.projectId = @p", **q):
            if low(d.get("enterpriseId")) in inventory_names:
                add("peopleInventory", d)
        for d in db.get_container_client("groupResolutions").query_items("SELECT c.id, c.objectId FROM c WHERE c.projectId = @p", **q):
            if d.get("objectId") in group_ids:
                add("groupResolutions", d)
        mine = {low(x["id"]): low(x["name"]) for x in p.groups if x["project"] == project}
        for d in db.get_container_client("mappingSheet").query_items("SELECT * FROM c WHERE c.projectId = @p", **q):
            if d.get("groupState") != "Exists":
                continue
            if low(d.get("groupObjectId")) in mine or (not d.get("groupObjectId") and low(d.get("entraGroup")) in mine.values()):
                p.mapping_reverts.append(d)
    return p


def plan_digest(p: Plan) -> str:
    """What --apply must find again: every object id and document the plan deletes."""
    body = {
        "users": sorted(x["id"] for x in p.users), "groups": sorted(x["id"] for x in p.groups),
        "purge": sorted(x["id"] for x in p.purge_only), "assignments": sorted(x["id"] for x in p.assignments),
        "cosmos": {k: sorted(f"{pr}/{i}" for i, pr in v) for k, v in sorted(p.cosmos.items())},
        "mapping": sorted(f"{d['projectId']}/{d['id']}" for d in p.mapping_reverts), "projects": sorted(p.projects),
    }
    return hashlib.sha256(json.dumps(body, sort_keys=True).encode()).hexdigest()[:16]


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
    for w in dict.fromkeys(p.warnings): print(f"Warning: {w}")
    print(f"Plan digest: {plan_digest(p)}")


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
    if failed:
        # Review 10 Nov 2026: the requests name what is left in Entra or Azure, so a re-run can find it again.
        print("Cosmos and the mapping sheet are left as they are: an Entra or Azure step failed. Re-run to retry.")
        cosmos_items, reverts = {}, []
    else:
        cosmos_items, reverts = p.cosmos, p.mapping_reverts
    print("Cosmos:")
    for container, docs in cosmos_items.items():
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
    for d in reverts:
        d2 = {k: v for k, v in d.items() if not k.startswith("_")}
        d2.update(groupState="ToBeCreated", groupExistsInEntra=False, groupObjectId=None, version=int(d.get("version") or 1) + 1,
                  updatedBy=f"cleanup ({approved_by})", updatedAt=now.isoformat())
        attempt(f"mapping row {d['id']} -> to be created", lambda d=d, d2=d2: replace_if_unchanged(sheet, d, d2))
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


def replace_if_unchanged(container, before: dict, after: dict) -> None:
    """Replace only if the row is as the plan read it (PMO may have changed it since)."""
    if before.get("_etag"):
        from azure.core import MatchConditions
        container.replace_item(before["id"], after, etag=before["_etag"], match_condition=MatchConditions.IfNotModified)
    else:
        container.replace_item(before["id"], after)


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
    ap.add_argument("--project", action="append", required=True, help="project id (repeatable, required: there is no default)")
    ap.add_argument("--include-real-requests", action="store_true", help="also requests that are not simulations (test tenant only)")
    ap.add_argument("--include-hand-made-accounts", action="store_true", help="also accounts a person created (named on those requests)")
    ap.add_argument("--plan-digest", help="the digest the plan printed; --apply refuses if the plan differs")
    ap.add_argument("--database", default="pmoreview")
    ap.add_argument("--apply", action="store_true", help="really delete (needs --approved-by)")
    ap.add_argument("--approved-by", help='"<name> <date>", after an explicit approval of the plan')
    args = ap.parse_args()
    if args.apply and not args.approved_by:
        raise SystemExit("--apply needs --approved-by \"<name> <date>\" (after the plan was read and approved)")
    if args.apply and not args.plan_digest:
        raise SystemExit("--apply needs --plan-digest <digest> (printed at the end of the plan you approved)")
    cs = os.environ.get("COSMOS_CONNECTION_STRING")
    if not cs:
        raise SystemExit("COSMOS_CONNECTION_STRING is not set")
    from azure.cosmos import CosmosClient
    db = CosmosClient.from_connection_string(cs).get_database_client(args.database)
    projects = args.project
    me = json.loads(subprocess.run(["az", "ad", "signed-in-user", "show", "--query", "{userPrincipalName:userPrincipalName, mail:mail}", "-o", "json"],
                                   capture_output=True, text=True, check=True).stdout)
    assignments = json.loads(subprocess.run(["az", "role", "assignment", "list", "--all", "--query",
                                             "[].{id:id, principalId:principalId, roleDefinitionName:roleDefinitionName, scope:scope}", "-o", "json"],
                                            capture_output=True, text=True, check=True).stdout)
    gcall = graph_caller()
    try:
        plan = build_plan(db, gcall, assignments, projects, me, args.include_real_requests, args.include_hand_made_accounts)
    except Refused as e:
        raise SystemExit(f"Refused (nothing changed): {e}") from None
    print_plan(plan)
    if not args.apply:
        print("Plan only: nothing changed. After an explicit approval re-run with --apply --plan-digest "
              f"{plan_digest(plan)} --approved-by \"<name> <date>\".")
        print("Role assignments were read in the current subscription only (az role assignment list --all); "
              "assignments in other subscriptions stay.")
        return 0
    if plan_digest(plan) != args.plan_digest:
        raise SystemExit(f"Refused (nothing changed): the plan is now {plan_digest(plan)}, not the approved {args.plan_digest}. "
                         "Something changed since the plan was read: read the new plan and approve it.")
    print(f"Applying (approved by {args.approved_by}) ...")
    def az_delete(assignment_id: str):
        subprocess.run(["az", "role", "assignment", "delete", "--ids", assignment_id, "-o", "none"], capture_output=True, text=True, check=True)
    failed = apply_plan(db, gcall, az_delete, plan, args.approved_by)
    print(f"Finished: {len(failed)} failure(s)." + ("" if not failed else " Re-run the same command to retry them."))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Seed SAMPLE projects, mapping sheet, response document and test users in Cosmos DB (v2 onboarding flow).

The connection string is read ONLY from the COSMOS_CONNECTION_STRING environment
variable (never a CLI argument, so it stays out of shell history and logs).

Projects and sample users are upserted. Mapping-sheet rows and response-document records are only CREATED when
missing: a redeploy never overwrites what PMO, the ID team or the role team changed in the app. Without --domain-suffix
a project keeps the tenant domain already stored in Cosmos (so `deploy_free.sh app` does not reset it to @sample.invalid).

Examples:
  # Validate the sample file without touching Cosmos
  python3 tools/seed_sample_data.py --dry-run

  # Seed (optionally with the tenant domain used for domain-specific IDs)
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --domain-suffix @contoso.onmicrosoft.com

  # Tenant projects (SAMPLE PEST / SAMPLE Vestacy): the agent may assign roles in the approved access scope, and in a test
  # environment every project contact (PMO, Security, ID team, role team, operators, managers) is the tester's mailbox
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py \
      --access-scope /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access --test-contact tester@example.com

  # Register a real test account after its first sign-in (userId from /.auth/me)
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py \
      --register-user <swa-user-id> --project SAMPLE-ALPHA --roles Requester,Owner

  # Live app with the real tenants (tools/import_workbook.py): remove every SAMPLE project and everything in it
  # (mapping rows, response records, requests, runs, notifications, issues, user access). Lists first; --yes deletes.
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --remove-sample [--yes]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

DEFAULT_DATA = Path(__file__).resolve().parent / "sample_data" / "sample-data.json"
CONTAINERS = {
    "projects": "projects",
    "mappingSheet": "mappingSheet",
    "responseDocument": "responseDocument",
    "userAccess": "userAccess",
    "peopleInventory": "peopleInventory",
}
PARTITION_FIELD = {"projects": "projectId", "mappingSheet": "projectId", "responseDocument": "projectId", "userAccess": "userId",
                   "peopleInventory": "projectId"}
# Created only when missing (live edits win); the others are upserted.
CREATE_ONLY = {"mappingSheet", "responseDocument", "peopleInventory"}
ROLES = {"Requester", "Owner", "Security", "IdTeam", "RoleTeam", "PMO", "Operator", "ProdApprover", "DataMaintainer"}
SAFE_ID = re.compile(r"^[A-Za-z0-9_.:\-]{1,128}$")
DOMAIN_SUFFIX = re.compile(r"^@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")
ACCESS_SCOPE = re.compile(r"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9._()-]{1,90}$")
MAIL = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
SCOPE_PLACEHOLDER = "{{ACCESS_SCOPE}}"


def validate(data: dict) -> list[str]:
    """Return a list of problems. Every record must be marked isSample and use SAMPLE identifiers."""
    problems: list[str] = []
    for section, pk in PARTITION_FIELD.items():
        for i, doc in enumerate(data.get(section, [])):
            where = f"{section}[{i}]"
            if doc.get("isSample") is not True:
                problems.append(f"{where}: isSample must be true")
            if not SAFE_ID.match(str(doc.get("id", ""))):
                problems.append(f"{where}: unsafe or missing id")
            if not doc.get(pk):
                problems.append(f"{where}: missing partition key {pk}")
            project = doc.get("projectId", "")
            if project and not project.startswith("SAMPLE-"):
                problems.append(f"{where}: projectId must start with SAMPLE-")
            if section == "mappingSheet":
                for field in ("resource", "accessLevel", "entraGroup", "role", "sourceReference"):
                    if not doc.get(field):
                        problems.append(f"{where}: {field} is required")
                if not str(doc.get("resource", "")).startswith("SAMPLE "):
                    problems.append(f"{where}: resource must start with 'SAMPLE '")
            if section == "userAccess":
                bad = set(doc.get("roles", [])) - ROLES
                if bad or not doc.get("roles"):
                    problems.append(f"{where}: roles must be a non-empty subset of {sorted(ROLES)}")
                if doc.get("id") != f"{doc.get('userId')}:{doc.get('projectId')}":
                    problems.append(f"{where}: id must be '<userId>:<projectId>'")
    return problems


def user_access_doc(user_id: str, project: str, roles: list[str]) -> dict:
    if not SAFE_ID.match(user_id):
        raise SystemExit("userId has unexpected characters")
    if not project.startswith("SAMPLE-"):
        raise SystemExit("Pilot only registers users against SAMPLE- projects")
    bad = set(roles) - ROLES
    if bad or not roles:
        raise SystemExit(f"roles must be from {sorted(ROLES)}")
    return {"id": f"{user_id}:{project}", "userId": user_id, "projectId": project, "roles": roles, "isSample": True, "schemaVersion": 1}


def apply_domain_suffix(data: dict, suffix: str | None) -> dict:
    """Pure: the projects use this tenant domain for domain-specific IDs (e.g. @contoso.onmicrosoft.com)."""
    if not suffix:
        return data
    if not DOMAIN_SUFFIX.match(suffix):
        raise SystemExit("--domain-suffix must look like @contoso.onmicrosoft.com")
    for p in data.get("projects", []):
        p["domainIdSuffix"] = suffix.lower()
    return data


def keep_live_domain(doc: dict, live: dict | None) -> dict:
    """Pure: without --domain-suffix, a project keeps the tenant domain already stored live."""
    if live and live.get("domainIdSuffix"):
        return {**doc, "domainIdSuffix": live["domainIdSuffix"]}
    return doc


def keep_live_tenant(doc: dict, live: dict | None) -> dict:
    """Pure: a project keeps the tenant id and contacts already stored live (set by --tenant-id / --test-contact)."""
    if not live:
        return doc
    out = dict(doc)
    if "tenantId" in live:
        out["tenantId"] = live["tenantId"]
    if live.get("contacts"):
        out["contacts"] = live["contacts"]
    return out


def apply_access_scope(data: dict, scope: str | None) -> dict:
    """Pure: Azure roles written against the approved access scope get it; without --access-scope their scope stays
    unresolved (the role assignment team assigns them)."""
    if scope and not ACCESS_SCOPE.match(scope):
        raise SystemExit("--access-scope must be /subscriptions/<id>/resourceGroups/<name>")
    for row in data.get("mappingSheet", []):
        for g in row.get("azureRoles") or []:
            if g.get("scope") == SCOPE_PLACEHOLDER:
                g["scope"] = scope
    return data


def apply_test_contact(data: dict, mail: str | None) -> dict:
    """Pure: a test environment sends every project notification to one tester mailbox."""
    if not mail:
        return data
    if not MAIL.match(mail):
        raise SystemExit("--test-contact must be one mail address")
    for p in data.get("projects", []):
        c = p.setdefault("contacts", {})
        for k in ("pmo", "security", "idTeam", "roleTeam", "operators"):
            c[k] = [mail.lower()]
        for k in ("securityManager", "idTeamManager", "roleTeamManager", "ownerManagerFallback"):
            c[k] = mail.lower()
    return data


def apply_tenant_ids(data: dict, pairs: list[str]) -> dict:
    """Pure: --tenant-id PROJECT=<guid> binds a project to an Entra tenant ('none' = the agent's own tenant)."""
    for pair in pairs:
        project, _, tenant = pair.partition("=")
        if tenant.lower() != "none" and not GUID.match(tenant):
            raise SystemExit("--tenant-id must be PROJECT=<tenant guid> or PROJECT=none")
        found = [p for p in data.get("projects", []) if p["projectId"] == project]
        if not found:
            raise SystemExit(f"--tenant-id: no project {project}")
        found[0]["tenantId"] = None if tenant.lower() == "none" else tenant.lower()
    return data


def fill_live_scope(client, doc: dict) -> None:
    """A live SAMPLE row whose Azure role scopes are still unresolved gets the --access-scope (version bumped, so the
    agent re-reviews in-flight requests: the approvers then see the scope). Rows PMO changed otherwise are untouched."""
    live = client.read_item(doc["id"], partition_key=doc["projectId"])
    want = [g.get("scope") for g in doc.get("azureRoles") or []]
    have = live.get("azureRoles") or []
    if not have or len(have) != len(want) or any(h.get("scope") for h in have) or not all(want):
        return
    live["azureRoles"] = [{**h, "scope": w} for h, w in zip(have, want)]
    live["version"] = int(live.get("version", 1)) + 1
    live["updatedBy"] = "seed"
    client.replace_item(live["id"], live)
    print(f"  {doc['id']}: Azure role scope set")


def is_sample(doc: dict) -> bool:
    """Pure: a document of the SAMPLE data set (a SAMPLE- project, or marked isSample). Real projects never match."""
    return str(doc.get("projectId") or "").startswith("SAMPLE-") or doc.get("isSample") is True


def remove_sample(db, really: bool) -> dict[str, int]:
    """Every container: SAMPLE documents found (and deleted with really=True). Real tenants' data is untouched."""
    counts: dict[str, int] = {}
    for props in db.list_containers():
        container = db.get_container_client(props["id"])
        key = props["partitionKey"]["paths"][0].lstrip("/")
        docs = [d for d in container.query_items(
            "SELECT * FROM c WHERE STARTSWITH(c.projectId, 'SAMPLE-') OR c.isSample = true", enable_cross_partition_query=True) if is_sample(d)]
        counts[props["id"]] = len(docs)
        if really:
            for d in docs:
                container.delete_item(d["id"], partition_key=d[key])
    return counts


def connect(database: str):
    cs = os.environ.get("COSMOS_CONNECTION_STRING")
    if not cs:
        raise SystemExit("COSMOS_CONNECTION_STRING is not set")
    try:
        from azure.cosmos import CosmosClient  # imported lazily so --dry-run needs no packages
        from azure.cosmos.exceptions import CosmosResourceExistsError  # noqa: F401
    except ImportError:
        raise SystemExit("pip install -r tools/requirements.txt")
    return CosmosClient.from_connection_string(cs).get_database_client(database)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA)
    ap.add_argument("--database", default=os.environ.get("COSMOS_DATABASE", "pmoreview"))
    ap.add_argument("--dry-run", action="store_true", help="validate and print counts only")
    ap.add_argument("--domain-suffix", help="tenant domain for domain-specific IDs, e.g. @contoso.onmicrosoft.com")
    ap.add_argument("--access-scope", help="resource group the agent may assign roles in (fills the SAMPLE rows' Azure roles)")
    ap.add_argument("--test-contact", help="test environment: every project contact is this mailbox")
    ap.add_argument("--tenant-id", action="append", default=[], metavar="PROJECT=GUID", help="bind a project to an Entra tenant (repeatable)")
    ap.add_argument("--register-user", metavar="USER_ID", help="SWA client principal userId of a test account")
    ap.add_argument("--project", help="SAMPLE- project for --register-user")
    ap.add_argument("--roles", help="comma-separated roles for --register-user")
    ap.add_argument("--remove-sample", action="store_true", help="list (and with --yes delete) every SAMPLE document in the live database")
    ap.add_argument("--yes", action="store_true", help="with --remove-sample: really delete")
    args = ap.parse_args()

    if args.remove_sample:
        counts = remove_sample(connect(args.database), args.yes)
        print(("deleted" if args.yes else "would delete (dry run; add --yes)"), json.dumps({k: v for k, v in counts.items() if v}), f"total {sum(counts.values())}")
        return 0

    if args.register_user:
        if not args.project or not args.roles:
            ap.error("--register-user needs --project and --roles")
        doc = user_access_doc(args.register_user, args.project, [r.strip() for r in args.roles.split(",") if r.strip()])
        if args.dry_run:
            print(json.dumps(doc, indent=2))
            return 0
        connect(args.database).get_container_client("userAccess").upsert_item(doc)
        print(f"registered {doc['id']} roles={doc['roles']}")
        return 0

    data = apply_domain_suffix(json.loads(args.data.read_text(encoding="utf-8")), args.domain_suffix)
    data = apply_tenant_ids(apply_test_contact(apply_access_scope(data, args.access_scope), args.test_contact), args.tenant_id)
    problems = validate(data)
    if problems:
        print("Refusing to seed:", *problems, sep="\n  ", file=sys.stderr)
        return 2

    counts = {section: len(data.get(section, [])) for section in CONTAINERS}
    if args.dry_run:
        print("dry-run OK:", json.dumps(counts))
        return 0

    from azure.cosmos.exceptions import CosmosResourceExistsError, CosmosResourceNotFoundError
    db = connect(args.database)
    created = {}
    for section, container in CONTAINERS.items():
        client = db.get_container_client(container)
        created[section] = 0
        for doc in data.get(section, []):
            if section in CREATE_ONLY:
                try:
                    client.create_item(doc)
                    created[section] += 1
                except CosmosResourceExistsError:
                    if section == "mappingSheet" and args.access_scope:
                        fill_live_scope(client, doc)  # an unresolved SAMPLE scope gets the approved one; nothing else changes
                    # otherwise changed in the app since; keep it
            else:
                if section == "projects":
                    try:
                        live = client.read_item(doc["id"], partition_key=doc["projectId"])
                    except CosmosResourceNotFoundError:
                        live = None
                    if not args.domain_suffix:
                        doc = keep_live_domain(doc, live)
                    if not args.test_contact and not any(t.startswith(doc["projectId"] + "=") for t in args.tenant_id):
                        doc = keep_live_tenant(doc, live)
                    print(f"  {doc['projectId']}: domain {doc['domainIdSuffix']}, tenant {doc.get('tenantName') or doc['name']} "
                          f"({doc.get('tenantId') or 'agent tenant'})")
                client.upsert_item(doc)
                created[section] += 1
    print("seeded:", json.dumps(created))
    return 0


if __name__ == "__main__":
    sys.exit(main())

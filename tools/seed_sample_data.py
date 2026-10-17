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

  # Register a real test account after its first sign-in (userId from /.auth/me)
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py \
      --register-user <swa-user-id> --project SAMPLE-ALPHA --roles Requester,Owner
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
}
PARTITION_FIELD = {"projects": "projectId", "mappingSheet": "projectId", "responseDocument": "projectId", "userAccess": "userId"}
# Created only when missing (live edits win); the others are upserted.
CREATE_ONLY = {"mappingSheet", "responseDocument"}
ROLES = {"Requester", "Owner", "Security", "IdTeam", "RoleTeam", "PMO", "Operator"}
SAFE_ID = re.compile(r"^[A-Za-z0-9_.:\-]{1,128}$")
DOMAIN_SUFFIX = re.compile(r"^@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")


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
    ap.add_argument("--register-user", metavar="USER_ID", help="SWA client principal userId of a test account")
    ap.add_argument("--project", help="SAMPLE- project for --register-user")
    ap.add_argument("--roles", help="comma-separated roles for --register-user")
    args = ap.parse_args()

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
                    pass  # changed in the app since; keep it
            else:
                if section == "projects" and not args.domain_suffix:
                    try:
                        live = client.read_item(doc["id"], partition_key=doc["projectId"])
                    except CosmosResourceNotFoundError:
                        live = None
                    doc = keep_live_domain(doc, live)
                    print(f"  {doc['projectId']}: domain {doc['domainIdSuffix']}")
                client.upsert_item(doc)
                created[section] += 1
    print("seeded:", json.dumps(created))
    return 0


if __name__ == "__main__":
    sys.exit(main())

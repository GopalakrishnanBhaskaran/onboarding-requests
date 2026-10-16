#!/usr/bin/env python3
"""Seed SAMPLE reference data and register test users in Cosmos DB.

The connection string is read ONLY from the COSMOS_CONNECTION_STRING environment
variable (never a CLI argument, so it stays out of shell history and logs).

Examples:
  # Validate the sample file without touching Cosmos
  python3 tools/seed_sample_data.py --dry-run

  # Seed projects, rules, mappings and sample users
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py

  # Register a real test account after its first sign-in (userId from /.auth/me)
  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py \
      --register-user <swa-user-id> --project SAMPLE-ALPHA --roles PMO
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
    "requirementRules": "requirementRules",
    "roleMappings": "roleMappings",
    "userAccess": "userAccess",
}
PARTITION_FIELD = {"projects": "projectId", "requirementRules": "projectId", "roleMappings": "projectId", "userAccess": "userId"}
ROLES = {"PMO", "Reviewer", "Maintainer", "Operator"}
SAFE_ID = re.compile(r"^[A-Za-z0-9_.:\-]{1,128}$")


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


MY_ACCESS = re.compile(r"^https://myaccess\.microsoft\.com/(@[A-Za-z0-9.-]+)?#/access-packages/(?P<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$")


def next_mapping_version(versions: list[dict], link: str) -> dict | None:
    """Pure: the new version to publish, or None if the latest published version already carries the link."""
    m = MY_ACCESS.match(link)
    if not m:
        raise SystemExit("not a My Access access package link")
    if not versions:
        raise SystemExit("mapping not found; create it in the app first")
    latest = max(versions, key=lambda v: v["version"])
    published = [v for v in versions if v.get("publicationStatus") == "Published"]
    if published and max(published, key=lambda v: v["version"]) is latest and latest.get("myAccessLink") == link:
        return None
    base = max(published or versions, key=lambda v: v["version"])
    version = latest["version"] + 1
    doc = {k: v for k, v in base.items() if not k.startswith("_")}
    doc.update({"id": f"{base['mappingId']}:v{version}", "version": version, "myAccessLink": link,
                "accessPackageId": m.group("id").lower(), "publicationStatus": "Published", "owner": "setup-script"})
    return doc


def publish_mapping_link(db, project: str, mapping_id: str, link: str, dry_run: bool = False) -> int:
    if not project.startswith("SAMPLE-"):
        raise SystemExit("Pilot only changes SAMPLE- projects")
    container = db.get_container_client("roleMappings")
    versions = list(container.query_items(
        "SELECT * FROM c WHERE c.projectId = @p AND c.mappingId = @m",
        parameters=[{"name": "@p", "value": project}, {"name": "@m", "value": mapping_id}], partition_key=project))
    doc = next_mapping_version(versions, link)
    if doc is None:
        print(f"mapping {mapping_id}: latest published version already has this link (no change)")
        return 0
    if dry_run:
        print(json.dumps(doc, indent=2))
        return 0
    container.create_item(doc)
    print(f"published {doc['id']} with the access package link")
    return 0


def connect(database: str):
    cs = os.environ.get("COSMOS_CONNECTION_STRING")
    if not cs:
        raise SystemExit("COSMOS_CONNECTION_STRING is not set")
    try:
        from azure.cosmos import CosmosClient  # imported lazily so --dry-run needs no packages
    except ImportError:
        raise SystemExit("pip install -r tools/requirements.txt")
    return CosmosClient.from_connection_string(cs).get_database_client(database)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA)
    ap.add_argument("--database", default=os.environ.get("COSMOS_DATABASE", "pmoreview"))
    ap.add_argument("--dry-run", action="store_true", help="validate and print counts only")
    ap.add_argument("--register-user", metavar="USER_ID", help="SWA client principal userId of a test account")
    ap.add_argument("--project", help="SAMPLE- project for --register-user")
    ap.add_argument("--roles", help="comma-separated roles for --register-user")
    ap.add_argument("--publish-mapping-link", nargs=2, metavar=("MAPPING_ID", "MY_ACCESS_LINK"),
                    help="admin setup: publish the next version of a SAMPLE mapping with this Entra My Access link (no-op if the latest published version has it)")
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

    if args.publish_mapping_link:
        if not args.project:
            ap.error("--publish-mapping-link needs --project")
        return publish_mapping_link(connect(args.database), args.project, *args.publish_mapping_link, dry_run=args.dry_run)

    data = json.loads(args.data.read_text(encoding="utf-8"))
    problems = validate(data)
    if problems:
        print("Refusing to seed:", *problems, sep="\n  ", file=sys.stderr)
        return 2

    counts = {section: len(data.get(section, [])) for section in CONTAINERS}
    if args.dry_run:
        print("dry-run OK:", json.dumps(counts))
        return 0

    db = connect(args.database)
    for section, container in CONTAINERS.items():
        client = db.get_container_client(container)
        for doc in data.get(section, []):
            client.upsert_item(doc)
    print("seeded:", json.dumps(counts))
    return 0


if __name__ == "__main__":
    sys.exit(main())

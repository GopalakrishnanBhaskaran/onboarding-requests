#!/usr/bin/env python3
"""Register people on a live project by sign-in name (or app user id), list them, or remove one. Cosmos data only.

The connection string is read ONLY from the COSMOS_CONNECTION_STRING environment variable.

  COSMOS_CONNECTION_STRING=... python3 tools/register_user.py --project PEST --user jane@contoso.com --roles Requester,Owner
  COSMOS_CONNECTION_STRING=... python3 tools/register_user.py --project PEST --list
  COSMOS_CONNECTION_STRING=... python3 tools/register_user.py --project PEST --user jane@contoso.com --remove

A sign-in name is the address the person signs in with (Owner field format): the app matches it the same way as the named
Owner, so jane@contoso.com also covers her guest (#EXT#) and live.com# sign-ins. A user id is the userId from /.auth/me.
Registering again replaces that person's roles on the project.
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from import_workbook import SAFE_ID, user_doc, user_key  # noqa: E402


def register(db, project: str, user: str | None, roles: list[str], remove: bool, list_only: bool) -> list[str]:
    """The lines to print. The project must exist (one project per tenant, created by import_workbook.py)."""
    from azure.cosmos.exceptions import CosmosResourceNotFoundError
    if not SAFE_ID.match(project):
        raise SystemExit("--project has unexpected characters")
    try:
        db.get_container_client("projects").read_item(project, partition_key=project)
    except CosmosResourceNotFoundError:
        raise SystemExit(f"no project {project}: import its workbooks first (tools/import_workbook.py … --apply)")
    access = db.get_container_client("userAccess")
    if list_only:
        rows = access.query_items("SELECT c.userId, c.roles FROM c WHERE c.projectId = @p", parameters=[{"name": "@p", "value": project}],
                                  enable_cross_partition_query=True)
        return [f"{r['userId']}: {', '.join(r.get('roles') or [])}" for r in rows] or [f"nobody is registered on {project}"]
    key = user_key(user or "")
    if remove:
        try:
            access.delete_item(f"{key}:{project}", partition_key=key)
        except CosmosResourceNotFoundError:
            return [f"{key} was not registered on {project}"]
        return [f"removed {key} from {project}"]
    doc = user_doc(user or "", project, roles)
    access.upsert_item(doc)
    return [f"registered {doc['userId']} on {project}: {', '.join(doc['roles'])}"]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", required=True, help="project id, e.g. PEST or VESTACY")
    ap.add_argument("--user", help="sign-in name (jane@contoso.com) or app user id")
    ap.add_argument("--roles", default="", help="comma-separated: Requester, Owner, Security, IdTeam, RoleTeam, PMO, Operator, ProdApprover, DataMaintainer (replaces the person's roles)")
    ap.add_argument("--remove", action="store_true")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--database", default=os.environ.get("COSMOS_DATABASE", "pmoreview"))
    args = ap.parse_args()
    if not args.list and not args.user:
        ap.error("give --user (with --roles, or --remove), or --list")
    if not args.list and not args.remove and not args.roles:
        ap.error("--roles is required to register")
    cs = os.environ.get("COSMOS_CONNECTION_STRING")
    if not cs:
        raise SystemExit("COSMOS_CONNECTION_STRING is not set")
    from azure.cosmos import CosmosClient
    db = CosmosClient.from_connection_string(cs).get_database_client(args.database)
    for line in register(db, args.project, args.user, [r.strip() for r in args.roles.split(",") if r.strip()], args.remove, args.list):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())

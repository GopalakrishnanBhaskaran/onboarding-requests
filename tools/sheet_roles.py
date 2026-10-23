#!/usr/bin/env python3
"""The Azure roles a project's mapping sheet needs on the agent's allow-list. Read-only.

Every Azure role on the project's active rows that the agent can resolve (the built-in catalogue, same as
BuiltInRoles.Ids) and that lies in the given scope, never a privileged administrator role (BuiltInRoles.NeverByAgent).
Used by `tools/deploy_free.sh provisioning <sub> on <scope> --from-sheet <PROJECT>` (dry run first, then approval).

  COSMOS_CONNECTION_STRING=... python3 tools/sheet_roles.py <PROJECT> <scope>   ids on stdout (comma-separated), summary on stderr
  python3 tools/sheet_roles.py --self-test
"""
from __future__ import annotations

import os
import sys

# Same names and ids as BuiltInRoles.Ids (InfraConsistencyTests keeps them equal), by lower-case name.
CATALOGUE = {
    "reader": "acdd72a7-3385-48ef-bd42-f606fba81ae7",
    "storage blob data reader": "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1",
    "storage blob data contributor": "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
    "storage blob data owner": "b7e6dc6d-f1e8-4753-8033-0f276bb0955b",
    "key vault reader": "21090545-7ca7-4776-b22c-e363652d74d2",
    "key vault secrets user": "4633458b-17de-408a-b874-0445c86b69e6",
    "key vault secrets officer": "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
}
# Same as BuiltInRoles.NeverByAgent (F68): people assign these, whatever the sheet says.
NEVER = {
    "owner": "8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
    "contributor": "b24988ac-6180-42a0-ab88-20f7382dd24c",
    "reservations administrator": "a8889054-8d42-49c9-bc1c-52486c10e7cd",
    "role based access control administrator": "f58310d9-a9f6-439a-9e8d-f62e7b41a168",
    "user access administrator": "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9",
    "key vault data access administrator": "8b54135c-b56d-4d72-a534-26097cfdc8d8",
}
NAMES = {v: k for k, v in CATALOGUE.items()}


def in_scope(scope: str | None, allowed: str) -> bool:
    s, a = (scope or "").rstrip("/").lower(), allowed.rstrip("/").lower()
    return bool(a) and (s == a or s.startswith(a + "/"))


def plan(rows: list[dict], scope: str) -> tuple[list[str], list[str]]:
    """(role ids for the allow-list, summary lines). Pure: tested by --self-test."""
    ids: dict[str, set[str]] = {}
    never: dict[str, set[str]] = {}
    unknown: dict[str, set[str]] = {}
    outside: dict[str, set[str]] = {}
    for r in rows:
        if r.get("deprecated") or r.get("isSample"):
            continue
        row = str(r.get("rowId") or r.get("id"))
        for g in r.get("azureRoles") or []:
            name = str(g.get("roleName") or "").strip()
            rid = str(g.get("roleDefinitionId") or CATALOGUE.get(name.lower()) or "").lower() or None
            if name.lower() in NEVER or rid in NEVER.values():
                never.setdefault(name, set()).add(row)
            elif rid is None:
                unknown.setdefault(name, set()).add(row)
            elif not in_scope(g.get("scope"), scope):
                outside.setdefault(name, set()).add(row)
            else:
                ids.setdefault(rid, set()).add(row)
    rows_of = lambda d, k: f"{len(d[k])} row(s), e.g. {sorted(d[k])[0]}"  # noqa: E731
    lines = [f"  allow: {NAMES.get(i, i).title()} ({i}) — {rows_of(ids, i)}" for i in sorted(ids, key=lambda i: NAMES.get(i, i))]
    lines += [f"  never by the agent (people assign it): {n} — {rows_of(never, n)}" for n in sorted(never)]
    lines += [f"  not in the agent's role catalogue (role team assigns it): {n} — {rows_of(unknown, n)}" for n in sorted(unknown)]
    lines += [f"  outside {scope.rsplit('/', 1)[-1]} (role team assigns it): {n} — {rows_of(outside, n)}" for n in sorted(outside)]
    return sorted(ids), lines


def self_test() -> None:
    scope = "/subscriptions/s/resourceGroups/rg-pmo-sample-access"
    rows = [
        {"rowId": "PH-DATAENGINEERS-DEV", "azureRoles": [{"roleName": "Storage Blob Data Contributor", "scope": scope},
                                                         {"roleName": "Key Vault Secrets Officer", "scope": scope},
                                                         {"roleName": "Reader", "scope": scope + "/"}]},
        {"rowId": "PH-SUPPORT-READER-DEV", "azureRoles": [{"roleName": "Storage Blob Data Reader", "scope": scope}, {"roleName": "reader", "scope": scope}]},
        {"rowId": "PH-ADMINS-DEV", "azureRoles": [{"roleName": "Owner", "scope": scope}, {"roleName": "Custom", "roleDefinitionId": NEVER["contributor"], "scope": scope}]},
        {"rowId": "PH-DEVOPS-DEV", "azureRoles": [{"roleName": "Website Contributor", "scope": scope}, {"roleName": "Key Vault Reader", "scope": "/subscriptions/s/resourceGroups/rg-other"}]},
        {"rowId": "OLD", "deprecated": True, "azureRoles": [{"roleName": "Storage Blob Data Owner", "scope": scope}]},
    ]
    ids, lines = plan(rows, scope)
    assert ids == sorted([CATALOGUE["storage blob data contributor"], CATALOGUE["key vault secrets officer"], CATALOGUE["reader"],
                          CATALOGUE["storage blob data reader"]]), ids
    text = "\n".join(lines)
    assert "never by the agent (people assign it): Owner" in text and "never by the agent (people assign it): Custom" in text, text
    assert "not in the agent's role catalogue (role team assigns it): Website Contributor" in text, text
    assert "outside rg-pmo-sample-access (role team assigns it): Key Vault Reader" in text, text
    assert "Storage Blob Data Owner" not in text  # deprecated rows do not count
    assert not set(ids) & set(NEVER.values())
    print("sheet_roles: self-test passed")


def main() -> int:
    if "--self-test" in sys.argv:
        self_test()
        return 0
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    project, scope = sys.argv[1], sys.argv[2]
    from azure.cosmos import CosmosClient
    db = CosmosClient.from_connection_string(os.environ["COSMOS_CONNECTION_STRING"]).get_database_client(os.environ.get("COSMOS_DATABASE", "pmoreview"))
    rows = list(db.get_container_client("mappingSheet").query_items(
        "SELECT * FROM c WHERE c.projectId = @p", parameters=[{"name": "@p", "value": project}], partition_key=project))
    ids, lines = plan(rows, scope)
    print(f"Mapping sheet {project}: {len(rows)} row(s); Azure roles the agent may assign at {scope}:", file=sys.stderr)
    print("\n".join(lines) or "  none", file=sys.stderr)
    print(",".join(ids))
    return 0 if ids else 1


if __name__ == "__main__":
    sys.exit(main())

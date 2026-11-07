#!/usr/bin/env python3
"""Offline test of tools/cleanup_test_data.py with a fake Cosmos database and a fake Graph (SAMPLE data only)."""
from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cleanup_test_data as ct  # noqa: E402

P = "SAMPLE-PEST"


class NotFound(Exception):
    pass


NotFound.__name__ = "CosmosResourceNotFoundError"


class Container:
    def __init__(self, docs):
        self.docs = {d["id"]: d for d in docs}

    def query_items(self, query, parameters=None, partition_key=None, enable_cross_partition_query=False):
        want = parameters[0]["value"] if parameters else None
        return [dict(d) for d in self.docs.values() if want is None or d.get("projectId") == want]

    def read_item(self, item, partition_key):
        return self.docs[item]

    def delete_item(self, item, partition_key):
        if item not in self.docs:
            raise NotFound(item)
        del self.docs[item]

    def upsert_item(self, doc):
        self.docs[doc["id"]] = doc

    def create_item(self, doc):
        self.docs[doc["id"]] = doc


class Db:
    def __init__(self, data):
        self.c = {k: Container(v) for k, v in data.items()}

    def get_container_client(self, name):
        return self.c.setdefault(name, Container([]))


def req(rid, upn, mail, agent=None):
    return {"id": rid, "projectId": P, "domainId": upn, "enterpriseId": mail, "flow": {"idTeamTask": {"agent": agent} if agent else None}}


class Graph:
    def __init__(self):
        self.users = {
            "u-new": {"id": "u-new", "userPrincipalName": "sample.new@sample.onmicrosoft.invalid", "userType": "Member"},
            "u-hand": {"id": "u-hand", "userPrincipalName": "sample.hand@sample.onmicrosoft.invalid", "userType": "Member"},
            "u-admin": {"id": "u-admin", "userPrincipalName": "sample.admin@sample.onmicrosoft.invalid", "userType": "Member"},
            "u-me": {"id": "u-me", "userPrincipalName": "sample.me@sample.onmicrosoft.invalid", "userType": "Member"},
            "u-reg": {"id": "u-reg", "userPrincipalName": "sample.tester@sample.onmicrosoft.invalid", "userType": "Member"},
            "u-other": {"id": "u-other", "userPrincipalName": "sample.x@other.invalid", "userType": "Member"},
        }
        self.groups = {
            "g-agent": {"id": "g-agent", "displayName": "sample-grp-new", "description": "PMO onboarding mapping row R1. Created by the AI agent after Owner and Security approval."},
            "g-sheet": {"id": "g-sheet", "displayName": "sample-grp-sheet", "description": "existing team group"},
        }
        self.deleted = {"u-offboarded": {"id": "u-offboarded"}}
        self.calls = []

    def __call__(self, method, path):
        self.calls.append((method, path.split("?")[0]))
        base = path.split("?")[0]
        if method == "DELETE":
            kind, oid = base.strip("/").split("/")[0], base.rsplit("/", 1)[1]
            if kind == "users": self.deleted[oid] = self.users.pop(oid)
            elif kind == "groups": self.deleted[oid] = self.groups.pop(oid)
            elif oid in self.deleted: del self.deleted[oid]
            else: raise LookupError(path)
            return {}
        if base.endswith("/microsoft.graph.directoryRole"):
            return {"value": [{"displayName": "User Administrator"}] if "u-admin" in base else []}
        if base.startswith("/users/"):
            key = ct.urllib.parse.unquote(base.split("/")[2])
            for u in self.users.values():
                if key in (u["id"], u["userPrincipalName"]): return u
            raise LookupError(path)
        if base.startswith("/groups/"):
            if base.split("/")[2] in self.groups: return self.groups[base.split("/")[2]]
            raise LookupError(path)
        if base.startswith("/directory/deletedItems/"):
            if base.rsplit("/", 1)[1] in self.deleted: return self.deleted[base.rsplit("/", 1)[1]]
            raise LookupError(path)
        raise AssertionError(path)


def world():
    data = {
        "projects": [{"id": P, "projectId": P, "domainIdSuffix": "@sample.onmicrosoft.invalid"}],
        "userAccess": [{"id": "signin:sample.tester@sample.onmicrosoft.invalid:" + P, "userId": "signin:sample.tester@sample.onmicrosoft.invalid", "projectId": P}],
        "requests": [
            req("r1", "sample.new@sample.onmicrosoft.invalid", "sample.new@corp.invalid", {"userCreated": True, "userObjectId": "u-new", "groupCreated": True, "groupObjectId": "g-agent"}),
            req("r2", "sample.hand@sample.onmicrosoft.invalid", "sample.hand@corp.invalid"),
            req("r3", "sample.admin@sample.onmicrosoft.invalid", "sample.admin@corp.invalid"),
            req("r4", "sample.me@sample.onmicrosoft.invalid", "sample.me@corp.invalid"),
            req("r5", "sample.tester@sample.onmicrosoft.invalid", "sample.tester@corp.invalid"),
            req("r6", "sample.x@other.invalid", "sample.x@other.invalid"),
            req("r7", "sample.sheet@sample.onmicrosoft.invalid", "sample.sheet@corp.invalid", {"groupCreated": False, "groupObjectId": "g-sheet"}),
        ],
        "reviewRuns": [{"id": "run1", "projectId": P}],
        "escalations": [{"id": "e1", "projectId": P}],
        "offboardingRuns": [{"id": "off1", "projectId": P, "enterpriseId": "sample.gone@sample.onmicrosoft.invalid", "objectId": "u-offboarded"}],
        "notifications": [{"id": "n1", "projectId": P, "requestId": "r1"}, {"id": "n-daily", "projectId": P, "requestId": None}],
        "issues": [{"id": "i1", "projectId": P, "requestId": "r2"}, {"id": "i-platform", "projectId": P, "requestId": None}],
        "responseDocument": [{"id": "r1", "projectId": P, "requestId": "r1"}, {"id": "imported-1", "projectId": P, "requestId": None}],
        "peopleInventory": [{"id": "person-1", "projectId": P, "enterpriseId": "sample.new@corp.invalid", "status": "Leaver"},
                            {"id": "person-2", "projectId": P, "enterpriseId": "sample.keep@corp.invalid", "status": "Active"}],
        "groupResolutions": [{"id": "grp-1", "projectId": P, "objectId": "g-agent"}, {"id": "grp-2", "projectId": P, "objectId": "g-sheet"}],
        "mappingSheet": [{"id": "R1", "projectId": P, "entraGroup": "SAMPLE-GRP-NEW", "groupState": "Exists", "version": 2},
                         {"id": "R2", "projectId": P, "entraGroup": "sample-grp-sheet", "groupState": "Exists", "version": 1}],
        "auditEvents": [],
    }
    assignments = [{"id": "/a/1", "principalId": "g-agent", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/2", "principalId": "g-sheet", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/3", "principalId": "u-hand", "roleDefinitionName": "Reader", "scope": "/rg"}]
    return Db(data), Graph(), assignments


class CleanupTests(unittest.TestCase):
    def plan(self):
        db, g, a = world()
        return db, g, ct.build_plan(db, g, a, [P], {"userPrincipalName": "sample.me@sample.onmicrosoft.invalid"})

    def test_deletes_every_request_account_but_never_me_admins_registered_or_other_domains(self):
        _, _, p = self.plan()
        self.assertEqual(sorted(u["upn"] for u in p.users), ["sample.hand@sample.onmicrosoft.invalid", "sample.new@sample.onmicrosoft.invalid"])
        kept = " ".join(p.kept)
        for why in ("signed-in account", "directory role", "registered in the app", "outside the project's domain"):
            self.assertIn(why, kept)

    def test_deletes_only_groups_the_agent_created_and_their_and_the_accounts_role_assignments(self):
        _, _, p = self.plan()
        self.assertEqual([g["name"] for g in p.groups], ["sample-grp-new"])
        self.assertEqual(sorted(a["id"] for a in p.assignments), ["/a/1", "/a/3"])

    def test_purges_an_account_an_offboarding_run_already_soft_deleted(self):
        _, _, p = self.plan()
        self.assertEqual([x["id"] for x in p.purge_only], ["u-offboarded"])

    def test_keeps_imported_records_project_mails_and_issues_and_other_people(self):
        _, _, p = self.plan()
        ids = {k: sorted(i for i, _ in v) for k, v in p.cosmos.items()}
        self.assertEqual(ids["responseDocument"], ["r1"])
        self.assertEqual(ids["notifications"], ["n1"])
        self.assertEqual(ids["issues"], ["i1"])
        self.assertEqual(ids["peopleInventory"], ["person-1"])  # the Leaver row that would refuse a rehire
        self.assertEqual(ids["groupResolutions"], ["grp-1"])
        self.assertEqual(len(ids["requests"]), 7)
        self.assertEqual([d["id"] for d in p.mapping_reverts], ["R1"])

    def test_apply_deletes_in_order_purges_and_records_one_audit_event(self):
        db, g, p = self.plan()
        removed = []
        failed = ct.apply_plan(db, g, removed.append, p, "SAMPLE tester 2026-11-07")
        self.assertEqual(failed, [])
        self.assertEqual(sorted(removed), ["/a/1", "/a/3"])
        self.assertNotIn("u-new", g.users); self.assertNotIn("u-hand", g.users); self.assertIn("u-admin", g.users); self.assertIn("u-me", g.users)
        self.assertEqual(g.deleted, {})  # everything purged: the names can be used again at once
        self.assertIn("g-sheet", g.groups)
        self.assertEqual(sorted(db.c["responseDocument"].docs), ["imported-1"])
        self.assertEqual(db.c["requests"].docs, {})
        self.assertEqual(db.c["mappingSheet"].docs["R1"]["groupState"], "ToBeCreated")
        self.assertEqual(db.c["mappingSheet"].docs["R2"]["groupState"], "Exists")
        self.assertEqual(len(db.c["auditEvents"].docs), 1)
        first_delete = next(i for i, c in enumerate(g.calls) if c[0] == "DELETE")
        self.assertTrue(g.calls[first_delete][1].startswith("/groups/"))  # role assignments were removed before


if __name__ == "__main__":
    unittest.main(verbosity=1)

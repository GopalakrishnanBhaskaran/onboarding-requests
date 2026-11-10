#!/usr/bin/env python3
"""Offline test of tools/cleanup_test_data.py with a fake Cosmos database and a fake Graph (SAMPLE data only)."""
from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cleanup_test_data as ct  # noqa: E402

P = "SAMPLE-PEST"
TENANT = "00000000-0000-4000-8000-0000000000aa"
SUFFIX = "@sample.onmicrosoft.invalid"


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

    def replace_item(self, item, body, **_):
        self.docs[item] = body

    def create_item(self, doc):
        self.docs[doc["id"]] = doc


class Db:
    def __init__(self, data):
        self.c = {k: Container(v) for k, v in data.items()}

    def get_container_client(self, name):
        return self.c.setdefault(name, Container([]))


def req(rid, upn, mail, agent=None, simulated=True):
    return {"id": rid, "projectId": P, "domainId": upn, "enterpriseId": mail, "simulated": simulated,
            "flow": {"idTeamTask": {"agent": agent} if agent else None}}


def user(oid, name, kind="Member"):
    return {"id": oid, "userPrincipalName": name, "userType": kind}


class Graph:
    def __init__(self):
        self.users = {u["id"]: u for u in [
            user("u-new", "sample.new" + SUFFIX), user("u-hand", "sample.hand" + SUFFIX), user("u-admin", "sample.admin" + SUFFIX),
            user("u-pim", "sample.pim" + SUFFIX), user("u-azowner", "sample.azowner" + SUFFIX), user("u-me", "sample.me" + SUFFIX),
            user("u-reg", "sample.tester" + SUFFIX), user("u-other", "sample.x@other.invalid"), user("u-real", "sample.real" + SUFFIX),
        ]}
        self.groups = {
            "g-agent": {"id": "g-agent", "displayName": "sample-grp-new", "description": "PMO onboarding mapping row R1. Created by the AI agent after Owner and Security approval."},
            "g-sheet": {"id": "g-sheet", "displayName": "sample-grp-sheet", "description": "existing team group"},
        }
        # Soft-deleted: a live simulation's account offboarded by the agent, and a real leaver offboarded in the project.
        self.deleted = {"u-simgone": user("u-simgone", "a1b2u-simgonesample.simgone" + SUFFIX), "u-leaver": user("u-leaver", "c3d4sample.leaver" + SUFFIX)}
        self.calls = []
        self.fail_delete = set()

    def __call__(self, method, path):
        self.calls.append((method, path.split("?")[0]))
        base = path.split("?")[0]
        if method == "DELETE":
            kind, oid = base.strip("/").split("/")[0], base.rsplit("/", 1)[1]
            if oid in self.fail_delete: raise RuntimeError("Forbidden")
            if kind == "users": self.deleted[oid] = self.users.pop(oid)
            elif kind == "groups": self.deleted[oid] = self.groups.pop(oid)
            elif oid in self.deleted: del self.deleted[oid]
            else: raise LookupError(path)
            return {}
        if base == "/roleManagement/directory/roleAssignments":
            return {"value": [{"roleDefinitionId": "x"}] if "u-admin" in path else []}
        if base == "/roleManagement/directory/roleEligibilityScheduleInstances":
            return {"value": [{"roleDefinitionId": "x"}] if "u-pim" in path else []}
        if base.startswith("/users/"):
            key = ct.urllib.parse.unquote(base.split("/")[2])
            for u in self.users.values():
                if key in (u["id"], u["userPrincipalName"]): return u
            raise LookupError(path)
        if base.startswith("/groups/"):
            if base.split("/")[2] in self.groups: return self.groups[base.split("/")[2]]
            raise LookupError(path)
        if base.startswith("/directory/deletedItems/"):
            oid = base.split("/")[3]
            if oid in self.deleted: return self.deleted[oid]
            raise LookupError(path)
        raise AssertionError(path)


def agent_user(oid, group=None):
    a = {"userCreated": True, "userObjectId": oid}
    if group: a.update(groupCreated=True, groupObjectId=group)
    return a


def world(production=False, other_tenant_project=None):
    projects = [{"id": P, "projectId": P, "tenantId": TENANT, "isProductionTenant": production, "domainIdSuffix": SUFFIX}]
    if other_tenant_project: projects.append(other_tenant_project)
    data = {
        "projects": projects,
        "userAccess": [{"id": "signin:sample.tester" + SUFFIX + ":" + P, "userId": "signin:sample.tester" + SUFFIX, "projectId": P}],
        "requests": [
            req("r1", "sample.new" + SUFFIX, "sample.new@corp.invalid", agent_user("u-new", "g-agent")),
            req("r2", "sample.hand" + SUFFIX, "sample.hand@corp.invalid"),                  # a simulation naming a hand-made account
            req("r3", "sample.admin" + SUFFIX, "sample.admin@corp.invalid", agent_user("u-admin")),
            req("r3b", "sample.pim" + SUFFIX, "sample.pim@corp.invalid", agent_user("u-pim")),
            req("r3c", "sample.azowner" + SUFFIX, "sample.azowner@corp.invalid", agent_user("u-azowner")),
            req("r4", "sample.me" + SUFFIX, "sample.me@corp.invalid", agent_user("u-me")),
            req("r5", "sample.tester" + SUFFIX, "sample.tester@corp.invalid", agent_user("u-reg")),
            req("r6", "sample.x@other.invalid", "sample.x@other.invalid", agent_user("u-other")),
            req("r7", "sample.sheet" + SUFFIX, "sample.sheet@corp.invalid", {"groupCreated": False, "groupObjectId": "g-sheet"}),
            req("r8", "sample.simgone" + SUFFIX, "sample.simgone@corp.invalid", agent_user("u-simgone")),
            req("r9", "sample.real" + SUFFIX, "sample.real@corp.invalid", agent_user("u-real"), simulated=False),  # a real request
        ],
        "reviewRuns": [{"id": "run1", "projectId": P, "requestId": "r1"}, {"id": "run9", "projectId": P, "requestId": "r9"}],
        "escalations": [{"id": "e1", "projectId": P, "requestId": "r1"}, {"id": "e3", "projectId": P, "requestId": "r3"}],
        "offboardingRuns": [{"id": "off-sim", "projectId": P, "enterpriseId": "sample.simgone" + SUFFIX, "objectId": "u-simgone"},
                            {"id": "off-real", "projectId": P, "enterpriseId": "sample.leaver" + SUFFIX, "objectId": "u-leaver"}],
        "notifications": [{"id": "n1", "projectId": P, "requestId": "r1"}, {"id": "n-daily", "projectId": P, "requestId": None}],
        "issues": [{"id": "i1", "projectId": P, "requestId": "r2"}, {"id": "i-platform", "projectId": P, "requestId": None}],
        "responseDocument": [{"id": "r1", "projectId": P, "requestId": "r1"}, {"id": "r3", "projectId": P, "requestId": "r3"},
                             {"id": "imported-1", "projectId": P, "requestId": None}],
        "peopleInventory": [{"id": "person-1", "projectId": P, "enterpriseId": "sample.new@corp.invalid", "status": "Leaver"},
                            {"id": "person-2", "projectId": P, "enterpriseId": "sample.keep@corp.invalid", "status": "Active"},
                            {"id": "person-sim", "projectId": P, "enterpriseId": "sample.simgone" + SUFFIX, "status": "Leaver"},
                            {"id": "person-leaver", "projectId": P, "enterpriseId": "sample.leaver" + SUFFIX, "status": "Leaver"},
                            {"id": "person-hand", "projectId": P, "enterpriseId": "sample.hand@corp.invalid", "status": "Leaver"},
                            {"id": "person-4", "projectId": P, "enterpriseId": "sample.admin" + SUFFIX, "status": "Leaver"}],
        "groupResolutions": [{"id": "grp-1", "projectId": P, "objectId": "g-agent"}, {"id": "grp-2", "projectId": P, "objectId": "g-sheet"}],
        "mappingSheet": [{"id": "R1", "projectId": P, "entraGroup": "SAMPLE-GRP-NEW", "groupObjectId": "g-agent", "groupState": "Exists", "version": 2},
                         {"id": "R2", "projectId": P, "entraGroup": "sample-grp-sheet", "groupObjectId": "g-sheet", "groupState": "Exists", "version": 1}],
        "auditEvents": [],
    }
    assignments = [{"id": "/a/1", "principalId": "g-agent", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/2", "principalId": "g-sheet", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/3", "principalId": "u-hand", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/4", "principalId": "u-new", "roleDefinitionName": "Reader", "scope": "/rg"},
                   {"id": "/a/5", "principalId": "u-azowner", "roleDefinitionName": "Owner", "scope": "/subscriptions/s"}]
    return Db(data), Graph(), assignments


ME = {"userPrincipalName": "sample.me" + SUFFIX}


class CleanupTests(unittest.TestCase):
    def plan(self, **kw):
        db, g, a = world()
        return db, g, ct.build_plan(db, g, a, [P], ME, **kw)

    def test_refuses_a_production_or_unmarked_tenant_and_a_missing_project(self):
        for production in (True, None):
            db, g, a = world(production=production)
            with self.assertRaises(ct.Refused):
                ct.build_plan(db, g, a, [P], ME)
        db, g, a = world(other_tenant_project={"id": "REAL", "tenantId": TENANT, "isProductionTenant": None, "domainIdSuffix": SUFFIX})
        with self.assertRaisesRegex(ct.Refused, "REAL"):
            ct.build_plan(db, g, a, [P], ME)
        db, g, a = world(other_tenant_project={"id": "NOTENANT", "tenantId": None, "isProductionTenant": True})
        with self.assertRaisesRegex(ct.Refused, "NOTENANT"):  # no tenant id counts as sharing it
            ct.build_plan(db, g, a, [P], ME)
        db, g, a = world()
        with self.assertRaises(ct.Refused):
            ct.build_plan(db, g, a, [], ME)
        self.assertEqual(g.calls, [])  # refused before any Graph read

    def test_by_default_only_accounts_the_agent_created_for_simulations(self):
        _, _, p = self.plan()
        self.assertEqual([u["upn"] for u in p.users], ["sample.new" + SUFFIX])
        kept = " ".join(p.kept)
        for why in ("signed-in account", "directory role (active", "directory role (eligible", "privileged Azure role",
                    "registered in the app", "outside the project's domain", "not created by the AI agent"):
            self.assertIn(why, kept)
        self.assertNotIn("sample.real", " ".join(u["upn"] for u in p.users))  # a real request is out of scope
        self.assertNotIn("r9", {i for i, _ in p.cosmos["requests"]})

    def test_hand_made_accounts_and_real_requests_only_when_asked(self):
        _, _, p = self.plan(include_real=True, include_hand_made=True)
        self.assertEqual(sorted(u["upn"] for u in p.users), ["sample.hand" + SUFFIX, "sample.new" + SUFFIX, "sample.real" + SUFFIX])

    def test_deletes_only_groups_the_agent_created_and_their_and_the_accounts_role_assignments(self):
        _, _, p = self.plan()
        self.assertEqual([g["name"] for g in p.groups], ["sample-grp-new"])
        self.assertEqual(sorted(a["id"] for a in p.assignments), ["/a/1", "/a/4"])

    def test_purges_the_agents_offboarded_account_but_never_a_real_leaver(self):
        _, _, p = self.plan()
        self.assertEqual([x["id"] for x in p.purge_only], ["u-simgone"])
        ids = {k: sorted(i for i, _ in v) for k, v in p.cosmos.items()}
        self.assertEqual(ids["offboardingRuns"], ["off-sim"])          # the real leaver's run stays
        self.assertNotIn("person-leaver", ids["peopleInventory"])      # and its Leaver row (rehire control)

    def test_keeps_records_of_kept_accounts_imported_records_and_other_people(self):
        _, _, p = self.plan()
        ids = {k: sorted(i for i, _ in v) for k, v in p.cosmos.items()}
        self.assertEqual(ids["requests"], ["r1", "r8"])                # r2..r7 name kept accounts: their records stay
        self.assertEqual(ids["responseDocument"], ["r1"])
        self.assertEqual(ids["escalations"], ["e1"])
        self.assertEqual(ids["reviewRuns"], ["run1"])
        self.assertEqual(ids["notifications"], ["n1"])
        self.assertNotIn("issues", ids)
        self.assertEqual(ids["peopleInventory"], ["person-1", "person-sim"])
        self.assertEqual(ids["groupResolutions"], ["grp-1"])
        self.assertEqual([d["id"] for d in p.mapping_reverts], ["R1"])

    def test_the_plan_digest_changes_when_anything_new_appears(self):
        db, g, a = world()
        first = ct.plan_digest(ct.build_plan(db, g, a, [P], ME))
        self.assertEqual(first, ct.plan_digest(ct.build_plan(db, g, a, [P], ME)))
        db.c["requests"].docs["r10"] = req("r10", "sample.hand" + SUFFIX, "sample.hand@corp.invalid", agent_user("u-hand"))
        self.assertNotEqual(first, ct.plan_digest(ct.build_plan(db, g, a, [P], ME)))

    def test_apply_deletes_in_order_purges_and_records_one_audit_event(self):
        db, g, p = self.plan()
        removed = []
        failed = ct.apply_plan(db, g, removed.append, p, "SAMPLE tester 2026-11-07")
        self.assertEqual(failed, [])
        self.assertEqual(sorted(removed), ["/a/1", "/a/4"])
        self.assertNotIn("u-new", g.users)
        for kept in ("u-hand", "u-admin", "u-pim", "u-azowner", "u-me", "u-real"): self.assertIn(kept, g.users)
        self.assertEqual(set(g.deleted), {"u-leaver"})  # the real leaver stays restorable
        self.assertIn("g-sheet", g.groups)
        self.assertEqual(sorted(db.c["responseDocument"].docs), ["imported-1", "r3"])
        self.assertIn("r9", db.c["requests"].docs)
        self.assertEqual(db.c["mappingSheet"].docs["R1"]["groupState"], "ToBeCreated")
        self.assertEqual(db.c["mappingSheet"].docs["R2"]["groupState"], "Exists")
        self.assertEqual(len(db.c["auditEvents"].docs), 1)
        first_delete = next(i for i, c in enumerate(g.calls) if c[0] == "DELETE")
        self.assertTrue(g.calls[first_delete][1].startswith("/groups/"))  # role assignments were removed before

    def test_an_entra_failure_leaves_cosmos_as_it_is_so_a_re_run_finds_it(self):
        db, g, p = self.plan()
        g.fail_delete.add("u-new")
        failed = ct.apply_plan(db, g, lambda _: None, p, "SAMPLE tester 2026-11-07")
        self.assertTrue(failed)
        self.assertIn("r1", db.c["requests"].docs)
        self.assertIn("person-1", db.c["peopleInventory"].docs)
        self.assertEqual(db.c["mappingSheet"].docs["R1"]["groupState"], "Exists")


if __name__ == "__main__":
    unittest.main(verbosity=1)

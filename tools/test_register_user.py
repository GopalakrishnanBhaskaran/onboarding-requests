#!/usr/bin/env python3
"""Tests for tools/register_user.py on an in-memory stand-in for Cosmos (no Azure, no SDK needed).

  python3 tools/test_register_user.py
"""
from __future__ import annotations

import sys
import types
from pathlib import Path


class NotFound(Exception):
    pass


# The tool imports CosmosResourceNotFoundError lazily; stand in for the SDK module.
exceptions = types.ModuleType("azure.cosmos.exceptions")
exceptions.CosmosResourceNotFoundError = NotFound
sys.modules.setdefault("azure", types.ModuleType("azure"))
sys.modules.setdefault("azure.cosmos", types.ModuleType("azure.cosmos"))
sys.modules["azure.cosmos.exceptions"] = exceptions

sys.path.insert(0, str(Path(__file__).resolve().parent))
import register_user as ru  # noqa: E402


class Container:
    def __init__(self, docs: dict[str, dict], key: str):
        self.docs, self.key = docs, key

    def read_item(self, item: str, partition_key: str) -> dict:
        d = self.docs.get(item)
        if d is None or d[self.key] != partition_key:
            raise NotFound(item)
        return d

    def upsert_item(self, doc: dict) -> None:
        self.docs[doc["id"]] = doc

    def delete_item(self, item: str, partition_key: str) -> None:
        self.read_item(item, partition_key)
        del self.docs[item]

    def query_items(self, query: str, parameters: list, enable_cross_partition_query: bool):
        assert "c.projectId = @p" in query and enable_cross_partition_query
        return [d for d in self.docs.values() if d["projectId"] == parameters[0]["value"]]


class Db:
    def __init__(self):
        self.c = {"projects": Container({"PEST": {"id": "PEST", "projectId": "PEST"}}, "projectId"), "userAccess": Container({}, "userId")}

    def get_container_client(self, name: str) -> Container:
        return self.c[name]


def run() -> None:
    db = Db()
    assert ru.register(db, "PEST", "Jane.Doe@Sample.invalid", ["Requester", "Owner"], False, False) == [
        "registered signin:jane.doe@sample.invalid on PEST: Requester, Owner"]
    doc = db.c["userAccess"].docs["signin:jane.doe@sample.invalid:PEST"]
    assert (doc["userId"], doc["isSample"]) == ("signin:jane.doe@sample.invalid", False)
    ru.register(db, "PEST", "0123abcd0123abcd0123abcd0123abcd", ["Security"], False, False)  # by user id
    assert sorted(ru.register(db, "PEST", None, [], False, True)) == [
        "0123abcd0123abcd0123abcd0123abcd: Security", "signin:jane.doe@sample.invalid: Requester, Owner"]
    ru.register(db, "PEST", "jane.doe@sample.invalid", ["Security"], False, False)  # again: replaces the roles
    assert db.c["userAccess"].docs["signin:jane.doe@sample.invalid:PEST"]["roles"] == ["Security"]
    assert ru.register(db, "PEST", "jane.doe@sample.invalid", [], True, False) == ["removed signin:jane.doe@sample.invalid from PEST"]
    assert ru.register(db, "PEST", "jane.doe@sample.invalid", [], True, False) == ["signin:jane.doe@sample.invalid was not registered on PEST"]
    for bad in (("VESTACY", "jane@sample.invalid", ["Owner"]), ("PEST", "jane@sample.invalid", ["Approver"]), ("PEST", "live.com#j@sample.invalid", ["Owner"])):
        try:
            ru.register(db, bad[0], bad[1], bad[2], False, False)
            raise AssertionError(f"accepted {bad}")
        except SystemExit:
            pass
    print("register_user: all checks passed")


if __name__ == "__main__":
    run()

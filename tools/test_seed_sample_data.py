#!/usr/bin/env python3
"""Tests for tools/seed_sample_data.py --remove-sample on an in-memory stand-in for Cosmos (no Azure).

  python3 tools/test_seed_sample_data.py
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import seed_sample_data as seed  # noqa: E402


class Container:
    def __init__(self, name: str, key: str, docs: list[dict]):
        self.name, self.key, self.docs, self.deleted = name, key, docs, []

    def query_items(self, query: str, enable_cross_partition_query: bool):
        assert "STARTSWITH(c.projectId, 'SAMPLE-')" in query and enable_cross_partition_query
        return [d for d in self.docs if str(d.get("projectId", "")).startswith("SAMPLE-") or d.get("isSample") is True]

    def delete_item(self, item: str, partition_key: str):
        doc = next(d for d in self.docs if d["id"] == item)
        assert doc[self.key] == partition_key, (doc, partition_key)
        self.docs.remove(doc)
        self.deleted.append(item)


class Db:
    def __init__(self, containers: list[Container]):
        self.c = {x.name: x for x in containers}

    def list_containers(self):
        return [{"id": x.name, "partitionKey": {"paths": [f"/{x.key}"]}} for x in self.c.values()]

    def get_container_client(self, name: str) -> Container:
        return self.c[name]


def run() -> None:
    db = Db([
        Container("projects", "projectId", [{"id": "SAMPLE-PEST", "projectId": "SAMPLE-PEST", "isSample": True},
                                            {"id": "PEST", "projectId": "PEST", "isSample": False}]),
        Container("mappingSheet", "projectId", [{"id": "P-PH-SUPPORT-READER-DEV", "projectId": "SAMPLE-PEST", "isSample": True},
                                                {"id": "PH-SUPPORT-READER-DEV", "projectId": "PEST", "isSample": False}]),
        Container("requests", "projectId", [{"id": "r1", "projectId": "SAMPLE-ALPHA"}, {"id": "r2", "projectId": "PEST"}]),
        Container("userAccess", "userId", [{"id": "u1:SAMPLE-ALPHA", "userId": "u1", "projectId": "SAMPLE-ALPHA", "isSample": True},
                                           {"id": "u1:PEST", "userId": "u1", "projectId": "PEST", "isSample": False}]),
    ])
    assert seed.remove_sample(db, really=False) == {"projects": 1, "mappingSheet": 1, "requests": 1, "userAccess": 1}
    assert all(not c.deleted for c in db.c.values())  # dry run deletes nothing
    seed.remove_sample(db, really=True)
    assert {n: [d["id"] for d in c.docs] for n, c in db.c.items()} == {
        "projects": ["PEST"], "mappingSheet": ["PH-SUPPORT-READER-DEV"], "requests": ["r2"], "userAccess": ["u1:PEST"]}
    assert not seed.is_sample({"projectId": "PEST", "isSample": False}) and seed.is_sample({"projectId": "SAMPLE-BETA"})
    print("seed_sample_data --remove-sample: all checks passed")


if __name__ == "__main__":
    run()

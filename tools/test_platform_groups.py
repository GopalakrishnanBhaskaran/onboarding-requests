#!/usr/bin/env python3
"""Tests for tools/fabric_groups.py against fake az / HTTP calls (no network, no Azure).

  python3 tools/test_platform_groups.py
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fabric_groups as fab  # noqa: E402

ACCOUNT = "00000000-0000-0000-0000-0000000000aa"
GROUPS = {"PH-DATAENGINEERS-DEV": {"id": "g-eng", "name": "PH-DATAENGINEERS-DEV", "security": True},
          "PH-ADMINS-DEV": {"id": "g-adm", "name": "PH-ADMINS-DEV", "security": True}}


def run(module, argv: list[str]) -> tuple[int, str]:
    out = io.StringIO()
    old = sys.argv
    sys.argv = [module.__file__, *argv]
    try:
        with contextlib.redirect_stdout(out):
            code = module.main()
    finally:
        sys.argv = old
    return code, out.getvalue()


def test_fabric() -> None:
    calls: list[tuple[str, str, dict | None]] = []
    ws_id = "11111111-1111-1111-1111-111111111111"
    roles = [{"principal": {"id": "g-adm", "displayName": "PH-ADMINS-DEV", "type": "Group"}, "role": "Viewer"}]

    def fake_http(method, url, bearer, body=None, retries=3):
        assert bearer == "tok-fab"
        calls.append((method, url, body))
        if url == f"{fab.FABRIC}/v1/workspaces":
            return {"value": [{"id": ws_id, "displayName": "PMO Fabric test", "capacityId": "cap"}], "continuationUri": f"{fab.FABRIC}/v1/workspaces?page=2"}
        if url == f"{fab.FABRIC}/v1/workspaces?page=2":
            return {"value": [{"id": "2", "displayName": "Other"}]}
        if url.endswith("/roleAssignments") and method == "GET":
            return {"value": list(roles)}
        if url.endswith("/roleAssignments") and method == "POST":
            roles.append({"principal": body["principal"], "role": body["role"]})
            return {}
        raise AssertionError((method, url))

    fab.http, fab.token = fake_http, (lambda resource: "tok-fab" if resource == fab.FABRIC else "x")
    fab.entra_group = lambda key: GROUPS[key]
    args = ["--workspace", "PMO Fabric test", "--group", "PH-DATAENGINEERS-DEV=Contributor", "--group", "PH-ADMINS-DEV"]

    code, out = run(fab, args)
    assert code == 0, out
    assert all(m == "GET" for m, _, _ in calls), calls
    assert "PH-DATAENGINEERS-DEV (g-eng): add Contributor" in out and "PH-ADMINS-DEV (g-adm): keep Viewer" in out, out

    calls.clear()
    with tempfile.TemporaryDirectory() as tmp:
        fab.ROOT = tmp
        code, out = run(fab, [*args, "--approved-by", "Gokul 31 Oct 2026"])
    assert code == 0, out
    posts = [b for m, _, b in calls if m == "POST"]
    assert posts == [{"principal": {"id": "g-eng", "type": "Group"}, "role": "Contributor"}], posts
    assert "PASS PH-DATAENGINEERS-DEV: Contributor" in out and "PASS PH-ADMINS-DEV: Viewer" in out, out

    try:
        run(fab, ["--workspace", "PMO Fabric test", "--group", "PH-ADMINS-DEV=Admin"])
        raise AssertionError("Admin accepted")
    except ValueError:
        pass


if __name__ == "__main__":
    fab.self_test()
    test_fabric()
    print("test_platform_groups: all passed")

#!/usr/bin/env python3
"""Shared WAT state: purview-dbx/state/state.json, the only hand-off between agents.

Every write takes an exclusive file lock (state.json.lock), so agents running in
parallel never lose each other's updates. The first call copies state.template.json.

Usage:
  state.py get <dotted.key>                      print value (JSON; plain text for strings)
  state.py set <dotted.key> <json-value>         e.g. set network.transit.cidr '"10.120.0.0/22"'
  state.py set-file <dotted.key> <file.json>     store a JSON file under a key
  state.py lock <resource> <agent>               exit 3 if another agent holds it
  state.py unlock <resource> <agent>             only the holder can unlock
  state.py step <agent> <step> <status> [note]   status: pending|running|done|failed|skipped
  state.py error <agent> <message>               record a failure; the orchestrator decides
  state.py show                                  whole state
  state.py self-test                             offline checks of this file
"""
import fcntl
import json
import os
import sys
import tempfile
import time
from contextlib import contextmanager

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STATE = os.environ.get("PVD_STATE", os.path.join(ROOT, "state", "state.json"))
TEMPLATE = os.path.join(ROOT, "state", "state.template.json")


def now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


@contextmanager
def locked():
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    with open(STATE + ".lock", "a") as lf:
        fcntl.flock(lf, fcntl.LOCK_EX)
        try:
            if not os.path.exists(STATE):
                with open(TEMPLATE) as t:
                    data = json.load(t)
                data["created_at"] = now()
            else:
                with open(STATE) as f:
                    data = json.load(f)
            yield data
            data["updated_at"] = now()
            fd, tmp = tempfile.mkstemp(dir=os.path.dirname(STATE), prefix=".state-")
            with os.fdopen(fd, "w") as f:
                json.dump(data, f, indent=2, sort_keys=False)
                f.write("\n")
            os.chmod(tmp, 0o600)
            os.replace(tmp, STATE)
        finally:
            fcntl.flock(lf, fcntl.LOCK_UN)


def walk(data, key, create=False):
    parts = key.split(".")
    cur = data
    for p in parts[:-1]:
        if p not in cur or not isinstance(cur[p], dict):
            if not create:
                return None, None
            cur[p] = {}
        cur = cur[p]
    return cur, parts[-1]


def cmd_get(key):
    with locked() as d:
        parent, last = walk(d, key)
        val = None if parent is None else parent.get(last)
    if isinstance(val, str):
        print(val)
    else:
        print(json.dumps(val, indent=2))


def cmd_set(key, value):
    with locked() as d:
        parent, last = walk(d, key, create=True)
        parent[last] = value


def cmd_lock(resource, agent):
    with locked() as d:
        locks = d.setdefault("locks", {})
        cur = locks.get(resource)
        if cur and cur.get("locked_by") != agent:
            print(f"LOCKED: {resource} is held by {cur['locked_by']} since {cur['since']}", file=sys.stderr)
            sys.exit(3)
        locks[resource] = {"locked_by": agent, "since": now()}


def cmd_unlock(resource, agent):
    with locked() as d:
        locks = d.setdefault("locks", {})
        cur = locks.get(resource)
        if cur and cur.get("locked_by") != agent:
            print(f"NOT HOLDER: {resource} is held by {cur['locked_by']}", file=sys.stderr)
            sys.exit(3)
        locks.pop(resource, None)


def cmd_step(agent, step, status, note=""):
    if status not in ("pending", "running", "done", "failed", "skipped", "blocked"):
        sys.exit(f"bad status {status}")
    with locked() as d:
        a = d.setdefault("agents", {}).setdefault(agent, {"steps": {}})
        s = a.setdefault("steps", {}).setdefault(step, {})
        if status == "running" and "started_at" not in s:
            s["started_at"] = now()
        if status in ("done", "failed", "skipped", "blocked"):
            s["finished_at"] = now()
        s["status"] = status
        if note:
            s["note"] = note


def cmd_error(agent, message):
    with locked() as d:
        d.setdefault("errors", []).append({"at": now(), "agent": agent, "message": message, "decision": None})
        a = d.setdefault("agents", {}).setdefault(agent, {"steps": {}})
        a["status"] = "failed"


def self_test():
    global STATE
    with tempfile.TemporaryDirectory() as tmp:
        STATE = os.path.join(tmp, "state.json")
        cmd_set("network.transit.cidr", "10.120.0.0/22")
        with locked() as d:
            assert d["network"]["transit"]["cidr"] == "10.120.0.0/22"
            assert d["inputs"]["workspace_id"] == "7405607339118600"
        cmd_lock("vnet:transit", "B")
        cmd_lock("vnet:transit", "B")  # re-entrant for the holder
        try:
            cmd_lock("vnet:transit", "C")
            raise AssertionError("lock by another agent must fail")
        except SystemExit as e:
            assert e.code == 3
        cmd_unlock("vnet:transit", "B")
        cmd_lock("vnet:transit", "C")
        cmd_step("A", "vnet", "running")
        cmd_step("A", "vnet", "done", "ok")
        cmd_error("D", "SAMPLE failure")
        with locked() as d:
            assert d["agents"]["A"]["steps"]["vnet"]["status"] == "done"
            assert d["errors"][0]["agent"] == "D"
            assert d["locks"]["vnet:transit"]["locked_by"] == "C"
        assert oct(os.stat(STATE).st_mode & 0o777) == "0o600"
    print("state.py self-test: PASS")


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        sys.exit(2)
    c, a = argv[1], argv[2:]
    if c == "get" and len(a) == 1:
        cmd_get(a[0])
    elif c == "set" and len(a) == 2:
        cmd_set(a[0], json.loads(a[1]))
    elif c == "set-file" and len(a) == 2:
        with open(a[1]) as f:
            cmd_set(a[0], json.load(f))
    elif c == "lock" and len(a) == 2:
        cmd_lock(*a)
    elif c == "unlock" and len(a) == 2:
        cmd_unlock(*a)
    elif c == "step" and len(a) in (3, 4):
        cmd_step(*a)
    elif c == "error" and len(a) == 2:
        cmd_error(*a)
    elif c == "show":
        with locked() as d:
            print(json.dumps(d, indent=2))
    elif c == "self-test":
        self_test()
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main(sys.argv)

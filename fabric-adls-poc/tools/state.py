#!/usr/bin/env python3
"""state/state.json: handoff + locks between the POC sub-agents (A-E).

Commands
  show                                  print state
  set <dotted.key> <json-or-text>       set a value (e.g. env.storageAccount mystg)
  lock <resource> <agent>               take a lock (fails if held by another agent)
  unlock <resource> <agent>             release a lock
  agent <A-E> <status> [note]           update an agent's status (pending|running|blocked|done|failed)
  handoff <from> <to> <text>            append a handoff note
  approve <wave> --by "<name> <date>"   record an explicit approval for a wave (1|2|3)
  check-approval <wave>                 exit 0 only if the wave is approved
  self-test                             run the built-in tests on a temp file
No secrets, tokens or passwords ever go into this file.
"""
import fcntl
import json
import os
import sys
import tempfile
from contextlib import contextmanager
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE = os.path.join(HERE, "..", "state", "state.json")
# Same working copy as tools/lib.sh (RAW_DIR = fabric-adls-poc/.local): approvals written here are the ones the tools read.
DEFAULT = os.environ.get("STATE_FILE") or os.path.join(HERE, "..", ".local", "state.json")
FORBIDDEN = ("password", "secret", "token", "accountkey", "sig=")


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


@contextmanager
def locked(path):
    with open(path + ".lock", "w") as lf:
        fcntl.flock(lf, fcntl.LOCK_EX)
        try:
            with open(path) as f:
                data = json.load(f)
            yield data
            data["updated"] = now()
            tmp = path + ".tmp"
            with open(tmp, "w") as f:
                json.dump(data, f, indent=2)
                f.write("\n")
            os.replace(tmp, path)
        finally:
            fcntl.flock(lf, fcntl.LOCK_UN)


def guard(text):
    low = str(text).lower()
    for word in FORBIDDEN:
        if word in low:
            raise SystemExit(f"refused: value looks like it carries a secret ({word})")


def set_dotted(data, key, value):
    parts = key.split(".")
    cur = data
    for p in parts[:-1]:
        cur = cur.setdefault(p, {})
    cur[parts[-1]] = value


def parse_value(raw):
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def _gen(p):
    try:
        with open(p) as f:
            return json.load(f).get("stateGeneration", 1)
    except (OSError, ValueError):
        return 0


def seed_working_copy(path):
    """Working copy outside git, seeded from the template; same rules as tools/lib.sh.
    A newer template generation replaces a stale copy (kept as .gen<N>.bak), except for a
    separate profile state (STATE_PROFILE set and not the template's profile, see cloud_init.sh)."""
    import shutil
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(TEMPLATE) as f:
        tprof = json.load(f).get("profile", "")
    prof = os.environ.get("STATE_PROFILE", "")
    if prof and prof != tprof:
        if not os.path.exists(path):
            raise SystemExit(f"no state for profile '{prof}': run  source fabric-adls-poc/tools/cloud_init.sh {prof}")
        return
    if not os.path.exists(path):
        shutil.copyfile(TEMPLATE, path)
    elif _gen(path) != _gen(TEMPLATE):
        shutil.copyfile(path, f"{path}.gen{_gen(path)}.bak")
        shutil.copyfile(TEMPLATE, path)
        print(f"state: working copy replaced by template generation {_gen(TEMPLATE)} (old copy kept as .bak)", file=sys.stderr)


def main(argv, path=DEFAULT):
    if path == DEFAULT:
        seed_working_copy(path)
    if not argv:
        print(__doc__)
        return 2
    cmd, args = argv[0], argv[1:]
    if cmd == "self-test":
        return self_test()
    if cmd == "show":
        with open(path) as f:
            print(f.read())
        return 0
    if cmd == "check-approval":
        with open(path) as f:
            data = json.load(f)
        w = data["waves"].get(args[0], {})
        return 0 if w.get("approvedBy") else 1
    with locked(path) as data:
        if cmd == "set":
            guard(args[0] + " " + args[1])
            set_dotted(data, args[0], parse_value(args[1]))
        elif cmd == "lock":
            res, agent = args
            holder = data["locks"].get(res)
            if holder and holder["agent"] != agent:
                raise SystemExit(f"lock {res} held by {holder['agent']} since {holder['since']}")
            data["locks"][res] = {"agent": agent, "since": now()}
        elif cmd == "unlock":
            res, agent = args
            holder = data["locks"].get(res)
            if holder and holder["agent"] != agent:
                raise SystemExit(f"lock {res} held by {holder['agent']}, not {agent}")
            data["locks"][res] = None
        elif cmd == "agent":
            agent, status = args[0], args[1]
            note = " ".join(args[2:])
            guard(note)
            if status not in ("pending", "running", "blocked", "done", "failed"):
                raise SystemExit("bad status")
            a = data["agents"][agent]
            a["status"], a["at"] = status, now()
            if note:
                a.setdefault("notes", []).append(f"{now()} {note}")
        elif cmd == "handoff":
            guard(" ".join(args[2:]))
            data["handoff"].append({"at": now(), "from": args[0], "to": args[1], "text": " ".join(args[2:])})
        elif cmd == "approve":
            wave = args[0]
            if len(args) < 3 or args[1] != "--by" or len(args[2].split()) < 2:
                raise SystemExit('usage: approve <wave> --by "<name> <date>"')
            data["waves"][wave]["approvedBy"] = args[2]
            data["waves"][wave]["approvedAt"] = now()
        else:
            print(__doc__)
            return 2
    return 0


def self_test():
    seed = {"updated": "", "waves": {"1": {"approvedBy": None}}, "locks": {"storage": None},
            "agents": {"A": {"status": "pending"}, "B": {"status": "pending"}}, "handoff": []}
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "s.json")
        with open(p, "w") as f:
            json.dump(seed, f)
        assert main(["check-approval", "1"], p) == 1
        assert main(["lock", "storage", "A"], p) == 0
        try:
            main(["lock", "storage", "B"], p)
            raise AssertionError("B must not take A's lock")
        except SystemExit as e:
            assert "held by A" in str(e)
        assert main(["unlock", "storage", "A"], p) == 0
        assert main(["lock", "storage", "B"], p) == 0
        try:
            main(["approve", "1", "--by", "Gokul"], p)
            raise AssertionError("approval needs name and date")
        except SystemExit:
            pass
        assert main(["approve", "1", "--by", "Gokul 2026-11-02"], p) == 0
        assert main(["check-approval", "1"], p) == 0
        try:
            main(["set", "env.x", "AccountKey=abc"], p)
            raise AssertionError("secret must be refused")
        except SystemExit as e:
            assert "refused" in str(e)
        assert main(["agent", "A", "done", "PEs approved"], p) == 0
        with open(p) as f:
            data = json.load(f)
        assert data["agents"]["A"]["status"] == "done"
    print("state.py self-test: 8/8 passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

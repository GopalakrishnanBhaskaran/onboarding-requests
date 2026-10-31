"""Shared helpers for tools/fabric_groups.py. Standard library only (Cloud Shell, Mac).

Tokens come from the signed-in Azure CLI (`az account get-access-token`), stay in memory and are never printed or saved.
Entra groups are resolved read-only with `az ad group show` (the signed-in admin's own Graph rights).
"""
from __future__ import annotations

import json
import re
import subprocess
import time
import urllib.error
import urllib.request

GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class ApiError(Exception):
    def __init__(self, status: int, method: str, url: str, body: str):
        super().__init__(f"{method} {url} -> HTTP {status}: {body[:400]}")
        self.status, self.body = status, body


def az(*args: str) -> str:
    """Run the Azure CLI; stdout stripped. Raises with az's own message on failure."""
    p = subprocess.run(["az", *args], capture_output=True, text=True)
    if p.returncode != 0:
        raise RuntimeError(f"az {' '.join(args[:3])} failed: {p.stderr.strip()[:400]}")
    return p.stdout.strip()


def token(resource: str) -> str:
    return az("account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv")


def http(method: str, url: str, bearer: str, body: dict | None = None, retries: int = 3) -> dict:
    """JSON call; waits out 429 Retry-After (at most `retries` times). Empty body -> {}."""
    data = json.dumps(body).encode() if body is not None else None
    for attempt in range(retries + 1):
        req = urllib.request.Request(url, data=data, method=method,
                                     headers={"Authorization": f"Bearer {bearer}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                raw = r.read().decode()
                return json.loads(raw) if raw.strip() else {}
        except urllib.error.HTTPError as e:
            text = e.read().decode(errors="replace")
            if e.code == 429 and attempt < retries:
                time.sleep(min(int(e.headers.get("Retry-After") or 30), 120))
                continue
            raise ApiError(e.code, method, url, text) from None
    raise AssertionError("unreachable")


def entra_group(key: str) -> dict:
    """{id, name, security} of an Entra group by object id or exact display name (az refuses an ambiguous name)."""
    out = az("ad", "group", "show", "--group", key, "--query", "{id:id, name:displayName, security:securityEnabled}", "-o", "json")
    return json.loads(out)


def parse_group(arg: str, allowed: tuple[str, ...], default: str) -> tuple[str, str]:
    """'NAME' or 'NAME=ROLE' -> (NAME, ROLE); ROLE must be one of `allowed` (case-insensitive, returned as spelled there)."""
    name, _, role = arg.partition("=")
    name, role = name.strip(), (role.strip() or default)
    match = next((a for a in allowed if a.lower() == role.lower()), None)
    if not name or match is None:
        raise ValueError(f"--group {arg!r}: expected <name|objectId>[={'|'.join(allowed)}]")
    return name, match


def record_approval(root: str, what: str, by: str) -> None:
    import os
    os.makedirs(os.path.join(root, ".local"), exist_ok=True)
    with open(os.path.join(root, ".local", "approval.txt"), "a") as f:
        f.write(f"Approval recorded ({what}): {by}\n")

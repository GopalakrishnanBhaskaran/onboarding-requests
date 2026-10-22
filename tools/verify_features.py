#!/usr/bin/env python3
"""Feature checklist for the live pilot: PASS / WAIT / FAIL per feature, with the next step. Read-only.

Called by tools/verify_features.sh, which collects the Azure facts (settings, permissions, resources) into a JSON file
and passes the Cosmos connection string in the environment. The verdicts come from `evaluate`, a pure function over
that data (tested with `--self-test`, no Azure needed).
"""
from __future__ import annotations

import json
import os
import sys
from collections import Counter
from dataclasses import dataclass

PASS, WAIT, FAIL = "PASS", "WAIT", "FAIL"
READ_ROLES = {"User.Read.All", "GroupMember.Read.All", "UserAuthenticationMethod.Read.All", "AuditLog.Read.All"}
CREATE_ROLES = {"User.Create", "Group.Create", "GroupMember.ReadWrite.All"}
RETIRED_ROLES = {"EntitlementManagement.ReadWrite.All", "User.ReadWrite.All", "LicenseAssignment.ReadWrite.All",
                 "UserAuthenticationMethod.ReadWrite.All", "Group.ReadWrite.All", "EntitlementManagement.Read.All", "User.ReadBasic.All"}
READER = "acdd72a7-3385-48ef-bd42-f606fba81ae7"


@dataclass
class Result:
    feature: str
    status: str
    detail: str
    next: str = ""


def steps(r: dict) -> list[str]:
    return [e.get("step", "") for e in ((r.get("flow") or {}).get("events") or [])]


def status(r: dict) -> str:
    return (r.get("flow") or {}).get("status") or "legacy-v1"


def in_scope(scope: str, allowed: list[str]) -> bool:
    s = (scope or "").rstrip("/").lower()
    return any(a and (s == a.rstrip("/").lower() or s.startswith(a.rstrip("/").lower() + "/")) for a in allowed)


def evaluate(ctx: dict, projects: list[dict], rows: list[dict], requests: list[dict], notes: list[dict], issues: list[dict]) -> list[Result]:
    out: list[Result] = []
    s = ctx.get("worker_settings") or {}
    swa = ctx.get("swa_settings") or {}
    on = lambda k: str(s.get(k, "")).lower() == "true"  # noqa: E731
    allowed_scopes = [x for x in str(s.get("ROLE_ASSIGN_SCOPES", "")).split(",") if x.strip()]
    tenant = (ctx.get("tenant_id") or "").lower()
    v2 = [r for r in requests if r.get("flow")]

    # 1. Code
    head, app, worker = ctx.get("head", "?"), ctx.get("app_deployed") or "unknown", ctx.get("worker_deployed") or "unknown"
    stale = [n for n, k in (("app", "app_code_changed"), ("worker", "worker_code_changed")) if ctx.get(k, True)]
    out.append(Result("1. Latest code deployed (app + worker)", PASS if not stale else WAIT,
                      f"checkout {head}; app {app}; worker {worker}",
                      "" if not stale else " and ".join(f"tools/deploy_free.sh {n} <sub>" for n in stale)))

    # 2. Environment settings
    env, redirect = s.get("APP_ENVIRONMENT", ""), s.get("NOTIFY_REDIRECT_TO", "")
    ok_env = env in ("dev", "test") and bool(redirect) and str(s.get("AGENT_TENANT_ID", "")).lower() == tenant and tenant != ""
    same_swa = swa.get("APP_ENVIRONMENT") == env and swa.get("NOTIFY_REDIRECT_TO", "") == redirect
    out.append(Result("2. Environment-specific settings (test env, mail redirect, agent tenant)",
                      PASS if ok_env and same_swa else WAIT,
                      f"worker APP_ENVIRONMENT={env or '-'}, NOTIFY_REDIRECT_TO={redirect or '-'}, AGENT_TENANT_ID {'= this tenant' if str(s.get('AGENT_TENANT_ID','')).lower() == tenant else 'missing/different'}; website APP_ENVIRONMENT={swa.get('APP_ENVIRONMENT', '-')}",
                      "" if ok_env and same_swa else "tools/deploy_free.sh environment <sub> test --redirect <your mail>"))

    # 3. Mail switched on
    mail_on = s.get("NOTIFY_SENDER") == "AcsEmail" and s.get("ACS_ENDPOINT") and s.get("NOTIFY_FROM")
    infra = ctx.get("acs_exists") and ctx.get("acs_role")
    out.append(Result("3. Mail switched on (Azure Communication Services, managed identity)",
                      PASS if mail_on and infra and swa.get("NOTIFY_SENDER") == "AcsEmail" else (FAIL if mail_on and not infra else WAIT),
                      f"worker NOTIFY_SENDER={s.get('NOTIFY_SENDER', '-')}, from {s.get('NOTIFY_FROM') or '-'}; ACS resource {'yes' if ctx.get('acs_exists') else 'no'}; "
                      f"worker role on it {'yes' if ctx.get('acs_role') else 'no'}; website shows mail {'on' if swa.get('NOTIFY_SENDER') == 'AcsEmail' else 'off'}",
                      "" if mail_on and infra and swa.get("NOTIFY_SENDER") == "AcsEmail"
                      else "tools/deploy_free.sh app <sub> (website mirrors the worker)" if mail_on and infra
                      else "tools/deploy_free.sh email <sub> --redirect <mail> (dry run → approval)"))

    # 4. Mail delivered
    by = Counter(n.get("status") for n in notes)
    errors = Counter(n.get("lastError") for n in notes if n.get("lastError") and n.get("lastError") != "NoRecipients")
    delivered = Counter(a for n in notes for a in (n.get("deliveredTo") or []))
    st = PASS if by.get("Sent") else (FAIL if by.get("Failed") else WAIT)
    out.append(Result("4. Notifications mailed (Security, Owner, requester, joiner's Enterprise ID)", st,
                      f"by status {dict(by) or 'none'}; delivered to {dict(delivered) or 'nobody yet'}; errors {dict(errors) or 'none'}",
                      "" if st == PASS else "MailForbidden = the worker's mail role has not reached it yet (retried 3x); check spam; re-run later"
                      if errors else "create or approve a request (any new notification is mailed within 5 min)"))

    # 5. Agent provisioning rights
    roles = set(ctx.get("graph_roles") or [])
    rbac = [x for x in (ctx.get("rbac_admin_scopes") or []) if in_scope(x, allowed_scopes) or any(in_scope(a, [x]) for a in allowed_scopes)]
    prov_ok = on("AGENT_PROVISIONING") and on("AGENT_ROLE_ASSIGNMENT") and CREATE_ROLES <= roles and READ_ROLES <= roles and rbac \
        and READER in str(s.get("ROLE_ASSIGN_ALLOWED_ROLES", "")).lower()
    retired = sorted(roles & RETIRED_ROLES)
    out.append(Result("5. Agent may create user/group/membership and assign Reader (v2.1)", PASS if prov_ok and not retired else (FAIL if retired else WAIT),
                      f"settings {'on' if on('AGENT_PROVISIONING') and on('AGENT_ROLE_ASSIGNMENT') else 'off'}; Graph create roles "
                      f"{'all' if CREATE_ROLES <= roles else sorted(CREATE_ROLES - roles)}; read roles {'all' if READ_ROLES <= roles else sorted(READ_ROLES - roles)}; "
                      f"RBAC Administrator at {rbac or 'no allowed scope'}; retired roles {retired or 'none'}",
                      "" if prov_ok and not retired else "tools/deploy_free.sh revoke-graph-writes <sub> --approved-by …" if retired
                      else "tools/deploy_free.sh provisioning <sub> on /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access --approved-by \"Gokul 17 Oct 2026\""))

    # 6. Tenant separation + environments
    p = {x.get("projectId"): x for x in projects}
    pest, ves = p.get("SAMPLE-PEST") or {}, p.get("SAMPLE-VESTACY") or {}
    connected = lambda x: not x.get("tenantId") or str(x.get("tenantId")).lower() == tenant  # noqa: E731
    sep_ok = bool(pest) and bool(ves) and connected(pest) and not connected(ves)
    envs_ok = (pest.get("environments") or []) == ["Dev", "PreProd", "Prod"]
    out.append(Result("6. Tenant separation (PEST in the agent's tenant, Vestacy separate) and environments", PASS if sep_ok and envs_ok else WAIT,
                      f"SAMPLE-PEST {'connected' if pest and connected(pest) else 'missing/not connected'} envs {pest.get('environments') or '-'}; "
                      f"SAMPLE-VESTACY {'separate' if ves and not connected(ves) else 'missing/connected'}",
                      "" if sep_ok and envs_ok else "re-run the seed (step 3 of docs/NEXT_SESSION.md)"))
    ves_handoff = [r["id"][:8] for r in v2 if r.get("projectId") == "SAMPLE-VESTACY" and any("separate from the AI agent" in (e.get("note") or "")
                   for e in r["flow"].get("events") or [])]
    if ves_handoff:
        out.append(Result("6b. Vestacy request handed to the teams (agent did not touch it)", PASS, f"requests {ves_handoff}"))

    # 7. Mapping sheet from the workbooks (SAMPLE PEST) + Azure roles
    prow = {r.get("rowId"): r for r in rows if r.get("projectId") == "SAMPLE-PEST"}
    dev = prow.get("P-PH-SUPPORT-READER-DEV") or {}
    dev_scopes = [g.get("scope") for g in dev.get("azureRoles") or []]
    rows_ok = bool(dev) and dev_scopes and all(x and in_scope(x, allowed_scopes) for x in dev_scopes) \
        and (prow.get("P-PH-SUPPORT-READER-PRD") or {}).get("flag") == "Red"
    out.append(Result("7. Workbook-style mapping rows (environments, red/amber, Azure roles x resources)", PASS if rows_ok else WAIT,
                      f"{len(prow)} SAMPLE-PEST rows; Dev support-reader scopes {dev_scopes or 'none'}; Prod row red: {(prow.get('P-PH-SUPPORT-READER-PRD') or {}).get('flag') == 'Red'}",
                      "" if rows_ok else "seed with --access-scope /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access"))
    fin = next((r for r in rows if r.get("projectId") == "SAMPLE-ALPHA" and r.get("rowId") == "A-FIN-VIEW"), {})
    fin_roles = fin.get("azureRoles") or ([{"scope": fin.get("azureScope")}] if fin.get("azureRoleDefinitionId") else [])
    fin_ok = bool(fin_roles) and all(g.get("scope") and in_scope(g["scope"], allowed_scopes) for g in fin_roles)
    out.append(Result("7b. A-FIN-VIEW has the Azure role the agent assigns (test A)", PASS if fin_ok else WAIT,
                      f"{len(fin_roles)} Azure role(s): {[g.get('scope') for g in fin_roles] or 'none'}",
                      "" if fin_ok else "Mapping sheet → Azure roles the AI agent assigns → A-FIN-VIEW: Reader | rg-pmo-sample-access | /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access"))

    # 8–11. Flow evidence
    def evidence(feature: str, step: str, hint: str, project: str | None = None) -> None:
        hits = [r for r in v2 if step in steps(r) and (project is None or r.get("projectId") == project)]
        out.append(Result(feature, PASS if hits else WAIT,
                          f"requests {[r['id'][:8] + ' ' + status(r) for r in hits][:6]}" if hits else "no request has reached this yet", "" if hits else hint))

    evidence("8. Owner approval given in the app", "owner-approved", "the named Owner opens the request, ticks 'I have read…', Approve business need")
    evidence("9. Security approval given in the app", "security-approved", "a Security member (not the Owner approver) approves")
    evidence("10. Agent created user / group / membership", "agent-provisioned",
             "after both approvals wait 5–10 min; 'agent-waiting-permission' = new permission not in the token yet (up to 24 h)")
    evidence("11. Agent assigned the Azure role(s)", "agent-role-assigned", "after the ID team records the TAP metadata, wait 5–10 min")
    waiting = [r["id"][:8] for r in v2 if "agent-waiting-permission" in steps(r) and status(r) in ("WithIdTeam", "WithRoleTeam")]
    if waiting:
        out.append(Result("10b. Agent waiting for its new permission (F61)", WAIT, f"requests {waiting}", "retried every 5 min for up to 24 h"))
    manual = [(r["id"][:8], (e.get("note") or "")[:120]) for r in v2 for e in r["flow"].get("events") or [] if e.get("step") == "agent-manual"]
    if manual:
        out.append(Result("10c. Work the agent left to a team (with reason)", PASS, "; ".join(f"{i}: {n}" for i, n in manual[-4:])))

    # 12. End to end
    done = [r for r in v2 if status(r) == "Completed"]
    pest_done = [r for r in done if r.get("projectId") == "SAMPLE-PEST"]
    a = next((r for r in v2 if r["id"].startswith("05616a80")), None)
    out.append(Result("12. End to end to Completed (test A 05616a80)", PASS if a and status(a) == "Completed" else WAIT,
                      f"05616a80 is {status(a) if a else 'not found'} (waiting on {(a or {}).get('flow', {}).get('waitingOn', '-')})",
                      "" if a and status(a) == "Completed" else "follow docs/NEXT_SESSION.md step 6"))
    out.append(Result("12b. End to end to Completed (test B, SAMPLE PEST)", PASS if pest_done else WAIT,
                      f"PEST requests: {[r['id'][:8] + ' ' + str(r.get('environment')) + ' ' + status(r) for r in v2 if r.get('projectId') == 'SAMPLE-PEST'][:6] or 'none yet'}",
                      "" if pest_done else "New request in SAMPLE PEST data platform (Dev / SAMPLE PEST Platform (ph) / Support Reader)"))
    prod_returned = [r["id"][:8] for r in v2 if r.get("projectId") == "SAMPLE-PEST" and r.get("environment") == "Prod" and status(r) == "Returned"]
    if prod_returned:
        out.append(Result("12c. PEST Prod request returned (red row)", PASS, f"requests {prod_returned}"))

    # 13. Open high issues
    high = [i for i in issues if i.get("status") == "Open" and i.get("severity") == "High"]
    out.append(Result("13. No open High issues", PASS if not high else WAIT,
                      "; ".join(f"{str(i.get('requestId'))[:8]} {i.get('kind')}: {(i.get('summary') or '')[:90]}" for i in high[:5]) or "none",
                      "" if not high else "Issues and reports page; SLA escalations clear once the stage moves on"))
    return out


def print_results(results: list[Result]) -> None:
    mark = {PASS: "[PASS]", WAIT: "[WAIT]", FAIL: "[FAIL]"}
    for r in results:
        print(f"{mark[r.status]} {r.feature}\n        {r.detail}")
        if r.next:
            print(f"        next: {r.next}")
    c = Counter(r.status for r in results)
    print(f"\nSummary: {c.get(PASS, 0)} PASS, {c.get(WAIT, 0)} WAIT, {c.get(FAIL, 0)} FAIL of {len(results)}. "
          + ("All features verified live." if c.get(PASS, 0) == len(results) else "WAIT = a step still to do (see 'next'); FAIL = fix first."))


def load() -> tuple:
    from azure.cosmos import CosmosClient
    db = CosmosClient.from_connection_string(os.environ["COSMOS_CONNECTION_STRING"]).get_database_client("pmoreview")

    def q(container: str, sql: str) -> list:
        try:
            return list(db.get_container_client(container).query_items(sql, enable_cross_partition_query=True))
        except Exception as e:  # noqa: BLE001 - a missing container reads as empty
            print(f"  ({container} not readable: {type(e).__name__})")
            return []

    return (q("projects", "SELECT c.projectId, c.tenantId, c.tenantName, c.environments FROM c"),
            q("mappingSheet", "SELECT c.projectId, c.rowId, c.flag, c.azureRoles, c.azureRoleDefinitionId, c.azureScope FROM c"),
            q("requests", "SELECT c.id, c.projectId, c.environment, c.flow.status, c.flow.waitingOn, c.flow.events FROM c"),
            q("notifications", "SELECT c.status, c.lastError, c.deliveredTo, c.kind FROM c"),
            q("issues", "SELECT c.requestId, c.kind, c.severity, c.status, c.summary FROM c"))


def flatten(requests: list[dict]) -> list[dict]:
    """The query returns status/waitingOn/events at the top level; rebuild the flow object."""
    out = []
    for r in requests:
        flow = None if r.get("status") is None else {"status": r.get("status"), "waitingOn": r.get("waitingOn"), "events": r.get("events") or []}
        out.append({"id": r["id"], "projectId": r.get("projectId"), "environment": r.get("environment"), "flow": flow})
    return out


def self_test() -> None:
    allowed = "/subscriptions/s/resourceGroups/rg-pmo-sample-access"
    ctx = {"head": "abc", "app_deployed": "abc", "worker_deployed": "abc", "app_code_changed": False, "worker_code_changed": False,
           "tenant_id": "t1", "acs_exists": True, "acs_role": True, "graph_roles": sorted(READ_ROLES | CREATE_ROLES), "rbac_admin_scopes": [allowed],
           "worker_settings": {"APP_ENVIRONMENT": "test", "NOTIFY_REDIRECT_TO": "x@y.z", "AGENT_TENANT_ID": "T1", "NOTIFY_SENDER": "AcsEmail",
                               "ACS_ENDPOINT": "https://a", "NOTIFY_FROM": "DoNotReply@d", "AGENT_PROVISIONING": "true", "AGENT_ROLE_ASSIGNMENT": "true",
                               "ROLE_ASSIGN_ALLOWED_ROLES": READER, "ROLE_ASSIGN_SCOPES": allowed},
           "swa_settings": {"APP_ENVIRONMENT": "test", "NOTIFY_REDIRECT_TO": "x@y.z", "NOTIFY_SENDER": "AcsEmail"}}
    projects = [{"projectId": "SAMPLE-PEST", "tenantId": None, "environments": ["Dev", "PreProd", "Prod"]},
                {"projectId": "SAMPLE-VESTACY", "tenantId": "00000000-0000-4000-8000-000000000001", "environments": ["Dev"]}]
    rows = [{"projectId": "SAMPLE-PEST", "rowId": "P-PH-SUPPORT-READER-DEV", "azureRoles": [{"scope": allowed}]},
            {"projectId": "SAMPLE-PEST", "rowId": "P-PH-SUPPORT-READER-PRD", "flag": "Red"},
            {"projectId": "SAMPLE-ALPHA", "rowId": "A-FIN-VIEW", "azureRoleDefinitionId": READER, "azureScope": allowed}]
    ev = lambda *s: [{"step": x, "note": ""} for x in s]  # noqa: E731
    all_steps = ev("owner-approved", "security-approved", "agent-provisioned", "agent-role-assigned", "completed")
    requests = [{"id": "05616a80aaaa", "projectId": "SAMPLE-ALPHA", "flow": {"status": "Completed", "events": all_steps}},
                {"id": "bbbbbbbbbbbb", "projectId": "SAMPLE-PEST", "environment": "Dev", "flow": {"status": "Completed", "events": all_steps}}]
    notes = [{"status": "Sent", "deliveredTo": ["x@y.z"]}]
    res = evaluate(ctx, projects, rows, requests, notes, [])
    assert all(r.status == PASS for r in res), [(r.feature, r.status, r.detail) for r in res if r.status != PASS]

    # The live state of 21 Oct: pilot env, nothing mailed yet, 05616a80 awaiting Security.
    ctx2 = json.loads(json.dumps(ctx))
    ctx2["worker_settings"]["APP_ENVIRONMENT"] = "pilot"
    ctx2["swa_settings"] = {}
    requests2 = [{"id": "05616a80aaaa", "projectId": "SAMPLE-ALPHA", "flow": {"status": "AwaitingSecurity", "waitingOn": "Security", "events": ev("owner-approved")}}]
    res2 = {r.feature.split(".")[0] + "." + r.feature.split(".")[1][:2]: r for r in evaluate(ctx2, projects, rows, requests2, [{"status": "Recorded"}], [])}
    got = {k: v.status for k, v in res2.items()}
    assert got["2. E"] == WAIT and got["3. M"] == WAIT and got["4. N"] == WAIT and got["8. O"] == PASS and got["9. S"] == WAIT and got["12. E"] == WAIT, got
    assert "environment <sub> test" in res2["2. E"].next and "deploy_free.sh app" in res2["3. M"].next
    ctx3 = json.loads(json.dumps(ctx))
    ctx3["graph_roles"] = sorted(READ_ROLES | CREATE_ROLES | {"Group.ReadWrite.All"})
    assert next(r for r in evaluate(ctx3, projects, rows, requests, notes, []) if r.feature.startswith("5.")).status == FAIL
    assert flatten([{"id": "x", "status": "Completed", "events": [{"step": "completed"}]}])[0]["flow"]["status"] == "Completed"
    print("verify_features: self-test passed")


def main() -> int:
    if "--self-test" in sys.argv:
        self_test()
        return 0
    ctx = json.load(open(sys.argv[1], encoding="utf-8"))
    projects, rows, requests, notes, issues = load()
    print_results(evaluate(ctx, projects, rows, flatten(requests), notes, issues))
    return 0


if __name__ == "__main__":
    sys.exit(main())

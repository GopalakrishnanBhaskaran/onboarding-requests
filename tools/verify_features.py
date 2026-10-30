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
# The roles of the PEST Dev Support Reader row (response document), by lower-case name: the end-to-end test needs both.
TEST_ROLES = {"reader": READER, "storage blob data reader": "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"}
TAP_ROLE = "UserAuthMethod-TAP.ReadWrite.All"
# Built-in role ids by lower-case name (BuiltInRoles.Ids) and the ones the agent never assigns (BuiltInRoles.NeverByAgent).
from sheet_roles import CATALOGUE as ROLE_IDS, NEVER  # noqa: E402  (tools/ is the script's folder)
NEVER_BY_AGENT = set(NEVER)
# The privileged PEST row of the live test (Data Engineer, Dev).
PRIVILEGED_ROW = "PH-DATAENGINEERS-DEV"


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
    # A test environment redirects every mail to the tester; production (go-live) mails the real people, never redirected.
    ok_env = ((env in ("dev", "test") and bool(redirect)) or (env == "prod" and not redirect)) \
        and str(s.get("AGENT_TENANT_ID", "")).lower() == tenant and tenant != ""
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

    # 5. Agent provisioning rights (create + the roles the PEST test row needs)
    roles = set(ctx.get("graph_roles") or [])
    rbac = [x for x in (ctx.get("rbac_admin_scopes") or []) if in_scope(x, allowed_scopes) or any(in_scope(a, [x]) for a in allowed_scopes)]
    allowed_roles = str(s.get("ROLE_ASSIGN_ALLOWED_ROLES", "")).lower()
    prov_ok = on("AGENT_PROVISIONING") and on("AGENT_ROLE_ASSIGNMENT") and CREATE_ROLES <= roles and READ_ROLES <= roles and rbac \
        and all(r in allowed_roles for r in TEST_ROLES.values())
    retired = sorted(roles & RETIRED_ROLES)
    out.append(Result("5. Agent may create user/group/membership and assign Reader + Storage Blob Data Reader (v2.1)",
                      PASS if prov_ok and not retired else (FAIL if retired else WAIT),
                      f"settings {'on' if on('AGENT_PROVISIONING') and on('AGENT_ROLE_ASSIGNMENT') else 'off'}; Graph create roles "
                      f"{'all' if CREATE_ROLES <= roles else sorted(CREATE_ROLES - roles)}; read roles {'all' if READ_ROLES <= roles else sorted(READ_ROLES - roles)}; "
                      f"RBAC Administrator at {rbac or 'no allowed scope'}; allow-listed roles {[n for n, r in TEST_ROLES.items() if r in allowed_roles]}; retired roles {retired or 'none'}",
                      "" if prov_ok and not retired else "tools/deploy_free.sh revoke-graph-writes <sub> --approved-by …" if retired
                      else f"tools/deploy_free.sh provisioning <sub> on /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access {','.join(TEST_ROLES.values())} (dry run → approval)"))

    # 5b. Agent TAP (sign-in details mailed by the agent)
    tap_ok = on("AGENT_TAP") and TAP_ROLE in roles and on("AGENT_PROVISIONING") and s.get("NOTIFY_SENDER") in ("AcsEmail", "GraphMail")
    out.append(Result("5b. Agent issues the one-time TAP and mails it with the initial password and MFA steps", PASS if tap_ok else WAIT,
                      f"AGENT_TAP={s.get('AGENT_TAP') or 'false'} (lifetime {s.get('AGENT_TAP_LIFETIME_MINUTES') or '-'} min); {TAP_ROLE} {'held' if TAP_ROLE in roles else 'not held'}; "
                      f"website shows it {'on' if str(swa.get('AGENT_TAP', '')).lower() == 'true' else 'off'}",
                      "" if tap_ok else "tools/deploy_free.sh tap <sub> on (dry run → approval), then tools/deploy_free.sh app <sub>"))

    # 5c. Privileged rows by the agent (the PEST Data Engineer row: its roles must be on the allow-list)
    prow = next((r for r in rows if r.get("projectId") == "PEST" and r.get("rowId") == PRIVILEGED_ROW), None)
    pnames = sorted({str(g.get("roleName", "")).strip().lower() for g in ((prow or {}).get("azureRoles") or []) if g.get("roleName")})
    never = [n for n in pnames if n in NEVER_BY_AGENT]
    missing = [n for n in pnames if n not in NEVER_BY_AGENT and ROLE_IDS.get(n, n) not in allowed_roles]
    priv_ok = on("AGENT_PRIVILEGED") and str(swa.get("AGENT_PRIVILEGED", "")).lower() == "true" and prow is not None and not never and not missing
    flagged = f", flagged privileged: {bool(prow.get('privileged'))}" if prow else ""
    out.append(Result(f"5c. Agent provisions privileged rows too ({PRIVILEGED_ROW}: user, group, membership and its roles)", PASS if priv_ok else WAIT,
                      f"AGENT_PRIVILEGED={s.get('AGENT_PRIVILEGED') or 'false'} (website {swa.get('AGENT_PRIVILEGED') or 'false'}); row {'found' if prow else 'missing'}"
                      f"{flagged}; "
                      f"roles {pnames or '-'}; not on the allow-list {missing or 'none'}; never by the agent {never or 'none'}",
                      "" if priv_ok else
                      f"row {PRIVILEGED_ROW} has {', '.join(never)}: people assign it (the agent never does)" if never else
                      ("tools/deploy_free.sh provisioning <sub> on /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access --from-sheet PEST (dry run → approval; "
                       f"adds {', '.join(missing)})" if missing else "") +
                      ("; " if missing and not on("AGENT_PRIVILEGED") else "") +
                      ("tools/deploy_free.sh privileged <sub> on (dry run → approval)" if not on("AGENT_PRIVILEGED") else "")))

    # 6. Real tenants (from the shared workbooks), no SAMPLE data left
    p = {x.get("projectId"): x for x in projects}
    real = {k: v for k, v in p.items() if not str(k).startswith("SAMPLE-") and not v.get("isSample")}
    per = Counter(r.get("projectId") for r in rows if not r.get("isSample"))
    connected = lambda x: not x.get("tenantId") or str(x.get("tenantId")).lower() == tenant  # noqa: E731
    want = {"PEST": "PEST", "VESTACY": "Vestacy"}
    have = {k: real.get(k) for k in want}
    tenants_ok = all(v and per.get(k) for k, v in have.items())
    out.append(Result("6. PEST and Vestacy tenants in the app (one project each, rows from the workbooks)", PASS if tenants_ok else WAIT,
                      "; ".join(f"{k}: " + (f"tenant {v.get('tenantName')}, {'agent tenant' if connected(v) else 'separate tenant'}, envs {v.get('environments')}, "
                                            f"{per.get(k, 0)} rows, contacts {'set' if (v.get('contacts') or {}).get('security') else 'none'}" if v else "missing")
                                for k, v in have.items()),
                      "" if tenants_ok else "python3 tools/import_workbook.py --tenant PEST --project PEST … --apply (docs/NEXT_SESSION.md)"))
    # 6c. Contact inventory: the teams that approval requests and tasks are mailed to (Contact inventory page).
    def gaps(v: dict) -> list[str]:
        c = v.get("contacts") or {}
        return [label for key, label in (("security", "Security"), ("idTeam", "ID team"), ("roleTeam", "role assignment team")) if not c.get(key)]
    missing = {k: gaps(v) for k, v in real.items() if gaps(v)}
    out.append(Result("6c. Contact inventory: Security, ID team and role assignment team have mail addresses", PASS if real and not missing else WAIT,
                      "; ".join(f"{k}: {'missing ' + ', '.join(m) if (m := gaps(v)) else 'set'}"
                                f"{' (saved in the app, v' + str(v.get('contactsVersion')) + ')' if v.get('contactsVersion') else ' (imported)'}"
                                for k, v in sorted(real.items())) or "no real project",
                      "" if real and not missing else "PMO: Contact inventory page → each team's addresses, separated by semicolons → Save contacts"))
    sample = sorted(k for k in p if str(k).startswith("SAMPLE-") or p[k].get("isSample"))
    sample_rows = sum(1 for r in rows if r.get("isSample") or str(r.get("projectId", "")).startswith("SAMPLE-"))
    out.append(Result("6b. No SAMPLE projects or mappings left in the live app", PASS if not sample and not sample_rows else WAIT,
                      f"SAMPLE projects {sample or 'none'}; SAMPLE mapping rows {sample_rows}",
                      "" if not sample and not sample_rows else "python3 tools/seed_sample_data.py --remove-sample (lists) then --yes"))

    # 7. The PEST Dev Support Reader row the end-to-end test uses: every Azure role ready for the agent
    row = next((r for r in rows if r.get("projectId") == "PEST" and r.get("rowId") == "PH-SUPPORT-READER-DEV"), {})
    grants = row.get("azureRoles") or []
    ready = bool(grants) and all(g.get("scope") and in_scope(g["scope"], allowed_scopes) and (TEST_ROLES.get(g.get("roleName", "").lower()) or "-") in allowed_roles
                                 for g in grants)
    out.append(Result("7. PEST Dev 'Support Reader' row: group to be created, every Azure role in the agent's allow-list", PASS if ready else WAIT,
                      f"{row.get('entraGroup', 'row missing')}: {[(g.get('roleName'), (g.get('scope') or 'no scope').rsplit('/', 1)[-1]) for g in grants] or 'no roles'}",
                      "" if ready else "import PEST with --test-scope /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access, and step 5"))

    # 8–11. Flow evidence
    def evidence(feature: str, step: str, hint: str) -> None:
        hits = [r for r in v2 if step in steps(r) and not str(r.get("projectId", "")).startswith("SAMPLE-")]
        out.append(Result(feature, PASS if hits else WAIT,
                          f"requests {[r['id'][:8] + ' ' + status(r) for r in hits][:6]}" if hits else "no request has reached this yet", "" if hits else hint))

    evidence("8. Owner approval given in the app", "owner-approved", "the named Owner opens the request, ticks 'I have read…', Approve business need")
    evidence("9. Security approval given in the app", "security-approved", "a Security member (not the Owner approver) approves")
    evidence("10. Agent created user / group / membership", "agent-provisioned",
             "after both approvals wait 5–10 min; 'agent-waiting-permission' = new permission not in the token yet (up to 24 h)")
    # v1.1 F2: no copy of the mail is stored any more; the request records when and to whom (masked) it went. Older
    # deployments kept a masked notification (kind SignInDetails): still counted.
    agent_of = lambda r: ((r.get("flow") or {}).get("idTeamTask") or {}).get("agent") or {}  # noqa: E731
    mailed = [r for r in v2 if agent_of(r).get("signInDetailsSentAt")]
    sent = [n for n in notes if n.get("kind") == "SignInDetails" and n.get("status") == "Sent"]
    to = sorted({agent_of(r).get("signInDetailsSentTo") or "?" for r in mailed} | {a for n in sent for a in n.get("deliveredTo") or []})
    out.append(Result("10d. Agent mailed the sign-in details (one-time TAP, initial password, MFA steps)", PASS if mailed or sent else WAIT,
                      f"{len(mailed) + len(sent)} sent, to {to or 'nobody yet'}",
                      "" if mailed or sent else "needs step 5b, TAP_MAIL_SENDER and a people inventory entry; then a new request (the agent mails it in the run that creates the account)"))
    evidence("11. Agent assigned the Azure role(s)", "agent-role-assigned", "after the agent's own ID step is verified, wait 5–10 min")
    waiting = [r["id"][:8] for r in v2 if "agent-waiting-permission" in steps(r) and status(r) in ("WithIdTeam", "WithRoleTeam")]
    if waiting:
        out.append(Result("10b. Agent waiting for its new permission (F61)", WAIT, f"requests {waiting}", "retried every 5 min for up to 24 h"))
    manual = [(r["id"][:8], (e.get("note") or "")[:120]) for r in v2 for e in r["flow"].get("events") or [] if e.get("step") == "agent-manual"]
    if manual:
        out.append(Result("10c. Work the agent left to a team (with reason)", PASS, "; ".join(f"{i}: {n}" for i, n in manual[-4:])))

    # 12. End to end
    pest = [r for r in v2 if r.get("projectId") == "PEST"]
    done = [r for r in pest if status(r) == "Completed"]
    out.append(Result("12. End to end to Completed (PEST / Dev / Support Reader)", PASS if done else WAIT,
                      f"PEST requests: {[r['id'][:8] + ' ' + str(r.get('environment')) + ' ' + status(r) for r in pest][:6] or 'none yet'}",
                      "" if done else "New request → Tenant PEST, Dev, the (ph) platform resource, Support Reader (docs/NEXT_SESSION.md)"))
    prod_returned = [r["id"][:8] for r in pest if r.get("environment") == "Prod" and status(r) == "Returned"]
    if prod_returned:
        out.append(Result("12c. PEST Prod request returned (red row)", PASS, f"requests {prod_returned}"))

    # 14. Mail queue: nothing failed, nothing held back for over an hour (the Azure-managed domain sends 10 an hour, F70)
    from datetime import datetime, timedelta, timezone
    now = ctx.get("now") or datetime.now(timezone.utc).isoformat()
    def older(n: dict, hours: float) -> bool:
        try:
            return datetime.fromisoformat(str(n.get("createdAt")).replace("Z", "+00:00")) < datetime.fromisoformat(str(now).replace("Z", "+00:00")) - timedelta(hours=hours)
        except ValueError:
            return False
    failed = [n for n in notes if n.get("status") == "Failed"]
    backlog = [n for n in notes if n.get("status") == "Pending" and older(n, 1)]
    out.append(Result("14. Mail queue: no failed mail, none waiting over an hour", FAIL if failed else WAIT if backlog else PASS,
                      f"failed {len(failed)} ({sorted({str(n.get('lastError')) for n in failed}) or '-'}), waiting > 1 h {len(backlog)}, "
                      f"limit {s.get('MAIL_MAX_PER_HOUR') or '10 (Azure-managed domain)'} an hour",
                      "workflow_diag.sh: mail errors; MailForbidden = sender role; NoRecipients = Contact inventory" if failed
                      else "the queue drains at the mail limit; a verified custom mail domain raises it (F70)" if backlog else ""))

    # 15. Go-live: mails reach the real people (production: no redirect) and no team list is still only the test mailbox
    test_only = sorted(k for k, v in real.items() if redirect and (v.get("contacts") or {}).get("security") == [redirect.lower()])
    live_mail = env == "prod" and not redirect and swa.get("APP_ENVIRONMENT") == "prod" and not swa.get("NOTIFY_REDIRECT_TO")
    out.append(Result("15. Go-live: every mail goes to the real requester, joiner and teams (prod, no redirect)", PASS if live_mail and not missing else WAIT,
                      f"worker {env or '-'} redirect {redirect or 'none'}; website {swa.get('APP_ENVIRONMENT', '-')}; "
                      f"projects whose Security list is only the test mailbox: {test_only or 'none'}",
                      "" if live_mail and not missing else "fill the Contact inventory, then tools/deploy_free.sh environment <sub> prod (docs/GO_LIVE.md)"))

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

    return (q("projects", "SELECT c.projectId, c.tenantId, c.tenantName, c.environments, c.isSample, c.contacts, c.contactsVersion FROM c"),
            q("mappingSheet", "SELECT c.projectId, c.rowId, c.flag, c.entraGroup, c.azureRoles, c.isSample FROM c"),
            q("requests", "SELECT c.id, c.projectId, c.environment, c.flow.status, c.flow.waitingOn, c.flow.events FROM c"),
            q("notifications", "SELECT c.status, c.lastError, c.deliveredTo, c.kind, c.createdAt FROM c"),
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
           "tenant_id": "t1", "acs_exists": True, "acs_role": True, "graph_roles": sorted(READ_ROLES | CREATE_ROLES | {TAP_ROLE}), "rbac_admin_scopes": [allowed],
           "worker_settings": {"APP_ENVIRONMENT": "test", "NOTIFY_REDIRECT_TO": "x@y.z", "AGENT_TENANT_ID": "T1", "NOTIFY_SENDER": "AcsEmail",
                               "ACS_ENDPOINT": "https://a", "NOTIFY_FROM": "DoNotReply@d", "AGENT_PROVISIONING": "true", "AGENT_ROLE_ASSIGNMENT": "true",
                               "AGENT_TAP": "true", "AGENT_TAP_LIFETIME_MINUTES": "480", "AGENT_PRIVILEGED": "true",
                               "ROLE_ASSIGN_ALLOWED_ROLES": ",".join([*TEST_ROLES.values(), ROLE_IDS["storage blob data contributor"], ROLE_IDS["key vault secrets officer"]]),
                               "ROLE_ASSIGN_SCOPES": allowed},
           "swa_settings": {"APP_ENVIRONMENT": "test", "NOTIFY_REDIRECT_TO": "x@y.z", "NOTIFY_SENDER": "AcsEmail", "AGENT_TAP": "true", "AGENT_PRIVILEGED": "true"}}
    team = {"security": ["sec@company.example"], "idTeam": ["id@company.example"], "roleTeam": ["roles@company.example"]}
    projects = [{"projectId": "PEST", "tenantId": "t1", "tenantName": "PEST", "environments": ["Dev", "PreProd", "Prod"], "contacts": team, "contactsVersion": 1},
                {"projectId": "VESTACY", "tenantId": "t1", "tenantName": "Vestacy", "environments": ["Dev", "Test", "PreProd", "Prod"], "contacts": team}]
    rows = [{"projectId": "PEST", "rowId": "PH-SUPPORT-READER-DEV", "entraGroup": "sample-group-support-reader",
             "azureRoles": [{"roleName": "Storage Blob Data Reader", "scope": allowed}, {"roleName": "Reader", "scope": allowed}]},
            {"projectId": "PEST", "rowId": PRIVILEGED_ROW, "privileged": True,
             "azureRoles": [{"roleName": n, "scope": allowed} for n in ("Storage Blob Data Contributor", "Key Vault Secrets Officer", "Reader")]},
            {"projectId": "VESTACY", "rowId": "PH-SUPPORT-DEV", "azureRoles": []}]
    ev = lambda *s: [{"step": x, "note": ""} for x in s]  # noqa: E731
    all_steps = ev("owner-approved", "security-approved", "agent-provisioned", "agent-role-assigned", "completed")
    requests = [{"id": "bbbbbbbbbbbb", "projectId": "PEST", "environment": "Dev", "flow": {"status": "Completed", "events": all_steps}}]
    notes = [{"status": "Sent", "deliveredTo": ["x@y.z"]}, {"status": "Sent", "kind": "SignInDetails", "deliveredTo": ["x@y.z"]}]
    res = evaluate(ctx, projects, rows, requests, notes, [])
    # v1.1 F2: the sign-in details are recorded on the request (masked recipient), not as a stored mail.
    f2 = [dict(requests[0], flow=dict(requests[0]["flow"], idTeamTask={"agent": {"signInDetailsSentAt": "2026-10-30T10:00:00Z", "signInDetailsSentTo": "j***@company.example"}}))]
    tap = next(r for r in evaluate(ctx, projects, rows, f2, notes[:1], []) if r.feature.startswith("10d."))
    assert tap.status == PASS and "j***@company.example" in tap.detail, (tap.status, tap.detail)
    # Test environment (redirect on): everything passes but the go-live check, which names the switch to production.
    assert all(r.status == PASS for r in res if not r.feature.startswith("15.")), [(r.feature, r.status, r.detail) for r in res if r.status != PASS]
    go = next(r for r in res if r.feature.startswith("15."))
    assert go.status == WAIT and "environment <sub> prod" in go.next, (go.detail, go.next)
    prod = json.loads(json.dumps(ctx))
    for k in ("worker_settings", "swa_settings"):
        prod[k].update({"APP_ENVIRONMENT": "prod", "NOTIFY_REDIRECT_TO": ""})
    assert all(r.status == PASS for r in evaluate(prod, projects, rows, requests, notes, [])), \
        [(r.feature, r.status, r.detail) for r in evaluate(prod, projects, rows, requests, notes, []) if r.status != PASS]
    # A team list left empty, a failed mail and an hour of backlog are named.
    empty = [dict(projects[0], contacts={"security": ["sec@company.example"]}), projects[1]]
    got6 = next(r for r in evaluate(prod, empty, rows, requests, notes, []) if r.feature.startswith("6c."))
    assert got6.status == WAIT and "missing ID team, role assignment team" in got6.detail and "Contact inventory" in got6.next, got6.detail
    q = [{"status": "Pending", "createdAt": "2026-10-23T08:00:00+00:00"}, {"status": "Failed", "lastError": "MailForbidden"}]
    mail = lambda n: next(r for r in evaluate({**prod, "now": "2026-10-23T09:30:00+00:00"}, projects, rows, requests, n, []) if r.feature.startswith("14."))  # noqa: E731
    assert mail(q).status == FAIL and mail(q[:1]).status == WAIT and mail([]).status == PASS

    # The state after this commit is deployed, before the steps: SAMPLE data still there, no tenants, TAP off, Reader only.
    ctx2 = json.loads(json.dumps(ctx))
    ctx2["worker_settings"].update({"AGENT_TAP": "false", "AGENT_PRIVILEGED": "false", "ROLE_ASSIGN_ALLOWED_ROLES": READER})
    ctx2["graph_roles"] = sorted(READ_ROLES | CREATE_ROLES)
    sample_projects = [{"projectId": "SAMPLE-PEST", "isSample": True}, {"projectId": "SAMPLE-ALPHA", "isSample": True}]
    sample_rows = [{"projectId": "SAMPLE-PEST", "rowId": "P-PH-SUPPORT-READER-DEV", "isSample": True, "azureRoles": [{"roleName": "Reader", "scope": allowed}]}]
    got = {r.feature.split(" ")[0]: r for r in evaluate(ctx2, sample_projects, sample_rows, [], [{"status": "Sent"}], [])}
    assert {k: got[k].status for k in ("5.", "5b.", "6.", "6b.", "7.", "10d.", "12.")} == {k: WAIT for k in ("5.", "5b.", "6.", "6b.", "7.", "10d.", "12.")}, \
        {k: v.status for k, v in got.items()}
    assert "2a2b9908" in got["5."].next and "tap <sub> on" in got["5b."].next and "--remove-sample" in got["6b."].next and "--test-scope" in got["7."].next
    # Privileged rows on, but the Data Engineer roles are not on the allow-list (Reader + Storage Blob Data Reader only).
    ctx4 = json.loads(json.dumps(ctx))
    ctx4["worker_settings"]["ROLE_ASSIGN_ALLOWED_ROLES"] = ",".join(TEST_ROLES.values())
    p4 = next(r for r in evaluate(ctx4, projects, rows, requests, notes, []) if r.feature.startswith("5c."))
    assert p4.status == WAIT and "--from-sheet PEST" in p4.next and "key vault secrets officer" in p4.next and "privileged <sub> on" not in p4.next, (p4.detail, p4.next)
    rows5 = [dict(r, azureRoles=[{"roleName": "Owner", "scope": allowed}]) if r["rowId"] == PRIVILEGED_ROW else r for r in rows]
    p5 = next(r for r in evaluate(ctx, projects, rows5, requests, notes, []) if r.feature.startswith("5c."))
    assert p5.status == WAIT and "never does" in p5.next, (p5.detail, p5.next)
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

# Prompts for enterprise Claude Code

Run each prompt in its **own session**, from the repo root on the imported branch. The prompts point Claude at the repo files instead of pasting their content, which saves tokens.

## How to spend tokens well in enterprise Claude Code

- **Model:**
  - prompt A (security-sensitive infra and sign-in): the most capable model your org allows (Opus class);
  - prompts B and C (reading output, runbook steps): a smaller, faster model (Sonnet class).
- **Effort:** `high` by default. Use `xhigh` or max only for prompt A, tasks A2–A3 (sign-in and identity).
- **Mode:** start prompt A in plan mode (Shift+Tab), approve the plan, then let it run.
- **Context:**
  - one session per prompt;
  - run `/compact` at about 60–70 % context;
  - paste only the failing lines, not whole logs.
- **Limits:**
  - your org admin sets the caps; check `/status` (and `/cost` if enabled) at the start;
  - if you hit a limit mid-task, prompt A's one-commit-per-task rule lets a new session resume.
- **Permissions:** give it file edits plus `dotnet`, `npm`, `python3`, `bicep`, `git`. Do not give it Azure write access: people run the deploy and admin steps.

Size: prompt A is the big one (a few hours of agent work). Prompts B and C are short, interactive sessions.

---

## Prompt A — prepare the tech stack (code only, no Azure changes)

```
You are preparing the PMO onboarding flow v2.2 for our enterprise Azure environment.
Read first, in this order: CLAUDE.md, docs/ENTERPRISE_HANDOFF.md (sections 2-6), docs/NEXT_SESSION.md,
docs/GO_LIVE.md, SCOPE.md, docs/PROMOTION.md, docs/VERIFIED_FACTS.md (F2, F3, F10, F21, F61, F63, F64, F70).

Constraints:
- No Azure writes. Do not run az commands that change anything. You write code, Bicep, pipelines and
  docs; people run the deploy and admin steps.
- Keep every hard rule in CLAUDE.md: the agent never approves; each security change goes dry run ->
  written approval -> --approved-by; never log TAP/password/connection string/user ids; no real data in git.
- Verify every Azure limit, SKU, API and permission against current Microsoft Learn. Record URL + date
  in docs/VERIFIED_FACTS.md. If a fact differs from the plan, stop and tell me.
- One commit per task (A1..A9), tests green before each commit. Ask me only for blocking choices.

Target: one stack per tenant (PEST, Vestacy) and per environment (test, prod), each in its own tenant's
subscription. The worker's AGENT_TENANT_ID = that tenant. No cross-tenant provisioning.

Tasks (close gaps G1-G9 in docs/ENTERPRISE_HANDOFF.md):
A1 Parameterise tenant/env/RG/name prefix in infra/ and in all tools/*.sh (no hardcoded
   rg-pmo-review-pilot). One params file per tenant+env under infra/params/ (placeholders only).
   Rename deploy_free.sh -> deploy.sh (keep the same subcommands and approval gates).
A2 SWA Standard + custom Entra auth: single-tenant app registration, openIdIssuer per tenant,
   client secret as a Key Vault reference. Keep the /login, /logout and anonymous signed-out routes.
   Add a test that the pre-configured provider is off.
A3 API as a BYO Function App (Flex, .NET 10 isolated, system MI) linked to SWA. Cosmos via
   COSMOS_ENDPOINT + MI (Cosmos DB Built-in Data Contributor, account scope), disableLocalAuth=true,
   no connection string anywhere. Update InfraConsistencyTests.
A4 Cosmos without the free-tier assumption (serverless or provisioned with an RU cap, a parameter).
   Remove the zero-cost policy/budget defaults; budget amount and email become parameters.
A5 Mail: ACS with a custom verified domain (parameter), DNS records listed in the deploy output;
   MAIL_MAX_PER_MINUTE / MAIL_MAX_PER_HOUR per environment within the verified domain's limit (F70);
   keep the Graph sendMail option. Prod refuses NOTIFY_REDIRECT_TO (already coded; keep the test).
   Prod contacts come from the Contact inventory page, never --test-contact.
A5b Least privilege: drop AuditLog.Read.All from the enterprise Graph read list (no sign-in check
   since 22 Oct); privileged rows stay off by default (`privileged ... on` only after Security decides).
A6 Org policy alignment: tags, allowed regions, public network access, diagnostic settings to Log
   Analytics, storage shared-key off, minimum TLS. I will paste our policy list when you ask.
A7 App Insights + Log Analytics (daily cap, minimum retention); no request text in logs.
A8 pipelines/azure-pipelines.yml: a matrix over tenant x env; WIF service connection per tenant;
   Build -> WhatIf (artifact) -> Deploy (environment with approval check); SWA token from a
   Key Vault-linked variable group.
A9 docs: update docs/ENTERPRISE_HANDOFF.md section 6 with the exact commands per step, a cost-input
   list per stack (no prices) and the who-runs-it column. Update docs/RUNBOOK.md.

Done when: dotnet test (net10.0), npm --prefix src/web test, bicep build for every params file and
tools/run_acceptance.sh all pass; no hardcoded RG/tenant/sub ids remain (grep proof); a summary lists
each change, test output, and the open decisions for me.
```

## Prompt B — deploy session helper (per tenant: run once for PEST, once for Vestacy)

```
We are deploying the onboarding flow to <TENANT>-<ENV>. Read docs/ENTERPRISE_HANDOFF.md section 6 and
docs/RUNBOOK.md. You cannot reach Azure. I run each command and paste the output.
For each step: give me the exact command, who must run it (role from section 5), and what output means
PASS. Read the what-if and dry-run output I paste and list every create/modify/delete, plus any risk.
Stop at each approval gate until I say "approved by <name> <date>". Never ask me to paste secrets.
At the end, write the DEPLOYMENT_LOG.md rows (no TAP, password or user ids) and commit.
```

## Prompt C — UAT end-to-end check

```
Read docs/ENTERPRISE_HANDOFF.md step 15 and workflows/09-onboarding-flow-v2.md. I paste
verify_features.sh and workflow_diag.sh output for <TENANT>-<ENV>. For each WAIT or FAIL, give the
cause, the next step and who owns it. When all rows PASS and a request shows Completed, write the UAT
sign-off summary (facts only, with evidence).
```

# Fabric ↔ ADLS Gen2 (private) POC

Goal: Microsoft Fabric (public SaaS) reads and writes an ADLS Gen2 account that has no open public access, reusing the `pvdbx-` private network from the Purview setup. Result: a production runbook built only from the methods that passed.

- Flow and waves: `workflows/00-poc-flow.md`
- Facts read on Microsoft Learn (with dates) and where they differ from the plan: `report/VERIFIED_FACTS.md`
- Handoff, locks and approvals: `state/state.json` (`tools/state.py`)
- Masked evidence: `evidence/`. Raw output stays in `.local/fabric-poc/`, which isn't in git.

## Run on the Mac (Remote Control)
```bash
bash
cd ~/onboarding-requests && git fetch -q origin claude/friendly-pasteur-vt5a4u
az login            # you sign in; Claude never does
bash fabric-adls-poc/tools/discover.sh --sub <subscriptionId> [--storage <account>]
```
Nothing changes in Azure or Fabric until a wave is approved:
`python3 fabric-adls-poc/tools/state.py approve 1 --by "Gokul 2026-11-02"`.

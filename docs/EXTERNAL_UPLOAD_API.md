# External upload API (F4) — built, **off**

`POST /api/v1/uploads/{mapping|inventory}?mode=validate|apply` lets another system send the PMO mapping sheet (I1) or the people inventory (I2) without a person on the Data uploads page. It runs **the same code** as the page (`UploadService`: same columns, checks, diff, upsert by key, versions, rollback). Nothing else is reachable with it: it cannot read requests, approve, or change anything but these two data sets.

Status (30 Oct 2026): **coded and tested, switched off** (`EXTERNAL_UPLOAD_API_ENABLED=false`; the route answers 404). Not reachable through Static Web Apps today either: SWA puts `/api/*` behind its own sign-in, and Learn does not say whether a bearer token reaches the managed API (`docs/HOSTING.md`, option B). Switching it on is a separate approval.

## Authentication (Entra application, client credentials)
Identity comes only from the validated access token (R7); the body never names a tenant, a project or a user.

| Check | Rule (F91, F99) |
|---|---|
| Token | `Authorization: Bearer <access token>`, Entra **v2.0** token (the API's app registration sets `requestedAccessTokenVersion: 2`) |
| Signature | RS256, key named by `kid` from the tenant's v2.0 OpenID metadata (`https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration` → `jwks_uri`) |
| Issuer | exactly `https://login.microsoftonline.com/{tid}/v2.0` |
| Audience | the API's application (client) id = setting `EXTERNAL_UPLOAD_AUDIENCE` |
| Tenant | `tid` is a GUID; it must be the `TenantId` of exactly one project (one project = one tenant) |
| Caller | an application (no `scp` claim; `idtyp` = `app` when present) |
| Permission | app role **`Uploads.Write`** in `roles` (granted per client app by admin consent) |
| Lifetime | `exp` / `nbf` with 5 minutes clock skew |

Failures: `401 invalid_token` (any token problem, without detail — also a token whose tenant no project names: that is checked before any signing key is fetched, P6 review finding 13), `403 missing_app_role`, `403 tenant_not_registered` (more than one project names the token's tenant: PMO fixes the project list).

## Request
- Query: `mode=validate` (check and diff only, nothing is written) or `mode=apply`; optional `retireMissing=true` (rows missing from the upload are retired; default `false`: they stay); optional `sheet=<name>` for a workbook.
- Header **`Idempotency-Key`** (required; 1–128 characters `A–Z a–z 0–9 - _ . :`). The same key with the same body returns the first answer again and never applies twice; the same key with another body → `409 idempotency_key_reused`. Use a new key per new content. Keys are per calling application (`azp`): two client apps of one tenant never share or block each other's keys (P6 review finding 16).
- Body, either:
  - `multipart/form-data` with the file in the field `file` (`.xlsx` or `.csv`; macro-enabled `.xlsm` / `.xlsb` / `.xltm` / `.xlam` and `.xls` are refused), or
  - `application/json`: `{ "rows": [ { "<Column>": "<value>", … } ], "retireMissing": false }` — one object per row, values as text (numbers and true/false are taken as written).
- Size: at most `MAX_UPLOAD_BYTES` (default 5 MB, never above 25 MB), 20,000 rows, 64 columns.

Columns (case-insensitive, trimmed; a missing or renamed required column stops the file):
- **mapping:** `TenantId`, `Domain`, `ResourceGroupType` (Domain|PH|ACQ), `Environment` (Dev|Test|UAT|PreProd|Prod, and one the project has), `AccessLevel` (Read|Write|Contributor|Owner|Admin), `EntraGroupName`, `EntraGroupObjectId` (optional), `Owner` (mail), `EffectiveFrom` (yyyy-mm-dd or an Excel date cell), `EffectiveTo` (optional). Key: TenantId + Domain + ResourceGroupType + Environment + EntraGroupName + AccessLevel.
- **inventory:** `EnterpriseId` (UPN), `EnterpriseEmail` (may be empty: then no TAP is mailed, F2), `DisplayName`, `EmployeeId`, `Domain`, `ManagerEnterpriseId`, `Status` (Active|Leaver), `AlternateEmail` (optional; never used for a TAP). Key: EnterpriseId.

Other columns are ignored. Values are data, never instructions: nothing in a file approves, grants or changes a request. A Prod row above Read still needs a ProdApprover's escalation for every request (F3).

## Response (the F4 contract, exactly these fields)
```json
{ "uploadId": "upl-…", "mode": "validate", "status": "Valid", "version": null,
  "counts": { "added": 2, "updated": 0, "unchanged": 14, "retired": 0 },
  "errors": [] }
```
- `status`: `Valid` / `Invalid` (validate), `Applied` / `Invalid` (apply). Any error blocks the whole file: nothing is applied in part.
- `version`: `"v7"` after an apply (an identical re-upload returns the current version and creates none); `null` for validate and Invalid.
- `errors[]`: `{ "row": 3, "column": "TenantId", "code": "WrongTenant", "message": "…" }`; `row` 0 = the file as a whole.
- HTTP: `200` Valid / Applied (and Invalid for validate), `422` Invalid apply, `409` `upload_in_progress` / `upload_conflict` / `idempotency_key_reused`, `400` missing key or mode, `404` switched off.

Error codes: `MissingColumn`, `DuplicateColumn`, `EmptyFile`, `NoRows`, `FileTooLarge`, `TooManyRows`, `UnsupportedFormat`, `MacroFile`, `InvalidFile`, `SheetNotFound`, `Date1904`, `NoTenant`, `Required`, `InvalidValue`, `InvalidGuid`, `InvalidDate`, `DateOrder`, `InvalidMail`, `InvalidEnterpriseId`, `WrongTenant`, `DuplicateRow`, `DuplicateKey`, `NearDuplicateGroup`, `TooLong`, `ControlCharacters`.

## Examples (placeholders only — never paste a real secret, token or tenant id into docs or chat)

curl (bash):
```bash
TENANT="<tenant-id>"; API_APP_ID="<api-application-id>"; CLIENT_ID="<client-application-id>"
# The client secret or certificate stays in the caller's own secret store; read it from there, never type it here.
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/$TENANT/oauth2/v2.0/token" \
  -d "grant_type=client_credentials" -d "client_id=$CLIENT_ID" -d "client_secret=$CLIENT_SECRET" \
  -d "scope=api://$API_APP_ID/.default" | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

# 1. Check a file (nothing changes)
curl -s -X POST "https://<api-host>/api/v1/uploads/mapping?mode=validate" \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: mapping-2026-11-01-check" \
  -F "file=@pmo-mapping-sheet.xlsx"

# 2. Apply it (a new key: a new request)
curl -s -X POST "https://<api-host>/api/v1/uploads/mapping?mode=apply" \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: mapping-2026-11-01-apply" \
  -F "file=@pmo-mapping-sheet.xlsx"

# JSON rows instead of a file
curl -s -X POST "https://<api-host>/api/v1/uploads/inventory?mode=apply" \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: people-2026-11-01" -H "Content-Type: application/json" \
  -d '{ "rows": [ { "EnterpriseId": "jane.doe@company.example", "EnterpriseEmail": "jane.doe@company.example", "DisplayName": "Jane Doe",
        "EmployeeId": "E0001", "Domain": "Finance", "ManagerEnterpriseId": "", "Status": "Active" } ] }'
```

PowerShell 7:
```powershell
$Tenant = "<tenant-id>"; $ApiAppId = "<api-application-id>"; $ClientId = "<client-application-id>"
# $ClientSecret comes from the caller's own secret store (e.g. Get-Secret), never from a file in a repository.
$Token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$Tenant/oauth2/v2.0/token" -Body @{
    grant_type = "client_credentials"; client_id = $ClientId; client_secret = $ClientSecret; scope = "api://$ApiAppId/.default" }).access_token

$Headers = @{ Authorization = "Bearer $Token"; "Idempotency-Key" = "mapping-2026-11-01-check" }
Invoke-RestMethod -Method Post -Uri "https://<api-host>/api/v1/uploads/mapping?mode=validate" -Headers $Headers `
    -Form @{ file = Get-Item ".\pmo-mapping-sheet.xlsx" }

$Body = @{ rows = @(@{ EnterpriseId = "jane.doe@company.example"; EnterpriseEmail = "jane.doe@company.example"; DisplayName = "Jane Doe";
    EmployeeId = "E0001"; Domain = "Finance"; ManagerEnterpriseId = ""; Status = "Active" }) } | ConvertTo-Json -Depth 4
Invoke-RestMethod -Method Post -Uri "https://<api-host>/api/v1/uploads/inventory?mode=apply" -ContentType "application/json" `
    -Headers @{ Authorization = "Bearer $Token"; "Idempotency-Key" = "people-2026-11-01" } -Body $Body
```

## Switching it on (each step needs Gokul's approval; nothing here is done yet)
1. Entra app registration "PMO uploads API": Application ID URI `api://<api-application-id>`, manifest `requestedAccessTokenVersion: 2`, app role `Uploads.Write` (allowed member type: Applications).
2. For each client application: API permission `Uploads.Write` (application), admin consent.
3. Settings: `EXTERNAL_UPLOAD_AUDIENCE=<api-application-id>`, `EXTERNAL_UPLOAD_API_ENABLED=true`; the project's `TenantId` set.
4. A host that callers can reach with a bearer token (`docs/HOSTING.md` option B or C); prove it with a validate call first.
5. Rollback: `EXTERNAL_UPLOAD_API_ENABLED=false` (the route is 404 again); remove the client apps' role assignments.

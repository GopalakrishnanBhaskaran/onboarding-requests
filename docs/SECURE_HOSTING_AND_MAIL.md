# Secure hosting and encrypted sign-in mail (actual tenant)

Written 23 Oct 2026. Design only: nothing here is built or deployed yet.
The code work is tasks A10–A11 in `docs/ENTERPRISE_CLAUDE_PROMPTS.md`.
Items marked *(verify)* must be checked on Microsoft Learn before the build and recorded in `docs/VERIFIED_FACTS.md`.


## 1. Short answer

- **HTTPS:** Static Web Apps is already HTTPS only. Its weak points are elsewhere:
  - the site is public;
  - any Microsoft account can sign in (F10);
  - the database key sits in the app settings.
- **Recommended:** host the website and the API in **one Azure App Service app** in the actual tenant:
  - HTTPS only, minimum TLS 1.2;
  - App Service sign-in (Entra, single tenant);
  - no public access: a private endpoint, behind a WAF;
  - managed identity to Cosmos, with Cosmos keys off.
- **Encrypted mail, only for the password and TAP mail:**
  - send that one mail through **Microsoft Graph from a shared mailbox** in the actual tenant, with a marker header;
  - an Exchange **mail flow rule** applies **Microsoft Purview Message Encryption** to it;
  - every other mail stays on Azure Communication Services, unencrypted.
- **Better still:** stop mailing the password. The one-time TAP is enough to sign in and set up Authenticator.


## 2. Hosting options

**A. Static Web Apps Standard (smallest change)**
- Adds custom Entra sign-in, a private endpoint and a custom domain.
- The managed API still has no managed identity, so it also needs its own Function App.
- That gives two hosts, two sign-in cookies and more moving parts.

**B. App Service, one app for the website and the API (recommended)**
- One origin, one sign-in, one set of security settings.
- Full control of TLS, headers, the network and logs.
- The code already has a second host for the same API logic (`tools/local-api` serves the same `ApiHost`), so the change is small.

**C. Container Apps**
- Also possible.
- More work: images, a registry, an environment. Not needed here.


## 3. Recommended design (option B)

```mermaid
flowchart LR
  U["Users (actual tenant)<br/>Conditional Access: MFA, compliant device"] --> WAF["Front Door Premium + WAF<br/>(or Application Gateway WAF v2 if internal only)<br/>custom domain, managed certificate, TLS 1.2+"]
  WAF -- "Private Link" --> APP["App Service (Linux, .NET 10)<br/>website + API in one app<br/>App Service sign-in: Entra single tenant<br/>HTTPS only · public access off"]
  APP -- "managed identity, private endpoint" --> COSMOS[("Cosmos DB<br/>keys off")]
  APP -- "managed identity, private endpoint" --> KV["Key Vault"]
  WRK["Worker (Flex Consumption)<br/>VNet integration, managed identity"] -- "private endpoint" --> COSMOS
  WRK --> GRAPH["Microsoft Graph"]
  WRK --> ARM["Azure RBAC"]
  WRK --> ACS["ACS Email: normal mail"]
  WRK --> EXO["Graph sendMail from a shared mailbox<br/>→ Exchange rule → Purview Message Encryption<br/>password + TAP mail only"]
```

### Settings checklist

**App Service**
- HTTPS Only: on.
- Minimum TLS: 1.2 (1.3 if every client supports it).
- FTP and basic publishing credentials: off.
- Remote debugging: off.
- Public network access: off, with a private endpoint; or access restrictions that allow only Front Door.

**Sign-in**
- App Service authentication with an Entra app registration (single tenant).
- "Assignment required" on the enterprise app, with only the user groups assigned.
- Unauthenticated requests are redirected to sign-in.
- A Conditional Access policy on the app: MFA, and a compliant or hybrid-joined device.

**Certificates**
- A custom domain with an App Service or Front Door managed certificate; or a certificate from Key Vault if the org requires its own certificate authority.

**Security headers** (added in the host)
- `Strict-Transport-Security`
- `Content-Security-Policy` (`default-src 'self'`, `frame-ancestors 'none'`)
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: no-referrer`

**Secrets and data**
- No secret in app settings: Key Vault references read with the managed identity.
- Cosmos: `disableLocalAuth: true`, a private endpoint, and the role Cosmos DB Built-in Data Contributor for the app and the worker.

**Network**
- One VNet with subnets for the private endpoints and for the worker's VNet integration.
- Private DNS zones for `privatelink.azurewebsites.net`, `privatelink.documents.azure.com` and `privatelink.vaultcore.azure.net`.

**Logs**
- Application Insights and Log Analytics.
- Diagnostic settings for App Service and the WAF.

### Code changes (prompt A, task A10)

1. **New host** `src/webhost` (ASP.NET Core, .NET 10). It reuses `ApiHost` for `/api/*` and serves the built website from `wwwroot`.
2. **Sign-in header:**
   - `ClientPrincipalParser` must also read App Service's `X-MS-CLIENT-PRINCIPAL`, which is a list of claims, unlike the Static Web Apps format.
   - Map `oid` to the user id, and `preferred_username` (or `upn`) to the sign-in name.
   - Reject a `tid` that is not the expected tenant.
   - Accept the header only from App Service sign-in, never from the client.
3. **Website:** read `/.auth/me` in App Service's format.
4. **Routes:** move the `staticwebapp.config.json` rules (logout redirect, blocked providers, signed-out page) into the host.
5. **Database:** Cosmos with managed identity only (already coded: `COSMOS_ENDPOINT` without a connection string).
6. **Tests:**
   - an App Service principal with the wrong `tid` is denied;
   - a request without the header is denied;
   - the security headers are present;
   - the existing access tests run on both principal formats.


## 4. Encrypted mail: only the password and TAP mail

**Today:** one mail from Azure Communication Services to the Enterprise ID mail address.
- It is protected in transit (TLS), but it is not encrypted end to end.
- Anyone with access to that mailbox, or a forward rule, can read the TAP and the password.
- Azure Communication Services can't apply Microsoft Purview Message Encryption, because that runs in Exchange Online.

### Design
1. **Shared mailbox:** create one in the actual tenant, for example `onboarding-signin@<domain>`.
2. **Send through Graph:** the worker sends **only** the sign-in mail through Graph `sendMail` from that mailbox, with the header `x-pmo-sensitivity: signin`. Graph allows custom `x-` headers on a message *(verify)*.
3. **Mail flow rule** in Exchange Online:
   - when the sender is `onboarding-signin@…` and the header `x-pmo-sensitivity` is `signin`;
   - then apply Purview Message Encryption: template **Encrypt-Only**, or **Do Not Forward** to also block forwarding and copying.
4. **Recipients:**
   - Inside the tenant: they open it in Outlook as usual.
   - Outside (the joiner's current company mail): they get a protected-message link and read it after a one-time passcode or a Microsoft sign-in.
5. **Every other mail** (approvals, tasks, *access ready*, completion) stays on Azure Communication Services, unchanged.
6. **Prod guard:** a new setting `REQUIRE_ENCRYPTED_SIGNIN_MAIL=true`. With it, the worker refuses to send the sign-in mail through Azure Communication Services. If the encrypted path fails, the TAP goes to the ID team, as today.

**Permissions** (the Mail.Send permission needs Security approval):
- Graph application permission `Mail.Send` for the worker identity, granted by a Privileged Role Administrator.
- **Exchange RBAC for Applications** limits that `Mail.Send` to the one shared mailbox, so the worker can't send as anyone else. An Exchange Administrator sets this up.

**Licensing:** Purview Message Encryption comes with Microsoft 365 E3/E5 or Office 365 E3/E5, or with an add-on *(verify the org's plan)*.

### Code changes (prompt A, task A11)
1. A new setting `DIRECT_MAIL_SENDER=GraphMail` and `DIRECT_MAIL_FROM=<shared mailbox>`. `DirectMail` uses its own `GraphMailSender`, while notifications keep `NOTIFY_SENDER=AcsEmail`.
2. `GraphMailSender` adds `internetMessageHeaders` (`x-pmo-sensitivity: signin`) for the sign-in mail only.
3. `REQUIRE_ENCRYPTED_SIGNIN_MAIL`: on by default in prod; refuses the Azure Communication Services path.
4. Optional, recommended: `AGENT_SEND_PASSWORD=false`. The mail then carries only the TAP. The joiner signs in with the TAP, adds Authenticator, then sets a password (or goes passwordless).
5. Tests:
   - the sign-in mail never goes through Azure Communication Services when the setting is on;
   - the header is set only on the sign-in mail;
   - the masked stored copy is unchanged.

### Extra hardening for the TAP
- A shorter TAP lifetime in prod, for example 60–120 minutes. It is one-time already. Allow for time zones and start dates.
- Send the TAP mail only on the joiner's start date (a small scheduling change).
- Optional split channel: the TAP to the joiner, and a notice without the TAP to the requester.


## 5. Who does what

- **Network admin:** VNet, subnets, private endpoints, private DNS zones, Front Door or Application Gateway with WAF.
- **Subscription Owner:** App Service plan and app, Key Vault, the Cosmos private endpoint, role assignments for the managed identities.
- **Application Administrator:** the app registration for App Service sign-in, and "assignment required".
- **Conditional Access Administrator:** the MFA and device policy for the app.
- **Exchange Administrator:**
  - the shared mailbox;
  - Exchange RBAC for Applications;
  - the mail flow rule for encryption.
- **Compliance Administrator:** Purview Message Encryption templates and branding.
- **Privileged Role Administrator:** Graph `Mail.Send` for the worker (after Security approval).
- **Security approver:** signs off the design, the WAF rules, `Mail.Send`, and whether the password is mailed at all.


## 6. Cost inputs (no prices; use the Azure Pricing Calculator)
- An App Service plan on a tier that supports private endpoints *(verify the tier)*. One instance for test, two for prod availability.
- Front Door Premium (needed for Private Link to the app), only if users reach the app from outside the corporate network. Otherwise Application Gateway WAF v2, or only a private endpoint with VPN or ExpressRoute.
- Private endpoints (per endpoint and GB) and the private DNS zones.
- Key Vault operations.
- Application Insights and Log Analytics ingestion.
- Purview Message Encryption licensing (usually already part of E3/E5).

# Architecture — PMO Request Review Pilot

Scope is the locked v1.0 scope. Only the hosting stack changes for the pilot. Every paid component sits behind an interface so the same code is promoted later.

## Component diagram

```mermaid
flowchart LR
  subgraph Browser
    UI["SPA<br/>React + Vite + Fluent UI"]
  end

  subgraph SWA["Azure Static Web Apps — Free"]
    AUTH["Built-in auth<br/>pre-configured Entra ID"]
    subgraph API["Managed Functions (C# isolated, HTTP only)"]
      FN["HTTP functions<br/>requests · analyze · runs · decisions · rules · operator"]
      AZ["AuthZ guard<br/>x-ms-client-principal → userAccess<br/>deny by default"]
    end
  end

  subgraph CORE["src/core (no Azure SDK)"]
    ENG["Review engine<br/>deterministic checks → model → host validation"]
    TOOLS["Read-only tools<br/>GetPublishedRequirements<br/>GetApprovedRoleMapping"]
    IF1(["IReviewModel"])
    IF2(["IReviewDispatcher"])
    IF3(["IRepository"])
    SCHEMA["review-result.v1.json"]
  end

  subgraph ADP["src/adapters"]
    RULE["RuleOnlyReviewModel<br/>ACTIVE"]
    AOAI["AzureOpenAIReviewModel<br/>DISABLED"]
    INL["InlineDispatcher<br/>ACTIVE"]
    QUE["QueueDispatcher<br/>DISABLED stub"]
    REPO["CosmosRepository"]
  end

  COSMOS[("Cosmos DB NoSQL<br/>free tier · shared 1000 RU/s<br/>7 containers")]

  UI -- "HTTPS + auth cookie" --> AUTH --> FN
  FN --> AZ
  FN --> IF2
  IF2 -.-> INL
  IF2 -.-> QUE
  INL --> ENG
  ENG --> TOOLS
  ENG --> IF1
  IF1 -.-> RULE
  IF1 -.-> AOAI
  ENG --> SCHEMA
  TOOLS --> IF3
  AZ --> IF3
  IF3 -.-> REPO --> COSMOS

  classDef off stroke-dasharray: 5 5,opacity:0.6
  class AOAI,QUE off
```

Solid lines run in the pilot. Dashed boxes are coded, tested with mocks, and switched off by config.

## Stack mapping (A1)

| Locked design (paid) | Zero-cost pilot (Azure) | Promotion path (paid, later) |
|---|---|---|
| Model-driven Power App | SWA **Free**, SPA UI | Keep SWA (Standard) or move UI to Power Apps |
| Entra sign-in via Power Apps | SWA built-in auth, pre-configured Entra ID | SWA Standard custom Entra app registration |
| Dataverse tables + security roles | Cosmos DB NoSQL **free tier** + server-side `userAccess` | Dataverse, or Cosmos with managed identity |
| Dataverse plug-in snapshot | API creates snapshot + SHA-256 server-side | Dataverse custom API |
| Power Automate processing flow | `IReviewDispatcher` → `InlineDispatcher` | Queue-triggered Function or Power Automate flow |
| Azure Functions (isolated) | SWA **managed** Functions, C# isolated, HTTP only | BYO Function App + managed identity + Key Vault |
| Azure OpenAI reviewer | `IReviewModel` → `RuleOnlyReviewModel` | `AzureOpenAIReviewModel` (coded, tested, disabled) |
| Application Insights | Off; structured logs only | App Insights with sampling |
| Azure Pipelines | SWA CLI deploy | Azure Pipelines, multi-stage |

## Analyze flow (pilot)

```mermaid
sequenceDiagram
  participant U as SPA
  participant F as POST /api/requests/{id}/analyze
  participant C as Cosmos
  participant E as Review engine
  U->>F: Analyze (client nonce)
  F->>C: userAccess lookup (from client principal)
  F->>C: read request (ETag, revision)
  F->>C: create reviewRun Queued (unique idempotencyKey)
  F->>C: claim run (ETag) → Running
  F->>E: snapshot + pinned rule/mapping versions
  E-->>F: result (host-computed readiness, citations checked)
  F->>F: validate against schema v1
  F->>C: revision changed? → Superseded, else Completed/Failed
  F-->>U: 202 + runId
  loop every 3 s, up to 60 s
    U->>C: GET /api/runs/{runId}
  end
```

**Honest limitation:** in the pilot the engine runs inside the Analyze HTTP call. The SWA gateway cuts API calls at 45 s (`VERIFIED_FACTS` F3), so the processing budget is capped well below that. If the browser closes after the request is accepted, the function keeps running and persists the run; the user sees it in history on return. A true background queue is the paid `QueueDispatcher` path.

## Trust boundaries
1. Browser → SWA: authenticated by SWA. Everything from the browser is untrusted, including body fields naming users or projects.
2. Function → Cosmos: connection string from SWA app settings (no managed identity on managed Functions, F2).
3. Request text → reviewer: untrusted content. Never interpreted as instructions.
4. Reviewer output → host: untrusted. Host recomputes readiness and validates every citation against pinned versions.

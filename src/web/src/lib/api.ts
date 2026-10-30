import type {
  AccessRequest, ActionItem, AssignedRole, ContactInventory, ContactInventoryInput, DailySummary, DeveloperReport, IntakeFields, Issue, Item, MappingRow, Me, RequestView,
  ResponseRecord, ReviewRun, StatusLine, ResourceOptions, ResolveResult, BundleInput, BundleResult, Escalation,
  UploadKind, UploadView, UploadsOverview, DataVersionView, OffboardingView,
} from './types';

/** Error returned by the API: safe message plus a correlation id to quote when reporting. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly correlationId?: string,
    public readonly details?: string[],
  ) {
    super(message);
  }
}

async function call<T>(method: string, path: string, body?: unknown, ifMatch?: string): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (ifMatch) headers['If-Match'] = ifMatch;

  let res: Response;
  try {
    res = await fetch(`/api/${path}`, {
      method,
      headers,
      credentials: 'same-origin',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    // The browser blocked or lost the call. On SWA this is usually an expired sign-in: the 401 override redirects the
    // API call to the Entra sign-in page, which fetch cannot follow cross-origin ("Failed to fetch").
    throw new ApiError(0, 'network', 'Could not reach the app. Your sign-in may have expired: reload the page (Ctrl+F5). If it repeats, sign out and sign in again.');
  }

  return read<T>(res);
}

async function read<T>(res: Response, alsoOk: number[] = []): Promise<T> {
  if (res.status === 401) {
    window.location.assign('/login');
    throw new ApiError(401, 'unauthenticated', 'Sign-in required.');
  }

  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok && !alsoOk.includes(res.status)) {
    const e = json?.error;
    throw new ApiError(res.status, e?.code ?? 'error', e?.message ?? `Request failed (${res.status}).`, e?.correlationId, e?.details ?? undefined);
  }
  return json as T;
}

/**
 * F4: sends one file as multipart/form-data (field "file"). A file that does not validate comes back as a result with
 * status Invalid (HTTP 422 on apply), not as an error.
 */
async function sendFile<T>(path: string, file: File): Promise<T> {
  const form = new FormData();
  form.append('file', file, file.name);
  let res: Response;
  try {
    res = await fetch(`/api/${path}`, { method: 'POST', headers: { Accept: 'application/json' }, credentials: 'same-origin', body: form });
  } catch {
    throw new ApiError(0, 'network', 'Could not reach the app. Your sign-in may have expired: reload the page (Ctrl+F5). If it repeats, sign out and sign in again.');
  }
  return read<T>(res, [422]);
}

const p = (projectId: string) => `projects/${encodeURIComponent(projectId)}`;
const id = (value: string) => encodeURIComponent(value);

/** Client nonce for an Operator retry. */
export function newNonce(): string {
  return crypto.randomUUID();
}

export interface TapInput {
  issuedAt: string;
  expiresAt: string;
  oneTime: boolean;
  issuedBy?: string;
}

export interface IdTeamReportInput {
  userPrincipalName: string;
  groupName: string;
  userCreated: boolean;
  groupCreated: boolean;
  addedToGroup: boolean;
  mappingSheetUpdated: boolean;
  /** Metadata only. There is no field for the pass itself, and the API refuses one. */
  tap: TapInput | null;
  notes?: string;
}

export const api = {
  me: () => call<Me>('GET', 'me'),
  queue: () => call<{ items: ActionItem[] }>('GET', 'queue'),

  listRequests: (projectId: string) => call<{ items: StatusLine[]; flowOverview: string[]; whoDoesWhat: string }>('GET', `${p(projectId)}/requests`),
  getRequest: (projectId: string, requestId: string) => call<RequestView>('GET', `${p(projectId)}/requests/${id(requestId)}`),
  submit: (projectId: string, fields: IntakeFields) => call<RequestView>('POST', `${p(projectId)}/requests`, fields),
  resubmit: (projectId: string, requestId: string, fields: IntakeFields, etag: string) =>
    call<RequestView>('PUT', `${p(projectId)}/requests/${id(requestId)}`, fields, etag),
  trail: (projectId: string, requestId: string) => call<{ text: string }>('GET', `${p(projectId)}/requests/${id(requestId)}/trail`),
  listRuns: (projectId: string, requestId: string) => call<{ items: Item<ReviewRun>[] }>('GET', `${p(projectId)}/requests/${id(requestId)}/runs`),

  respond: (projectId: string, requestId: string, decision: 'Approved' | 'Rejected', remarks: string, reviewRunId: string) =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/approvals`, { decision, remarks: remarks || null, reviewRunId }),
  pickUp: (projectId: string, requestId: string, team: 'id-team' | 'role-team') =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/${team}/pickup`, {}),
  /** A task the AI agent left to the team goes back to the agent (it runs every check again). */
  handBack: (projectId: string, requestId: string, team: 'id-team' | 'role-team') =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/${team}/handback`, {}),
  reportIdTeam: (projectId: string, requestId: string, report: IdTeamReportInput) =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/id-team/report`, report),
  reportRoleTeam: (projectId: string, requestId: string, assignedRoles: AssignedRole[], notes: string) =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/role-team/report`, { assignedRoles, notes: notes || null }),
  advance: (projectId: string, requestId: string) => call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/advance`, {}),
  resume: (projectId: string, requestId: string) => call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/resume`, {}),

  mappingSheet: (projectId: string) => call<{ items: Item<MappingRow>[] }>('GET', `${p(projectId)}/mapping-sheet`),
  addMappingRow: (projectId: string, row: Record<string, unknown>) => call<Item<MappingRow>>('POST', `${p(projectId)}/mapping-sheet`, row),
  updateMappingRow: (projectId: string, rowId: string, row: Record<string, unknown>, etag: string) =>
    call<Item<MappingRow>>('PUT', `${p(projectId)}/mapping-sheet/${id(rowId)}`, row, etag),
  updateMappingGroup: (projectId: string, rowId: string, entraGroup: string, groupState: string, etag: string) =>
    call<Item<MappingRow>>('POST', `${p(projectId)}/mapping-sheet/${id(rowId)}/group`, { entraGroup, groupState }, etag),
  responseDocument: (projectId: string) => call<{ items: Item<ResponseRecord>[] }>('GET', `${p(projectId)}/response-document`),
  contacts: (projectId: string) => call<Item<ContactInventory>>('GET', `${p(projectId)}/contacts`),
  saveContacts: (projectId: string, input: ContactInventoryInput, etag: string) =>
    call<Item<ContactInventory>>('PUT', `${p(projectId)}/contacts`, input, etag),

  issues: (projectId: string) => call<{ items: Issue[] }>('GET', `${p(projectId)}/issues`),
  daily: (projectId: string, date: string) => call<DailySummary>('GET', `${p(projectId)}/reports/daily/${id(date)}`),
  developer: (projectId: string) => call<DeveloperReport>('GET', `${p(projectId)}/reports/developer`),
  recoverStuck: (projectId: string) => call<{ recovered: string[] }>('POST', `${p(projectId)}/operator/recover`),

  // v1.1 (F1, F3)
  resourceOptions: (projectId: string, environment: string) =>
    call<ResourceOptions>('GET', `resources/options?projectId=${id(projectId)}&environment=${id(environment)}`),
  resolve: (projectId: string, environment: string, resources: string[]) =>
    call<ResolveResult>('GET', `resources/resolve?projectId=${id(projectId)}&environment=${id(environment)}${resources.map((r) => `&resource=${id(r)}`).join('')}`),
  submitBundle: (projectId: string, input: BundleInput) => call<BundleResult>('POST', `${p(projectId)}/requests/bundle`, input),
  escalations: (projectId: string) => call<{ items: Item<Escalation>[] }>('GET', `${p(projectId)}/escalations`),
  uploads: (projectId: string) => call<UploadsOverview>('GET', `${p(projectId)}/uploads`),
  upload: (projectId: string, kind: UploadKind, file: File, o: { mode: 'validate' | 'apply'; retireMissing: boolean; sheet?: string; baseVersion?: number }) => {
    const q = new URLSearchParams({ mode: o.mode, retireMissing: String(o.retireMissing) });
    if (o.sheet) q.set('sheet', o.sheet);
    if (o.baseVersion !== undefined) q.set('baseVersion', String(o.baseVersion));
    return sendFile<UploadView>(`${p(projectId)}/uploads/${kind}?${q.toString()}`, file);
  },
  offboarding: (projectId: string, enterpriseId: string) =>
    call<OffboardingView>('GET', `offboarding/${id(enterpriseId)}?projectId=${id(projectId)}`),
  planOffboarding: (projectId: string, enterpriseId: string) =>
    call<OffboardingView>('POST', `offboarding/${id(enterpriseId)}?projectId=${id(projectId)}&mode=plan`),
  confirmOffboarding: (projectId: string, enterpriseId: string, offboardingId: string) =>
    call<OffboardingView>('POST', `offboarding/${id(enterpriseId)}?projectId=${id(projectId)}&mode=execute`, { offboardingId }),
  rollbackUpload: (projectId: string, kind: UploadKind, version: number) =>
    call<DataVersionView>('POST', `${p(projectId)}/uploads/${kind}/rollback`, { version }),
  decideEscalation: (projectId: string, escalationId: string, decision: 'Approved' | 'Rejected', remarks: string, requestRevision: number) =>
    call<{ escalation: Item<Escalation> }>('POST', `${p(projectId)}/escalations/${id(escalationId)}/decision`, { decision, remarks: remarks || null, requestRevision }),
};

export type { AccessRequest };

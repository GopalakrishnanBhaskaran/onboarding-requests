import type {
  AccessRequest, ActionItem, AssignedRole, DailySummary, DeveloperReport, IntakeFields, Issue, Item, MappingRow, Me, RequestView,
  ResponseRecord, ReviewRun, StatusLine,
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

  if (res.status === 401) {
    window.location.assign('/login');
    throw new ApiError(401, 'unauthenticated', 'Sign-in required.');
  }

  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const e = json?.error;
    throw new ApiError(res.status, e?.code ?? 'error', e?.message ?? `Request failed (${res.status}).`, e?.correlationId, e?.details ?? undefined);
  }
  return json as T;
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
  reportIdTeam: (projectId: string, requestId: string, report: IdTeamReportInput) =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/id-team/report`, report),
  reportRoleTeam: (projectId: string, requestId: string, assignedRoles: AssignedRole[], notes: string) =>
    call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/tasks/role-team/report`, { assignedRoles, notes: notes || null }),
  advance: (projectId: string, requestId: string) => call<RequestView>('POST', `${p(projectId)}/requests/${id(requestId)}/advance`, {}),

  mappingSheet: (projectId: string) => call<{ items: Item<MappingRow>[] }>('GET', `${p(projectId)}/mapping-sheet`),
  addMappingRow: (projectId: string, row: Record<string, unknown>) => call<Item<MappingRow>>('POST', `${p(projectId)}/mapping-sheet`, row),
  updateMappingRow: (projectId: string, rowId: string, row: Record<string, unknown>, etag: string) =>
    call<Item<MappingRow>>('PUT', `${p(projectId)}/mapping-sheet/${id(rowId)}`, row, etag),
  updateMappingGroup: (projectId: string, rowId: string, entraGroup: string, groupState: string, etag: string) =>
    call<Item<MappingRow>>('POST', `${p(projectId)}/mapping-sheet/${id(rowId)}/group`, { entraGroup, groupState }, etag),
  responseDocument: (projectId: string) => call<{ items: Item<ResponseRecord>[] }>('GET', `${p(projectId)}/response-document`),

  issues: (projectId: string) => call<{ items: Issue[] }>('GET', `${p(projectId)}/issues`),
  daily: (projectId: string, date: string) => call<DailySummary>('GET', `${p(projectId)}/reports/daily/${id(date)}`),
  developer: (projectId: string) => call<DeveloperReport>('GET', `${p(projectId)}/reports/developer`),
  recoverStuck: (projectId: string) => call<{ recovered: string[] }>('POST', `${p(projectId)}/operator/recover`),
};

export type { AccessRequest };

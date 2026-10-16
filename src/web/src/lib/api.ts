import type {
  AccessRequest, AnalyzeResponse, AttentionItem, DecisionOutcome, EntraHandoff, Item, Me, PublicationStatus,
  RequestFields, RequirementRule, ReviewDecision, ReviewRun, RoleMapping,
} from './types';

/** Error returned by the API: safe message plus a correlation id to quote when reporting. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly correlationId?: string,
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
    // Session expired: SWA redirects to Entra sign-in.
    window.location.assign('/login');
    throw new ApiError(401, 'unauthenticated', 'Sign-in required.');
  }

  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const e = json?.error;
    throw new ApiError(res.status, e?.code ?? 'error', e?.message ?? `Request failed (${res.status}).`, e?.correlationId);
  }
  return json as T;
}

const p = (projectId: string) => `projects/${encodeURIComponent(projectId)}`;
const id = (value: string) => encodeURIComponent(value);

/** Client nonce for Analyze/Retry idempotency. */
export function newNonce(): string {
  return crypto.randomUUID();
}

export const api = {
  me: () => call<Me>('GET', 'me'),

  listRequests: (projectId: string) => call<{ items: Item<AccessRequest>[] }>('GET', `${p(projectId)}/requests`),
  getRequest: (projectId: string, requestId: string) => call<Item<AccessRequest>>('GET', `${p(projectId)}/requests/${id(requestId)}`),
  createRequest: (projectId: string, fields: RequestFields) => call<Item<AccessRequest>>('POST', `${p(projectId)}/requests`, fields),
  updateRequest: (projectId: string, requestId: string, fields: RequestFields, etag: string) =>
    call<Item<AccessRequest>>('PUT', `${p(projectId)}/requests/${id(requestId)}`, fields, etag),

  analyze: (projectId: string, requestId: string, nonce: string) =>
    call<AnalyzeResponse>('POST', `${p(projectId)}/requests/${id(requestId)}/analyze`, { nonce }),
  listRuns: (projectId: string, requestId: string) => call<{ items: Item<ReviewRun>[] }>('GET', `${p(projectId)}/requests/${id(requestId)}/runs`),
  getRun: (projectId: string, runId: string) => call<Item<ReviewRun>>('GET', `${p(projectId)}/runs/${id(runId)}`),
  retry: (projectId: string, runId: string, nonce: string) => call<AnalyzeResponse>('POST', `${p(projectId)}/runs/${id(runId)}/retry`, { nonce }),

  listDecisions: (projectId: string, runId: string) => call<{ items: Item<ReviewDecision>[] }>('GET', `${p(projectId)}/runs/${id(runId)}/decisions`),
  recordDecision: (projectId: string, runId: string, outcome: DecisionOutcome, comment: string) =>
    call<Item<ReviewDecision>>('POST', `${p(projectId)}/runs/${id(runId)}/decisions`, { outcome, comment: comment || null }),
  attention: () => call<{ items: AttentionItem[] }>('GET', 'attention'),
  listHandoffs: (projectId: string, requestId: string) =>
    call<{ enabled: boolean; autoAssign?: boolean; items: Item<EntraHandoff>[] }>('GET', `${p(projectId)}/requests/${id(requestId)}/handoffs`),

  listRules: (projectId: string) => call<{ items: Item<RequirementRule>[] }>('GET', `${p(projectId)}/rules`),
  createRule: (projectId: string, rule: Record<string, unknown>) => call<Item<RequirementRule>>('POST', `${p(projectId)}/rules`, rule),
  setRuleStatus: (projectId: string, docId: string, status: PublicationStatus, etag: string) =>
    call<Item<RequirementRule>>('POST', `${p(projectId)}/rules/${id(docId)}/status`, { status }, etag),
  listMappings: (projectId: string) => call<{ items: Item<RoleMapping>[] }>('GET', `${p(projectId)}/mappings`),
  createMapping: (projectId: string, mapping: Record<string, unknown>) => call<Item<RoleMapping>>('POST', `${p(projectId)}/mappings`, mapping),
  setMappingStatus: (projectId: string, docId: string, status: PublicationStatus, etag: string) =>
    call<Item<RoleMapping>>('POST', `${p(projectId)}/mappings/${id(docId)}/status`, { status }, etag),

  recoverStuck: (projectId: string) => call<{ recovered: string[] }>('POST', `${p(projectId)}/operator/recover`),
};

import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from './lib/api';

function reply(status: number, body: string, type = 'text/plain') {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(body, { status, headers: { 'Content-Type': type } })));
}

async function failure(run: () => Promise<unknown>): Promise<ApiError> {
  const e = await run().then(() => null, (err: unknown) => err);
  expect(e).toBeInstanceOf(ApiError);
  return e as ApiError;
}

afterEach(() => vi.unstubAllGlobals());

describe('API client', () => {
  it('turns the SWA gateway plain-text reply into a clear message, not a JSON parse error', async () => {
    reply(500, 'Backend call failure');
    const e = await failure(() => api.me());
    expect(e.status).toBe(500);
    expect(e.code).toBe('gateway');
    expect(e.message).toContain("The app's server did not answer (HTTP 500: Backend call failure)");
    expect(e.message).not.toMatch(/JSON|Unexpected token/);
    expect(e.message).not.toContain('may already be saved');
  });

  it('warns that an action may already be saved before it is repeated', async () => {
    reply(502, 'Backend call failure');
    const e = await failure(() => api.advance('PEST', 'r1'));
    expect(e.message).toContain('may already be saved');
  });

  it('leaves an HTML error page out of the message', async () => {
    reply(503, '<html><body>Service Unavailable</body></html>', 'text/html');
    const e = await failure(() => api.me());
    expect(e.message).toContain('(HTTP 503)');
    expect(e.message).not.toContain('<');
  });

  it('keeps the API error message, code and reference', async () => {
    reply(403, JSON.stringify({ error: { code: 'forbidden', message: 'Not permitted.', correlationId: 'c1' } }), 'application/json');
    const e = await failure(() => api.me());
    expect([e.status, e.code, e.message, e.correlationId]).toEqual([403, 'forbidden', 'Not permitted.', 'c1']);
  });

  it('returns the parsed body on success', async () => {
    reply(200, JSON.stringify({ items: [] }), 'application/json');
    await expect(api.queue()).resolves.toEqual({ items: [] });
  });
});

describe('offboarding plan', () => {
  it('asks for a new plan only when told to replace the unconfirmed one', async () => {
    const urls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify({ status: 'Planning' }), { status: 202, headers: { 'Content-Type': 'application/json' } });
    }));
    await api.planOffboarding('PEST', 'leaver@contoso.com');
    await api.planOffboarding('PEST', 'leaver@contoso.com', true);
    expect(urls).toEqual([
      '/api/offboarding/leaver%40contoso.com?projectId=PEST&mode=plan',
      '/api/offboarding/leaver%40contoso.com?projectId=PEST&mode=plan&replace=true',
    ]);
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from './lib/api';
import { deleteReasonError } from './lib/developer';

describe('developer tools', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('a real request needs a reason to be deleted; a simulation does not', () => {
    expect(deleteReasonError(false, '')).not.toBeNull();
    expect(deleteReasonError(false, ' abc ')).not.toBeNull();
    expect(deleteReasonError(false, 'duplicate')).toBeNull();
    expect(deleteReasonError(true, '')).toBeNull();
  });

  it('calls the simulate, simulated bundle and delete routes', async () => {
    const calls: { url: string; body: unknown }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : null });
      return new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));
    await api.simulate('PEST', { enterpriseId: 'a@b.c' } as never);
    await api.submitBundle('PEST', { selectedResources: ['x'] } as never, true);
    await api.deleteRequest('PEST', 'r1', ' duplicate ');
    expect(calls.map((c) => c.url)).toEqual([
      '/api/projects/PEST/requests/simulate',
      '/api/projects/PEST/requests/bundle?simulate=true',
      '/api/projects/PEST/requests/r1/delete',
    ]);
    expect(calls[2].body).toEqual({ reason: 'duplicate', confirm: true });
  });
});

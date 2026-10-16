import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { href, parse } from './lib/router';

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) return sources(p);
    return p.endsWith('.tsx') ? [p] : [];
  });
}

describe('scope guardrails', () => {
  // No control may approve, grant, pay or self-register. The banner sentence itself is the only allowed mention.
  const forbidden = /\b(approve|approval|grant(ed)? access|assign (group|role)|payment|pay now|sign ?up|register me)\b/i;

  it('has no approve / grant / payment / self-sign-up controls', () => {
    const offenders: string[] = [];
    for (const file of sources(join(__dirname))) {
      readFileSync(file, 'utf8').split('\n').forEach((line, i) => {
        const code = line.replace(/\/\/.*$/, '').replace(/\{\/\*.*\*\/\}/, '');
        if (code.includes('does not approve or grant access') || code.includes('It does not approve the request')) return;
        if (forbidden.test(code)) offenders.push(`${file}:${i + 1}: ${line.trim()}`);
      });
    }
    expect(offenders).toEqual([]);
  });
});

describe('router', () => {
  it('round-trips routes', () => {
    for (const r of [
      { page: 'requests' }, { page: 'new' }, { page: 'request', requestId: 'abc.r1' },
      { page: 'edit', requestId: 'x' }, { page: 'queue' }, { page: 'reference' }, { page: 'operator' },
    ] as const) {
      expect(parse(href(r))).toEqual(r);
    }
  });

  it('falls back to the request list for unknown paths', () => {
    expect(parse('#/../../etc')).toEqual({ page: 'requests' });
  });
});

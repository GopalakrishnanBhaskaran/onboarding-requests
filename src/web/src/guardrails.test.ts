import { readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { href, parse } from './lib/router';
import { APPROVE_LABEL, FLOW_OVERVIEW, WHO_DOES_WHAT } from './lib/flow';

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) return sources(p);
    return p.endsWith('.tsx') ? [p] : [];
  });
}

/** Source lines without comments, so a doc comment can explain the rules without tripping them. */
function lines(file: string): { n: number; code: string }[] {
  return readFileSync(file, 'utf8').split('\n').map((line, i) => ({
    n: i + 1,
    code: line.replace(/\/\/.*$/, '').replace(/\{\/\*.*\*\/\}/, '').replace(/^\s*\/?\*.*$/, ''),
  }));
}

const files = sources(join(__dirname));
const DECISION_PANEL = 'ApprovalPanel.tsx';

describe('scope guardrails', () => {
  it('has no grant / assign / create / payment / self-sign-up controls', () => {
    // The agent never creates or assigns; the teams do that in their own tools and only record it here.
    const forbidden = /\b(grant(ed)? access|grant access|assign (the )?(group|role)s?|add (user )?to group|create (user|group)|payment|pay now|sign ?up|register me)\b/i;
    const offenders = files.flatMap((f) => lines(f).filter((l) => forbidden.test(l.code)).map((l) => `${f}:${l.n}: ${l.code.trim()}`));
    expect(offenders).toEqual([]);
  });

  it('puts decision controls only in the Owner / Security decision panel', () => {
    const offenders = files
      .filter((f) => basename(f) !== DECISION_PANEL)
      .flatMap((f) => lines(f)
        .filter((l) => /\b(approve|reject)\b/i.test(l.code) || l.code.includes('APPROVE_LABEL') || l.code.includes('api.respond('))
        .map((l) => `${f}:${l.n}: ${l.code.trim()}`));
    expect(offenders).toEqual([]);
    expect(Object.values(APPROVE_LABEL)).toEqual(['Approve business need', 'Approve risk and access level', 'Reject']);
  });

  it('never asks for a password or the Temporary Access Pass value', () => {
    const offenders = files.flatMap((f) => lines(f)
      .filter((l) => /type=["']password["']/.test(l.code) || /label=["'][^"']*(password|pass code|pass value|secret|TAP value)/i.test(l.code))
      .map((l) => `${f}:${l.n}: ${l.code.trim()}`));
    expect(offenders).toEqual([]);
  });

  it('states the flow and who does what to every requester upfront', () => {
    expect(FLOW_OVERVIEW).toHaveLength(7);
    expect(FLOW_OVERVIEW[0]).toMatch(/Requester submits/);
    expect(FLOW_OVERVIEW[2]).toMatch(/Owner approval/);
    expect(FLOW_OVERVIEW[3]).toMatch(/Security approval/);
    expect(WHO_DOES_WHAT.map(([who]) => who)).toEqual(['AI agent', 'Owner', 'Security', 'ID team', 'Role assignment team', 'PMO']);
    expect(WHO_DOES_WHAT[0][1]).toMatch(/Never approves, creates or assigns/);
    const form = readFileSync(join(__dirname, 'pages', 'RequestFormPage.tsx'), 'utf8');
    expect(form).toContain('FLOW_OVERVIEW.map');
    expect(form).toContain('WHO_DOES_WHAT.map');
  });
});

describe('router', () => {
  it('round-trips routes', () => {
    for (const r of [
      { page: 'requests' }, { page: 'new' }, { page: 'request', requestId: 'abc.r1' },
      { page: 'edit', requestId: 'x' }, { page: 'queue' }, { page: 'sheet' }, { page: 'operator' },
    ] as const) {
      expect(parse(href(r))).toEqual(r);
    }
  });

  it('falls back to the request list for unknown paths', () => {
    expect(parse('#/../../etc')).toEqual({ page: 'requests' });
    expect(parse('#/reference')).toEqual({ page: 'requests' });
  });
});

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { href, parse } from './lib/router';
import {
  AGENT_BANNER, AGENT_TAP_BANNER, APPROVE_LABEL, FLOW_OVERVIEW, TEMPLATE_PLACEHOLDER, WHO_DOES_WHAT, justificationTemplate, whoDoesWhat,
} from './lib/flow';

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
    // v2.1 agent provisioning on: still never approves, only after both approvals, the TAP stays with the ID team.
    expect(whoDoesWhat(true)[0][1]).toMatch(/Never approves\. Only after both approvals/);
    expect(whoDoesWhat(true).find(([who]) => who === 'ID team')?.[1]).toMatch(/Temporary Access Pass/);
    expect(AGENT_BANNER).toMatch(/never approves.*Only after both approvals.*ID team issues the Temporary Access Pass/);
    // Agent TAP on: the agent mails the joiner's pass; still never approves; the ID team takes what the agent leaves.
    expect(whoDoesWhat(true, true)[0][1]).toMatch(/Never approves\. Only after both approvals.*one-time Temporary Access Pass.*MFA/);
    expect(AGENT_TAP_BANNER).toMatch(/never approves.*Only after both approvals.*one-time Temporary Access Pass/);
    const form = readFileSync(join(__dirname, 'pages', 'RequestFormPage.tsx'), 'utf8');
    expect(form).toContain('FLOW_OVERVIEW.map');
    expect(form).toContain('whoDoesWhat(agent, tap).map');
  });

  it('offers resource and access level as dropdowns only, and a justification template that must be filled in', () => {
    const form = readFileSync(join(__dirname, 'pages', 'RequestFormPage.tsx'), 'utf8');
    for (const label of ['Resource', 'Access level', 'Environment', 'Tenant']) {
      const field = form.slice(form.indexOf(`label="${label}"`));
      expect(field.slice(0, field.indexOf('</Field>'))).toContain('<Select');
      expect(field.slice(0, field.indexOf('</Field>'))).not.toContain('<Input');
    }
    const t = justificationTemplate('Support Reader', 'SAMPLE PEST Platform (ph)', 'Dev');
    expect(t).toContain('Access needed: Support Reader on SAMPLE PEST Platform (ph) (Dev).');
    expect(TEMPLATE_PLACEHOLDER.test(t)).toBe(true);
    const filled = t.replace(/\[[^\]]*\]/g, 'Support engineer');
    expect(TEMPLATE_PLACEHOLDER.test(filled)).toBe(false);
  });

  it('shows no SAMPLE wording in the screens', () => {
    const offenders = files.flatMap((f) => lines(f).filter((l) => /SAMPLE|sample-access/.test(l.code)).map((l) => `${f}:${l.n}: ${l.code.trim()}`));
    expect(offenders).toEqual([]);
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

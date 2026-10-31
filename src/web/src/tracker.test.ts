import { describe, expect, it } from 'vitest';
import {
  applyFilter, digest, duration, inBucket, journey, journeyText, laneOf, matches, stepSeconds, waitingLong,
} from './lib/tracker';
import type { CheckResult, RequestStatus, StatusLine } from './lib/types';

const line = (over: Partial<StatusLine>): StatusLine => ({
  requestId: 'abcdef12.r1', projectId: 'P', requester: 'alice@contoso.example', status: 'AwaitingOwner', currentStage: 'Awaiting Owner',
  waitingOn: 'Owner', waitingOnLabel: 'Owner', done: ['Submitted', 'AI review'], next: 'Named Owner decides', timeInStageSeconds: 60,
  timeInStage: '1m', adoLink: null, domainId: 'jane.doe@contoso.example', resource: 'Finance Reports', accessLevel: 'Viewer',
  updatedAt: '2026-10-31T10:00:00Z', ...over,
});

const states = (status: RequestStatus, done: string[]) => journey(status, done).map((s) => s.state);

describe('journey', () => {
  it('marks the steps before the current one done', () => {
    expect(states('AwaitingOwner', ['Submitted', 'AI review'])).toEqual(['done', 'done', 'current', 'upcoming', 'upcoming', 'upcoming', 'upcoming']);
    expect(states('WithRoleTeam', [])).toEqual(['done', 'done', 'done', 'done', 'done', 'current', 'upcoming']);
  });

  it('shows a returned request at the AI review, waiting on the requester', () => {
    expect(states('Returned', ['Submitted'])[1]).toBe('returned');
  });

  it('stops a rejected or failed request where it did not get past', () => {
    expect(states('Rejected', ['Submitted', 'AI review'])).toEqual(['done', 'done', 'stopped', 'upcoming', 'upcoming', 'upcoming', 'upcoming']);
    expect(states('Rejected', ['Submitted', 'AI review', 'Owner approval'])[3]).toBe('stopped');
    expect(states('FailedNeedsDev', ['Submitted', 'AI review', 'Owner approval', 'Security approval'])[4]).toBe('failed');
  });

  it('marks every step done when the request is completed', () => {
    expect(states('Completed', []).every((s) => s === 'done')).toBe(true);
    expect(journeyText(journey('Completed', []))).toBe('All 7 steps done.');
  });

  it('keeps the full wording of each step for the reader', () => {
    const steps = journey('AwaitingSecurity', ['Submitted', 'AI review', 'Owner approval']);
    expect(steps[2].detail).toBe('3. Owner approval');
    expect(journeyText(steps)).toBe('Step 4 of 7, Security: in progress. Done: Submitted, AI review, Owner.');
  });

  it('adds up the time per stage into the journey steps', () => {
    const secs = stepSeconds([
      { name: 'Submitted', seconds: 5 }, { name: 'AI Review', seconds: 60 }, { name: 'Returned', seconds: 120 },
      { name: 'Awaiting Owner', seconds: 7200 }, { name: 'Awaiting Prod approval', seconds: 30 }, { name: 'Awaiting Security', seconds: 30 },
    ]);
    expect(secs).toEqual([5, 180, 7200, 60, null, null, null]);
  });

  it('formats durations like the server', () => {
    expect(duration(59)).toBe('0m');
    expect(duration(3 * 3600 + 5 * 60)).toBe('3h 5m');
    expect(duration(2 * 86400 + 4 * 3600)).toBe('2d 4h');
  });
});

describe('tracker', () => {
  const items = [
    line({ requestId: 'aaaa1111.r1', status: 'AwaitingOwner', timeInStageSeconds: 30 * 3600 }),
    line({ requestId: 'bbbb2222.r1', status: 'Returned', waitingOn: 'Requester', timeInStageSeconds: 60 }),
    line({ requestId: 'cccc3333.r1', status: 'Completed', waitingOn: 'None', timeInStageSeconds: 99 * 3600, resource: 'Databricks Engineering' }),
    line({ requestId: 'dddd4444.r1', status: 'AiReview', waitingOn: 'Agent', timeInStageSeconds: 48 * 3600 }),
    line({ requestId: 'eeee5555.r1', status: 'Rejected', waitingOn: 'None' }),
  ];

  it('flags a long wait only where a person has to act', () => {
    expect(waitingLong(items[0])).toBe(true);
    expect(waitingLong(items[2])).toBe(false); // completed
    expect(waitingLong(items[3])).toBe(false); // the agent's turn
  });

  it('sorts requests into the overview buckets', () => {
    const ids = (b: Parameters<typeof inBucket>[1]) => items.filter((r) => inBucket(r, b)).map((r) => r.requestId.slice(0, 4));
    expect(ids('active')).toEqual(['aaaa', 'bbbb', 'dddd']);
    expect(ids('attention')).toEqual(['aaaa', 'bbbb']);
    expect(ids('completed')).toEqual(['cccc']);
    expect(ids('closed')).toEqual(['eeee']);
    expect(ids('all')).toHaveLength(5);
  });

  it('puts every in-progress status in one lane', () => {
    expect(laneOf('AwaitingProdApproval')).toBe('security');
    expect(laneOf('Submitted')).toBe('review');
    expect(laneOf('Completed')).toBeNull();
  });

  it('searches every word across id, joiner, resource and stage', () => {
    expect(matches(items[2], 'databricks CCCC')).toBe(true);
    expect(matches(items[2], 'databricks finance')).toBe(false);
    expect(matches(items[0], '  ')).toBe(true);
  });

  it('filters by lane and query, longest wait first on request', () => {
    const out = applyFilter(items, { bucket: 'active', lane: null, query: '', longestFirst: true });
    expect(out.map((r) => r.requestId.slice(0, 4))).toEqual(['dddd', 'aaaa', 'bbbb']);
    expect(applyFilter(items, { bucket: 'all', lane: 'owner', query: '', longestFirst: false }).map((r) => r.requestId.slice(0, 4))).toEqual(['aaaa']);
  });
});

describe('check digest', () => {
  const c = (code: string, status: CheckResult['status']): CheckResult => ({ code, group: 'G', status, detail: code });

  it('counts each result and lists what did not pass, most serious first', () => {
    const d = digest([c('A', 'Pass'), c('B', 'NotApplicable'), c('C', 'Fail'), c('D', 'Flagged'), c('E', 'Pass'), c('F', 'Fail')]);
    expect(d.total).toBe(6);
    expect(d.counts).toEqual({ Fail: 2, Flagged: 1, Pending: 0, NotApplicable: 1, Pass: 2 });
    expect(d.attention.map((x) => x.code)).toEqual(['C', 'F', 'D', 'B']);
  });
});

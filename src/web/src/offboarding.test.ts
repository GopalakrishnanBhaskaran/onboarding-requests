import { describe, expect, it } from 'vitest';
import { canConfirm, canPlan, changes, inProgress, planExpired } from './lib/offboarding';
import type { OffboardingView } from './lib/types';

const run = (status: OffboardingView['status'], results: OffboardingView['steps'][number]['result'][] = []): OffboardingView => ({
  offboardingId: 'off-1', enterpriseId: 'sample.leaver@sample.invalid', objectId: '', mode: 'plan', status,
  steps: results.map((result, i) => ({ name: `step ${i}`, target: 't', action: 'a', result, detail: '' })),
  leftovers: [], notCovered: [], pmoNotified: false, correlationId: 'c',
});

describe('F5 offboarding panel', () => {
  it('confirms only a ready plan that was checked', () => {
    expect(canConfirm(run('Planned'), true)).toBe(true);
    expect(canConfirm(run('Planned'), false)).toBe(false);
    expect(canConfirm(run('Planning'), true)).toBe(false);
    expect(canConfirm(run('Executing'), true)).toBe(false);
    expect(canConfirm(null, true)).toBe(false);
  });

  it('asks for a new plan only when none is open (a double click reuses it)', () => {
    expect(canPlan(null)).toBe(true);
    expect(canPlan(run('Completed'))).toBe(true);
    expect(canPlan(run('PartiallyCompleted'))).toBe(true);
    for (const s of ['Planning', 'Planned', 'Executing'] as const) expect(canPlan(run(s))).toBe(false);
  });

  it('offers a new plan when the confirm says the shown plan expired', () => {
    const expired = { status: 409, code: 'plan_expired', message: 'old' };
    expect(planExpired(expired)).toBe(true);
    expect(planExpired({ status: 409, code: 'plan_not_ready' })).toBe(false);
    expect(planExpired(null)).toBe(false);
    expect(canPlan(run('Planned'), planExpired(expired))).toBe(true);
  });

  it('refreshes while the worker plans or executes and counts the planned changes', () => {
    expect(inProgress(run('Planning'))).toBe(true);
    expect(inProgress(run('Executing'))).toBe(true);
    expect(inProgress(run('Planned'))).toBe(false);
    expect(changes(run('Planned', ['Done', 'Planned', 'Skipped', 'Planned']))).toBe(2);
  });
});

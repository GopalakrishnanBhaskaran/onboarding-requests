import type { OffboardingView } from './types';

/** The worker is on it (plan or execution): the page refreshes until it is done. */
export function inProgress(v: OffboardingView | null): boolean {
  return v?.status === 'Planning' || v?.status === 'Executing';
}

/** A plan that is ready and was ticked as checked can be confirmed (the API still enforces the switch, age and four eyes). */
export function canConfirm(v: OffboardingView | null, checked: boolean): boolean {
  return !!v && v.status === 'Planned' && checked;
}

/**
 * A new plan can be asked for when there is none, the last one finished, or the shown plan expired unconfirmed (the
 * confirm answered 409 plan_expired; the API then closes it and makes a new one).
 */
export function canPlan(v: OffboardingView | null, expired = false): boolean {
  return !v || expired || !['Planning', 'Planned', 'Executing'].includes(v.status);
}

/** The confirm was refused because the plan is too old: offer a new plan. */
export function planExpired(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { code?: unknown }).code === 'plan_expired';
}

/** The confirm was refused because offboarding changes are switched off (AGENT_OFFBOARDING): expected, nothing changed. */
export function switchedOff(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { code?: unknown }).code === 'offboarding_off';
}

/** What will change, in the plan's own words: the steps the run will do. */
export function changes(v: OffboardingView): number {
  return v.steps.filter((s) => s.result === 'Planned').length;
}

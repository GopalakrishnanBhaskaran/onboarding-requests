import type { OffboardingView } from './types';

/** The worker is on it (plan or execution): the page refreshes until it is done. */
export function inProgress(v: OffboardingView | null): boolean {
  return v?.status === 'Planning' || v?.status === 'Executing';
}

/** A plan that is ready and was ticked as checked can be confirmed (the API still enforces the switch, age and four eyes). */
export function canConfirm(v: OffboardingView | null, checked: boolean): boolean {
  return !!v && v.status === 'Planned' && checked;
}

/** A new plan can be asked for when there is none or the last one finished. */
export function canPlan(v: OffboardingView | null): boolean {
  return !v || !['Planning', 'Planned', 'Executing'].includes(v.status);
}

/** What will change, in the plan's own words: the steps the run will do. */
export function changes(v: OffboardingView): number {
  return v.steps.filter((s) => s.result === 'Planned').length;
}

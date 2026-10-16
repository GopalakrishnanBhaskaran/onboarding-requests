import type { ProcessingStatus, Readiness, Severity } from './types';

export const readinessLabel: Record<Readiness, string> = {
  NeedsInformation: 'Needs information',
  ReadyForHumanReview: 'Ready for human review',
  ManualReviewRequired: 'Manual review required',
};

export const readinessColor: Record<Readiness, 'warning' | 'success' | 'danger'> = {
  NeedsInformation: 'warning',
  ReadyForHumanReview: 'success',
  ManualReviewRequired: 'danger',
};

export const severityColor: Record<Severity, 'informative' | 'warning' | 'danger'> = {
  Info: 'informative',
  Warning: 'warning',
  Blocking: 'danger',
};

export const fieldLabel: Record<string, string> = {
  requestType: 'Request type',
  role: 'Business role',
  environment: 'Environment',
  manager: 'Line manager',
  justification: 'Justification',
  startDate: 'Start date',
  endDate: 'End date',
};

export const inProgress = (s: ProcessingStatus) => s === 'Queued' || s === 'Running';

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

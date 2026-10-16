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
  joinerUpn: 'New joiner sign-in name',
};

export const handoffLabel: Record<string, string> = {
  NotEligible: 'Cannot hand off',
  Queued: 'Accepted, Entra request queued',
  AwaitingJoinerRequest: 'Waiting for the new joiner to request',
  PendingApproval: 'Waiting for Entra approval',
  Delivering: 'Approved, access being added',
  Delivered: 'Access delivered by Entra',
  Denied: 'Denied in Entra',
  Cancelled: 'Cancelled in Entra',
  Failed: 'Entra could not deliver',
  Expired: 'Access ended (expired)',
};

export const handoffColor: Record<string, 'informative' | 'warning' | 'success' | 'danger' | 'subtle'> = {
  NotEligible: 'danger',
  Queued: 'informative',
  AwaitingJoinerRequest: 'informative',
  PendingApproval: 'warning',
  Delivering: 'informative',
  Delivered: 'success',
  Denied: 'danger',
  Cancelled: 'subtle',
  Failed: 'danger',
  Expired: 'subtle',
};

export const inProgress = (s: ProcessingStatus) => s === 'Queued' || s === 'Running';

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

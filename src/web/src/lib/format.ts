import type { CheckStatus, RequestStatus, TeamTaskState } from './types';

export const statusLabel: Record<RequestStatus, string> = {
  Submitted: 'Submitted',
  AiReview: 'AI Review',
  Returned: 'Returned',
  AwaitingOwner: 'Awaiting Owner',
  AwaitingSecurity: 'Awaiting Security',
  Rejected: 'Rejected',
  WithIdTeam: 'With ID Team',
  WithRoleTeam: 'With Role Team',
  Verifying: 'Verifying',
  Completed: 'Completed',
  FailedNeedsDev: 'Failed-Needs-Dev',
};

export const statusColor: Record<RequestStatus, 'informative' | 'warning' | 'success' | 'danger' | 'subtle' | 'brand'> = {
  Submitted: 'informative',
  AiReview: 'informative',
  Returned: 'warning',
  AwaitingOwner: 'brand',
  AwaitingSecurity: 'brand',
  Rejected: 'subtle',
  WithIdTeam: 'brand',
  WithRoleTeam: 'brand',
  Verifying: 'informative',
  Completed: 'success',
  FailedNeedsDev: 'danger',
};

export const checkColor: Record<CheckStatus, 'success' | 'danger' | 'informative' | 'subtle' | 'warning'> = {
  Pass: 'success',
  Fail: 'danger',
  Pending: 'informative',
  NotApplicable: 'subtle',
  Flagged: 'warning',
};

export const checkLabel: Record<CheckStatus, string> = {
  Pass: 'Pass',
  Fail: 'Fail',
  Pending: 'Pending',
  NotApplicable: 'Not checked',
  Flagged: 'Risk flag',
};

export const taskLabel: Record<TeamTaskState, string> = {
  Open: 'Open',
  Reported: 'Reported — AI verifying',
  Returned: 'Returned with differences',
  Verified: 'Verified',
  Cancelled: 'Cancelled',
};

export const fieldLabel: Record<string, string> = {
  enterpriseId: 'Enterprise ID',
  firstName: 'First name',
  lastName: 'Last name',
  domainId: 'Domain-specific ID',
  resource: 'Resource',
  accessLevel: 'Access level',
  justification: 'Justification',
  owner: 'Owner',
  guest: 'Guest user',
};

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** yyyy-MM-ddTHH:mm in local time, for datetime-local inputs. */
export function localInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

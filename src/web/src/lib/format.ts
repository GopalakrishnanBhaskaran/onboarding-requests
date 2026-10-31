import type { AzureRoleGrant, CheckStatus, RequestStatus, ReviewedScope, TeamTaskState } from './types';

export const statusLabel: Record<RequestStatus, string> = {
  Submitted: 'Submitted',
  AiReview: 'AI Review',
  Returned: 'Returned',
  AwaitingOwner: 'Awaiting Owner',
  AwaitingSecurity: 'Awaiting Security',
  AwaitingProdApproval: 'Awaiting Prod approval',
  Rejected: 'Rejected',
  WithIdTeam: 'With ID Team',
  WithRoleTeam: 'With Role Team',
  Verifying: 'Verifying',
  Completed: 'Completed',
  FailedNeedsDev: 'Failed-Needs-Dev',
  Closed: 'Closed – offboarded',
};

export const statusColor: Record<RequestStatus, 'informative' | 'warning' | 'success' | 'danger' | 'subtle' | 'brand'> = {
  Submitted: 'informative',
  AiReview: 'informative',
  Returned: 'warning',
  AwaitingOwner: 'brand',
  AwaitingSecurity: 'brand',
  AwaitingProdApproval: 'warning',
  Rejected: 'subtle',
  WithIdTeam: 'brand',
  WithRoleTeam: 'brand',
  Verifying: 'informative',
  Completed: 'success',
  FailedNeedsDev: 'danger',
  Closed: 'subtle',
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
  environment: 'Environment',
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

/** The Azure roles on a reviewed scope: the list, else the single pair of older reviews. */
export function grantsOf(scope: ReviewedScope | null | undefined): AzureRoleGrant[] {
  if (!scope) return [];
  if (scope.azureRoles && scope.azureRoles.length > 0) return scope.azureRoles;
  return scope.azureRoleDefinitionId && scope.azureScope
    ? [{ roleName: scope.azureRoleDefinitionId, roleDefinitionId: scope.azureRoleDefinitionId, scope: scope.azureScope, resourceName: null, resourceType: null }]
    : [];
}

/** Last path segment of an Azure scope (the resource or resource group name). */
export function scopeName(scope: string | null | undefined): string {
  if (!scope) return '—';
  const parts = scope.replace(/\/+$/, '').split('/');
  return parts[parts.length - 1] || scope;
}

// Mirrors the API contracts in src/core. Server is the authority for every value here.

export type AppRole = 'Requester' | 'Owner' | 'Security' | 'IdTeam' | 'RoleTeam' | 'PMO' | 'Operator' | 'Reviewer' | 'Maintainer';
export type RequestStatus =
  | 'Submitted' | 'AiReview' | 'Returned' | 'AwaitingOwner' | 'AwaitingSecurity' | 'Rejected'
  | 'WithIdTeam' | 'WithRoleTeam' | 'Verifying' | 'Completed' | 'FailedNeedsDev';
export type WaitingOn = 'None' | 'Agent' | 'Requester' | 'Owner' | 'Security' | 'IdTeam' | 'RoleTeam' | 'User' | 'Developer';
export type ProcessingStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Superseded';
export type ReviewOutcome = 'Pass' | 'Fail' | 'Pending';
export type CheckStatus = 'Pass' | 'Fail' | 'Pending' | 'NotApplicable' | 'Flagged';
export type GroupState = 'Exists' | 'ToBeCreated';
export type TeamTaskState = 'Open' | 'Reported' | 'Returned' | 'Verified' | 'Cancelled';
export type ActionName = 'ApproveOwner' | 'ApproveSecurity' | 'IdTeamTask' | 'RoleTeamTask' | 'Resubmit';

export interface Item<T> {
  value: T;
  etag: string;
}

export interface ProjectSummary {
  projectId: string;
  name: string;
  roles: AppRole[];
  isSample: boolean;
  /** The Entra tenant of the project (tenant separation), e.g. "PEST". */
  tenantName?: string | null;
  /** Environments a request picks from; empty = the project has no environment field. */
  environments?: string[];
  domainIdSuffix?: string | null;
}

export interface Me {
  userId: string;
  displayName: string;
  projects: ProjectSummary[];
  /** v2.1: the AI agent provisions after both approvals (the wording in the app follows it). */
  agentProvisioning?: boolean;
  agentRoleAssignment?: boolean;
  /** Deployment environment: pilot, dev, test or prod. */
  appEnvironment?: string;
  /** Notifications are mailed by the worker (else kept in the app only). */
  mailEnabled?: boolean;
  /** Test environment: every mail goes to this one address. */
  mailRedirectTo?: string | null;
  /** Agent TAP: the agent mails a new joiner's one-time pass, initial password and MFA steps. */
  agentTap?: boolean;
}

export type RowFlag = 'None' | 'Amber' | 'Red';

/** One Azure role of a group, as the response document states it (role × resource). */
export interface AzureRoleGrant {
  roleName: string;
  roleDefinitionId: string | null;
  scope: string | null;
  resourceName: string | null;
  resourceType: string | null;
}

export interface IntakeFields {
  enterpriseId?: string | null;
  firstName?: string | null;
  lastName?: string | null;
  resource?: string | null;
  accessLevel?: string | null;
  environment?: string | null;
  justification?: string | null;
  owner?: string | null;
  guest?: boolean;
}

export interface CheckResult {
  code: string;
  group: string;
  status: CheckStatus;
  detail: string;
}

export interface Approval {
  stage: 'Owner' | 'Security';
  decision: 'Approved' | 'Rejected';
  by: string;
  byName: string;
  at: string;
  remarks: string | null;
  reviewRunId: string;
  channel: string;
  superseded: boolean;
}

export interface ReviewedScope {
  reviewRunId: string;
  mappingRowId: string;
  mappingRowVersion: number;
  entraGroup: string;
  role: string;
  roleScope: string | null;
  /** The Azure role the approvers approve (v2.1: the only role the agent may assign). */
  azureRoleDefinitionId: string | null;
  azureScope: string | null;
  /** Every Azure role approved for the group (empty on older reviews: then the single pair above). */
  azureRoles?: AzureRoleGrant[];
  environment?: string | null;
  groupState: GroupState;
  groupExistsInEntra: boolean | null;
  riskFlags: string[];
  summary: string;
}

export interface TapRecord {
  issuedAt: string;
  expiresAt: string;
  oneTime: boolean;
  issuedBy: string;
}

export interface AssignedRole {
  role: string;
  scope: string | null;
}

export interface TeamTask {
  id: string;
  team: 'IdTeam' | 'RoleTeam';
  state: TeamTaskState;
  instructions: string;
  createdAt: string;
  sinceAt: string;
  pickedUpAt: string | null;
  pickedUpBy: string | null;
  reportedAt: string | null;
  reportedBy: string | null;
  idReport: {
    userPrincipalName: string;
    groupName: string;
    userCreated: boolean;
    groupCreated: boolean;
    addedToGroup: boolean;
    mappingSheetUpdated: boolean;
    tap: TapRecord | null;
    notes: string | null;
  } | null;
  roleReport: { assignedRoles: AssignedRole[]; notes: string | null } | null;
  returnCount: number;
  lastDifference: string | null;
  checks: CheckResult[];
  /** v2.1: what the AI agent did itself after both approvals, or why it left the task to the team. */
  agent: AgentWork | null;
}

export interface AgentWork {
  at: string;
  userCreated: boolean;
  groupCreated: boolean;
  memberAdded: boolean;
  membership: boolean;
  mappingUpdated: boolean;
  roleAssigned: boolean;
  completed: boolean;
  manual: string | null;
  /** First refusal of the agent's write; it retries every run until the new permission reaches it (up to ~24 h). */
  refusedSince?: string | null;
}

export interface WorkflowEvent {
  at: string;
  actor: string;
  step: string;
  note: string;
}

export interface WorkflowState {
  status: RequestStatus;
  waitingOn: WaitingOn;
  reviewRunId: string | null;
  scope: ReviewedScope | null;
  approvals: Approval[];
  idTeamTask: TeamTask | null;
  roleTeamTask: TeamTask | null;
  finalChecks: CheckResult[];
  ado: { workItemId: number | null; url: string | null; error: string | null } | null;
  lastReason: string | null;
  events: WorkflowEvent[];
}

export interface AccessRequest extends IntakeFields {
  id: string;
  projectId: string;
  domainId: string | null;
  createdBy: string;
  createdByName: string | null;
  createdAt: string;
  updatedAt: string;
  revision: number;
  flow: WorkflowState | null;
}

export interface StatusLine {
  requestId: string;
  projectId: string;
  requester: string;
  status: RequestStatus;
  currentStage: string;
  waitingOn: WaitingOn;
  waitingOnLabel: string;
  done: string[];
  next: string;
  timeInStageSeconds: number;
  timeInStage: string;
  adoLink: string | null;
  domainId: string | null;
  resource: string | null;
  accessLevel: string | null;
  updatedAt: string;
}

export interface DurationItem {
  name: string;
  seconds: number;
  text: string;
}

export interface Timings {
  perStage: DurationItem[];
  perTeam: DurationItem[];
  total: DurationItem;
}

export type NotificationKind =
  | 'FlowOverview' | 'ReviewReturned' | 'ApprovalRequested' | 'Reminder' | 'Escalation' | 'Rejected' | 'TeamTask' | 'TaskReturned'
  | 'TapCleanupNeeded' | 'AccessReady' | 'CompletionSummary' | 'PmoInformed' | 'Closure' | 'SecurityAlert' | 'DailySummary' | 'DeveloperReport'
  | 'ProvisioningSummary';

export interface Notification {
  id: string;
  kind: NotificationKind;
  to: string[];
  subject: string;
  body: string;
  createdAt: string;
  status: 'Pending' | 'Sent' | 'Recorded' | 'Failed';
  /** Where the mail went (a test environment redirects every mail to one address). */
  deliveredTo?: string[] | null;
  lastError?: string | null;
}

export interface Issue {
  id: string;
  requestId: string | null;
  kind: string;
  step: string;
  severity: 'Info' | 'Warning' | 'High';
  summary: string;
  detail: string | null;
  status: 'Open' | 'AutoFixed' | 'Resolved';
  occurrences: number;
  lastSeenAt: string;
}

export interface RequestView {
  request: Item<AccessRequest>;
  status: StatusLine;
  timings: Timings;
  actions: ActionName[];
  notifications: Notification[];
  issues: Issue[];
  flowOverview: string[];
  whoDoesWhat: string;
  /** Why no decision is shown while the request waits for an approval (wrong sign-in, missing role, four eyes). */
  approvalHint?: string | null;
}

export interface ReviewResult {
  schemaVersion: string;
  reviewRunId: string;
  requestRevision: number;
  outcome: ReviewOutcome;
  summary: string;
  checks: CheckResult[];
  riskFlags: string[];
  mapping: {
    status: 'Matched' | 'NotFound' | 'Ambiguous' | 'NotApplicable';
    rowId: string | null;
    rowVersion: number | null;
    entraGroup: string | null;
    role: string | null;
    roleScope: string | null;
    groupState: GroupState | null;
    groupExistsInEntra: boolean | null;
    explanation: string;
  };
  findings: { code: string; field: string | null; severity: string; message: string }[];
  returnReasons: string[];
  humanApprovalRequired: true;
  limitations: string[];
}

export interface ReviewRun {
  id: string;
  requestRevision: number;
  processingStatus: ProcessingStatus;
  outcome: ReviewOutcome | null;
  reviewerProvider: string;
  reviewerVersion: string;
  trigger: string | null;
  sheetFingerprint: string | null;
  snapshotHash: string;
  result: ReviewResult | null;
  queuedAt: string;
  completedAt: string | null;
  errorCategory: string | null;
}

export interface MappingRow {
  id: string;
  rowId: string;
  resource: string;
  accessLevel: string;
  entraGroup: string;
  role: string;
  roleScope: string | null;
  /** Optional: the Azure role (definition GUID) and scope the AI agent assigns to the group after both approvals. */
  azureRoleDefinitionId: string | null;
  azureScope: string | null;
  /** Every Azure role the group carries (response document). */
  azureRoles?: AzureRoleGrant[];
  environment?: string | null;
  groupObjectId?: string | null;
  jobRoles?: string | null;
  flag?: RowFlag;
  notes?: string | null;
  privileged: boolean;
  sensitiveResource: boolean;
  deprecated: boolean;
  groupState: GroupState;
  sourceReference: string;
  version: number;
  updatedBy: string;
  updatedAt: string;
}

export interface ResponseRecord {
  id: string;
  requestId: string;
  userId: string;
  resource: string;
  accessLevel: string;
  environment?: string | null;
  entraGroup: string;
  role: string;
  roleScope: string | null;
  azureRoles?: AzureRoleGrant[];
  state: 'PendingApproval' | 'Approved' | 'Assigned' | 'Rejected' | 'Revoked';
  assignedRoles: AssignedRole[];
  assignedBy: string | null;
  updatedAt: string;
}

export interface ActionItem {
  status: StatusLine;
  action: ActionName;
}

export interface DailySummary {
  date: string;
  submitted: number;
  completed: number;
  rejected: number;
  returned: number;
  failedNeedsDev: number;
  waitTimePerTeam: DurationItem[];
  slaBreaches: number;
  text: string;
}

export interface DeveloperReport {
  requests: { requestId: string; reason: string | null; report: string }[];
  openIssues: Issue[];
  text: string;
}

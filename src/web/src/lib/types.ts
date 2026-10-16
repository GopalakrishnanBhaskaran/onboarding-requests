// Mirrors the API contracts in src/core. Server is the authority for every value here.

export type AppRole = 'PMO' | 'Reviewer' | 'Maintainer' | 'Operator';
export type ProcessingStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Superseded';
export type Readiness = 'NeedsInformation' | 'ReadyForHumanReview' | 'ManualReviewRequired';
export type Severity = 'Info' | 'Warning' | 'Blocking';
export type MappingStatus = 'Matched' | 'NotFound' | 'Ambiguous' | 'NotApplicable';
export type PublicationStatus = 'Draft' | 'Published' | 'Retired';
export type DecisionOutcome = 'Accepted' | 'Corrected' | 'Escalated';

export interface Item<T> {
  value: T;
  etag: string;
}

export interface ProjectSummary {
  projectId: string;
  name: string;
  roles: AppRole[];
  isSample: boolean;
  supportedRequestTypes: string[];
  supportedEnvironments: string[];
}

export interface Me {
  userId: string;
  displayName: string;
  projects: ProjectSummary[];
}

export interface RequestFields {
  requestType?: string | null;
  role?: string | null;
  environment?: string | null;
  manager?: string | null;
  justification?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  joinerUpn?: string | null;
}

export interface AccessRequest extends RequestFields {
  id: string;
  projectId: string;
  createdBy: string;
  createdAt: string;
  updatedBy: string;
  updatedAt: string;
  revision: number;
}

export interface Finding {
  code: string;
  field: string | null;
  severity: Severity;
  message: string;
  ruleId: string | null;
  ruleVersion: number | null;
  sourceReference: string | null;
}

export interface ReviewResult {
  schemaVersion: string;
  reviewRunId: string;
  requestRevision: number;
  readiness: Readiness;
  summary: string;
  findings: Finding[];
  mappingAssessment: {
    status: MappingStatus;
    mappingId: string | null;
    mappingVersion: number | null;
    explanation: string;
  };
  clarificationQuestions: string[];
  humanReviewRequired: true;
  limitations: string[];
}

export interface PinnedVersion {
  id: string;
  version: number;
}

export interface ReviewRun {
  id: string;
  projectId: string;
  requestId: string;
  requestRevision: number;
  snapshotHash: string;
  initiatedBy: string;
  processingStatus: ProcessingStatus;
  readiness: Readiness | null;
  ruleVersions: PinnedVersion[];
  mappingVersions: PinnedVersion[];
  reviewerProvider: string;
  reviewerVersion: string;
  promptVersion: string | null;
  result: ReviewResult | null;
  queuedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  errorCategory: string | null;
  retryOfRunId: string | null;
  supersededByRevision: number | null;
}

export interface AnalyzeResponse {
  runId: string;
  processingStatus: ProcessingStatus;
  duplicate: boolean;
}

export interface ReviewDecision {
  id: string;
  reviewRunId: string;
  reviewer: string;
  outcome: DecisionOutcome;
  comment: string | null;
  timestamp: string;
}

export interface AttentionItem {
  projectId: string;
  runId: string;
  requestId: string;
  requestRevision: number;
  processingStatus: ProcessingStatus;
  readiness: Readiness | null;
  errorCategory: string | null;
  queuedAt: string;
}

export interface RequirementRule {
  id: string;
  ruleId: string;
  version: number;
  requestType: string;
  field: string;
  rule: string;
  parameter: string | null;
  requirementText: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  publicationStatus: PublicationStatus;
  sourceReference: string;
  isSample: boolean;
}

export interface RoleMapping {
  id: string;
  mappingId: string;
  version: number;
  businessRole: string;
  environment: string;
  groupReference: string;
  accessPackageId?: string | null;
  myAccessLink?: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  publicationStatus: PublicationStatus;
  sourceReference: string;
  isSample: boolean;
}

export type HandoffStatus =
  | 'NotEligible' | 'AwaitingJoinerRequest' | 'PendingApproval' | 'Delivering' | 'Delivered'
  | 'Denied' | 'Cancelled' | 'Failed' | 'Expired';

/** Where an accepted review stands in Microsoft Entra. Entra approves and assigns; this app only reports. */
export interface EntraHandoff {
  id: string;
  requestId: string;
  requestRevision: number;
  reviewRunId: string;
  acceptedBy: string;
  status: HandoffStatus;
  reason: string | null;
  joinerUpn: string | null;
  mappingId: string | null;
  mappingVersion: number | null;
  accessPackageId: string | null;
  myAccessLink: string | null;
  requestedStart: string | null;
  requestedEnd: string | null;
  entraRequestId: string | null;
  entraRequestState: string | null;
  entraRequestType: string | null;
  entraAssignmentState: string | null;
  accessStart: string | null;
  accessEnd: string | null;
  endDateExceedsRequest: boolean;
  preExistingAssignment: boolean;
  createdAt: string;
  lastCheckedAt: string | null;
  lastSyncError: string | null;
  history: { at: string; status: HandoffStatus; note: string }[];
}

// Pure view logic for the request tracker, the request journey and the review check digest.
// The server stays the authority for every status; this only arranges what it sent for the screen.
import type { CheckResult, CheckStatus, RequestStatus, StatusLine } from './types';
import { FLOW_OVERVIEW } from './flow';

// ---- Journey (the 7 steps of the flow) --------------------------------------------------------------------------------

export type StepState = 'done' | 'current' | 'upcoming' | 'returned' | 'stopped' | 'failed';

export interface JourneyStep {
  key: string;
  /** Short label under the step marker. */
  label: string;
  /** The full wording of the step (same text the request form states upfront). */
  detail: string;
  state: StepState;
}

/** Short labels for the 7 steps of FLOW_OVERVIEW, in the same order. */
export const STEP_LABELS = ['Submitted', 'AI review', 'Owner', 'Security', 'ID team', 'Role team', 'Verified'] as const;
const STEP_KEYS = ['submit', 'review', 'owner', 'security', 'idteam', 'roleteam', 'verify'] as const;

/** The step a request in this status is at (0-based); 7 = every step done. */
const AT: Partial<Record<RequestStatus, number>> = {
  Submitted: 1,
  AiReview: 1,
  Returned: 1,
  AwaitingOwner: 2,
  AwaitingSecurity: 3,
  AwaitingProdApproval: 3,
  WithIdTeam: 4,
  WithRoleTeam: 5,
  Verifying: 6,
  Completed: 7,
  Closed: 7,
};

/** Where the request is in the flow: done steps, the current one (and how it stands), the steps still to come. */
export function journey(status: RequestStatus, done: string[]): JourneyStep[] {
  // A stopped request (Rejected, Failed-Needs-Dev) stops at the first step it did not get past.
  const at = AT[status] ?? Math.min(Math.max(done.length, 1), STEP_LABELS.length - 1);
  const here: StepState = status === 'Returned' ? 'returned'
    : status === 'Rejected' ? 'stopped'
      : status === 'FailedNeedsDev' ? 'failed'
        : 'current';
  return STEP_LABELS.map((label, i) => ({
    key: STEP_KEYS[i],
    label,
    detail: FLOW_OVERVIEW[i] ?? label,
    state: i < at ? 'done' : i === at ? here : 'upcoming',
  }));
}

/** Plain-words summary of the journey for assistive technology, e.g. "Step 3 of 7, Owner: in progress. Done: Submitted, AI review." */
export function journeyText(steps: JourneyStep[]): string {
  const done = steps.filter((s) => s.state === 'done').map((s) => s.label);
  const i = steps.findIndex((s) => s.state !== 'done' && s.state !== 'upcoming');
  if (i < 0) return done.length === steps.length ? 'All 7 steps done.' : `Done: ${done.join(', ') || 'nothing yet'}.`;
  const word: Record<StepState, string> = {
    current: 'in progress', returned: 'returned to the requester', stopped: 'stopped', failed: 'needs a developer', done: 'done', upcoming: 'to come',
  };
  return `Step ${i + 1} of ${steps.length}, ${steps[i].label}: ${word[steps[i].state]}. Done: ${done.join(', ') || 'nothing yet'}.`;
}

/** The stage names (as the server reports time per stage) that belong to each journey step. */
const STEP_STAGES: string[][] = [
  ['Submitted'], ['AI Review', 'Returned'], ['Awaiting Owner'], ['Awaiting Security', 'Awaiting Prod approval'],
  ['With ID Team'], ['With Role Team'], ['Verifying'],
];

/** Seconds spent in each journey step, from the server's time per stage (null where nothing was spent yet). */
export function stepSeconds(perStage: { name: string; seconds: number }[]): (number | null)[] {
  return STEP_STAGES.map((names) => {
    const hit = perStage.filter((t) => names.some((n) => n.toLowerCase() === t.name.toLowerCase()));
    return hit.length ? hit.reduce((sum, t) => sum + t.seconds, 0) : null;
  });
}

/** Same format as the server's durations: 45m, 3h 5m, 2d 4h. */
export function duration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d >= 1) return `${d}d ${h}h`;
  if (h >= 1) return `${h}h ${m}m`;
  return `${m}m`;
}

// ---- Tracker (the request list) ----------------------------------------------------------------------------------------

/** Default reminder time of the human stages (Notifier: Owner, Security, ID team, role team, ProdApprover: 24 h). */
export const LONG_WAIT_SECONDS = 24 * 3600;

export type Bucket = 'all' | 'active' | 'attention' | 'completed' | 'closed';

const TERMINAL: RequestStatus[] = ['Completed', 'Rejected', 'Closed'];
const ATTENTION: RequestStatus[] = ['Returned', 'FailedNeedsDev'];

/** Waiting longer than the default reminder time in a stage a person (not the agent) has to act on. */
export function waitingLong(r: Pick<StatusLine, 'status' | 'waitingOn' | 'timeInStageSeconds'>): boolean {
  return !TERMINAL.includes(r.status) && r.waitingOn !== 'Agent' && r.waitingOn !== 'None' && r.timeInStageSeconds >= LONG_WAIT_SECONDS;
}

export function inBucket(r: StatusLine, bucket: Bucket): boolean {
  switch (bucket) {
    case 'active': return !TERMINAL.includes(r.status);
    case 'attention': return ATTENTION.includes(r.status) || waitingLong(r);
    case 'completed': return r.status === 'Completed';
    case 'closed': return r.status === 'Rejected' || r.status === 'Closed';
    default: return true;
  }
}

/** The lanes of the pipeline overview: requests in progress, by the step they wait in. */
export const LANES: { key: string; label: string; statuses: RequestStatus[] }[] = [
  { key: 'review', label: 'AI review', statuses: ['Submitted', 'AiReview'] },
  { key: 'returned', label: 'Returned', statuses: ['Returned'] },
  { key: 'owner', label: 'Owner', statuses: ['AwaitingOwner'] },
  { key: 'security', label: 'Security', statuses: ['AwaitingSecurity', 'AwaitingProdApproval'] },
  { key: 'idteam', label: 'ID team', statuses: ['WithIdTeam'] },
  { key: 'roleteam', label: 'Role team', statuses: ['WithRoleTeam'] },
  { key: 'verify', label: 'Verifying', statuses: ['Verifying'] },
];

export function laneOf(status: RequestStatus): string | null {
  return LANES.find((l) => l.statuses.includes(status))?.key ?? null;
}

/** Case-insensitive search over what a person would type: request id, joiner, requester, resource, level, stage, waiting on. */
export function matches(r: StatusLine, query: string): boolean {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return true;
  const hay = [r.requestId, r.domainId, r.requester, r.resource, r.accessLevel, r.currentStage, r.waitingOnLabel, r.next]
    .filter(Boolean).join(' ').toLowerCase();
  return words.every((w) => hay.includes(w));
}

export interface TrackerFilter {
  bucket: Bucket;
  lane: string | null;
  query: string;
  longestFirst: boolean;
}

export function applyFilter(items: StatusLine[], f: TrackerFilter): StatusLine[] {
  const out = items.filter((r) => inBucket(r, f.bucket) && (!f.lane || laneOf(r.status) === f.lane) && matches(r, f.query));
  return f.longestFirst ? [...out].sort((a, b) => b.timeInStageSeconds - a.timeInStageSeconds) : out;
}

export function countBy<K extends string>(items: StatusLine[], keys: readonly K[], test: (r: StatusLine, k: K) => boolean): Record<K, number> {
  return Object.fromEntries(keys.map((k) => [k, items.filter((r) => test(r, k)).length])) as Record<K, number>;
}

// ---- Review check digest -------------------------------------------------------------------------------------------------

/** The order a reader needs: what failed, what is flagged, what is still open, what was not checked, what passed. */
export const CHECK_ORDER: CheckStatus[] = ['Fail', 'Flagged', 'Pending', 'NotApplicable', 'Pass'];

export interface CheckDigest {
  total: number;
  counts: Record<CheckStatus, number>;
  /** Every check that did not pass, most serious first (the server's order within one status). */
  attention: CheckResult[];
}

export function digest(checks: CheckResult[]): CheckDigest {
  const counts = Object.fromEntries(CHECK_ORDER.map((s) => [s, 0])) as Record<CheckStatus, number>;
  for (const c of checks) counts[c.status] = (counts[c.status] ?? 0) + 1;
  const attention = CHECK_ORDER.filter((s) => s !== 'Pass').flatMap((s) => checks.filter((c) => c.status === s));
  return { total: checks.length, counts, attention };
}

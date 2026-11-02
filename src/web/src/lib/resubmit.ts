import type { IntakeFields, MappingRow, StatusLine } from './types';

/**
 * A request submitted with "All" or several resources is one request per group (F1 bundle). When the AI review returns
 * them for the same reason, the requester fixes one and applies the same correction to the others.
 */
export function returnedSiblings(lines: StatusLine[], current: { id: string; bundleId?: string | null }): StatusLine[] {
  if (!current.bundleId) return [];
  return lines
    .filter((l) => l.bundleId === current.bundleId && l.requestId !== current.id && l.status === 'Returned')
    .sort((a, b) => (a.resource ?? '').localeCompare(b.resource ?? '') || (a.accessLevel ?? '').localeCompare(b.accessLevel ?? ''));
}

/** The shared correction for a sibling: joiner, justification and Owner from the form; its own resource, level and environment. */
export function correctionFor(sibling: IntakeFields, shared: IntakeFields): IntakeFields {
  return { ...shared, resource: sibling.resource ?? null, accessLevel: sibling.accessLevel ?? null, environment: sibling.environment ?? null };
}

/** Same words as the API's privileged-row rule (ReviewChecks: AdminReason or WriteWork). */
const TASK_WORDS = new RegExp(
  '\\b(admin\\w*|manag\\w*|configur\\w*|maintain\\w*|operat\\w*|support\\w*|deploy\\w*|incident\\w*|own\\w*|govern\\w*|provision\\w*'
  + '|build\\w*|develop\\w*|writ\\w*|edit\\w*|chang\\w*|load\\w*|ingest\\w*|creat\\w*|updat\\w*|run\\w*|pipeline\\w*|transform\\w*|fix\\w*|test\\w*)\\b',
);
/** Same words as the API's admin levels (ReviewPolicy.PrivilegedWords): such a level needs an administrative task instead. */
const ADMIN_LEVEL = /\b(owner|admin|administrator|contributor|global|privileged|elevated|manage)\b|full control|user access administrator/i;

/** The chosen rows that are flagged Privileged under a job-role level (e.g. Data Engineer): their justification must name tasks. */
export function privilegedJobRows(rows: MappingRow[]): MappingRow[] {
  return rows.filter((r) => r.privileged && !ADMIN_LEVEL.test(r.accessLevel));
}

/** True when the justification names no task, so the AI review would return a privileged job-role request. */
export function missingTasks(justification: string): boolean {
  return !TASK_WORDS.test(justification.toLowerCase());
}

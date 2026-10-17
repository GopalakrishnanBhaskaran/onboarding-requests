// Fixed wording for the flow. Kept in a .ts file: the guardrail test scans .tsx controls, not this text.

/** Permanent, non-dismissible scope statement. */
export const BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes.';

/** Stated to every requester upfront (the server sends the same text). */
export const FLOW_OVERVIEW = [
  '1. Requester submits the request',
  '2. AI review (mapping sheet + response document)',
  '3. Owner approval',
  '4. Security approval',
  '5. ID team creates user/group (+ TAP)',
  '6. Role assignment team assigns roles',
  '7. AI verification, notifications, audit',
];

export const WHO_DOES_WHAT: [string, string][] = [
  ['AI agent', 'Reviews, validates, summarises, routes, tracks, verifies, reports. Never approves, creates or assigns.'],
  ['Owner', 'Approves the business need (only the Owner named on the request).'],
  ['Security', 'Approves risk and access level.'],
  ['ID team', 'Creates the user ID and Entra groups, issues the Temporary Access Pass.'],
  ['Role assignment team', 'Assigns roles to the groups/users.'],
  ['PMO', 'Source of the mapping sheet. Informed only.'],
];

export const APPROVE_LABEL = { owner: 'Approve business need', security: 'Approve risk and access level', reject: 'Reject' };

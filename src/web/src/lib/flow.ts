// Fixed wording for the flow. Kept in a .ts file: the guardrail test scans .tsx controls, not this text.

/** Permanent, non-dismissible scope statement. */
export const BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes.';

/** v2.1 agent provisioning switched on (approved): the same statement, true to the deployment. */
export const AGENT_BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves: only the named Owner and Security approve. Only after both approvals it creates the user, group and membership and assigns the approved role where allowed; the ID team issues the Temporary Access Pass.';

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

/** Who does what, true to the deployment (v2.1 agent provisioning on or off). */
export function whoDoesWhat(agent: boolean): [string, string][] {
  if (!agent) return WHO_DOES_WHAT;
  return WHO_DOES_WHAT.map(([who, what]): [string, string] => {
    if (who === 'AI agent') return [who, 'Reviews, validates, summarises, routes, tracks, verifies, reports. Never approves. Only after both approvals: creates the user ID, group and membership and assigns the approved role where the mapping sheet allows it.'];
    if (who === 'ID team') return [who, 'Issues the Temporary Access Pass; does the user/group work the agent leaves to it (guests, privileged access, exceptions).'];
    if (who === 'Role assignment team') return [who, 'Assigns the roles the agent cannot (e.g. Databricks or privileged roles).'];
    return [who, what];
  });
}

export const APPROVE_LABEL = { owner: 'Approve business need', security: 'Approve risk and access level', reject: 'Reject' };

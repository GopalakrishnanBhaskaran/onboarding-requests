// Fixed wording for the flow. Kept in a .ts file: the guardrail test scans .tsx controls, not this text.

/** Permanent, non-dismissible scope statement. */
export const BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns: only the named Owner and Security approve; the ID team and the role assignment team make the changes.';

/** v2.1 agent provisioning switched on (approved): the same statement, true to the deployment. */
export const AGENT_BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves: only the named Owner and Security approve. Only after both approvals it creates the user, group and membership and assigns the approved role where allowed; the ID team issues the Temporary Access Pass.';

/** Agent TAP switched on (approved) as well: the agent mails a new account's sign-in details itself. */
export const AGENT_TAP_BANNER =
  'The AI agent reviews, routes, verifies and reports. It never approves: only the named Owner and Security approve. Only after both approvals it creates the user, group and membership, assigns the approved role where allowed, and mails a new joiner a one-time Temporary Access Pass with the MFA set-up steps to the Enterprise ID on the request.';

/** Stated to every requester upfront (the server sends the same text). */
export const FLOW_OVERVIEW = [
  '1. Requester submits the request',
  '2. AI review (mapping sheet + response document)',
  '3. Owner approval',
  '4. Security approval (Prod above Read: then a ProdApprover)',
  '5. ID team creates user/group (+ TAP)',
  '6. Role assignment team assigns roles',
  '7. AI verifies the access, notifies the requester (Completed), audit',
];

export const WHO_DOES_WHAT: [string, string][] = [
  ['AI agent', 'Reviews, validates, summarises, routes, tracks, verifies, reports. Never approves, creates or assigns.'],
  ['Owner', 'Approves the business need (only the Owner named on the request).'],
  ['Security', 'Approves risk and access level.'],
  ['ID team', 'Creates the user ID and Entra groups, issues the Temporary Access Pass.'],
  ['Role assignment team', 'Assigns roles to the groups/users.'],
  ['PMO', 'Source of the mapping sheet. Informed only.'],
];

/** Who does what, true to the deployment (v2.1 agent provisioning on or off; agent TAP on or off; privileged rows on or off). */
export function whoDoesWhat(agent: boolean, tap = false, privileged = false): [string, string][] {
  if (!agent) return WHO_DOES_WHAT;
  const left = privileged ? 'guests, privileged administrator roles, exceptions' : 'guests, privileged access, exceptions';
  return WHO_DOES_WHAT.map(([who, what]): [string, string] => {
    if (who === 'AI agent') return [who, 'Reviews, validates, summarises, routes, tracks, verifies, reports. Never approves. Only after both approvals: creates the user ID, group and membership and assigns the approved role where the mapping sheet allows it' +
      (privileged ? ' (privileged rows too, never Owner, Contributor, User Access Administrator or the like)' : '') +
      (tap ? '; mails a new joiner a one-time Temporary Access Pass, the initial password and the MFA set-up steps (once, to the Enterprise ID mail on the request), with the instruction to change the password right after signing in.' : '.')];
    if (who === 'ID team') return [who, tap
      ? `Does the user/group work and Temporary Access Pass the agent leaves to it (${left}).`
      : `Issues the Temporary Access Pass; does the user/group work the agent leaves to it (${left}).`];
    if (who === 'Role assignment team') return [who, privileged
      ? 'Assigns the roles the agent cannot (e.g. Databricks roles, privileged administrator roles, roles off its allow-list).'
      : 'Assigns the roles the agent cannot (e.g. Databricks or privileged roles).'];
    return [who, what];
  });
}

export const APPROVE_LABEL = { owner: 'Approve business need', security: 'Approve risk and access level', reject: 'Reject' };

/**
 * Justification template for the request form: what the reviewers look for, with the request's own choices filled in.
 * Every [ … ] must be replaced before submitting (the API refuses an unfilled template).
 */
export function justificationTemplate(accessLevel: string, resource: string, environment: string): string {
  const access = `${accessLevel || '[access level]'} on ${resource || '[resource]'}${environment ? ` (${environment})` : ''}`;
  return [
    'Role: [job title] in the [team name] team.',
    `Access needed: ${access}.`,
    'Why: to [the tasks that need this access, e.g. investigate support tickets by reading pipeline logs and storage data].',
    'Start: [dd-mm-yyyy]. Duration: [permanent while in the team / until dd-mm-yyyy].',
    'Reference: [ticket or change number]. Manager: [manager name].',
  ].join('\n');
}

/** A [ … ] placeholder of the template is still in the text (same rule as the API). */
export const TEMPLATE_PLACEHOLDER = /\[[a-z][a-z0-9 ,./()-]{1,160}\]/;

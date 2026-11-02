import { useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Checkbox, Field, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea,
  makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { PersonAvailable20Regular, ShieldCheckmark20Regular } from '@fluentui/react-icons';
import { api, ApiError } from '../lib/api';
import { APPROVE_LABEL } from '../lib/flow';
import { dateTime, grantsOf, scopeName } from '../lib/format';
import type { AccessRequest, Escalation, ProjectSummary, RequestView } from '../lib/types';
import { ErrorBar, useStyles } from './common';

const useDecision = makeStyles({
  /** The one card on the page that asks this person to act: a brand accent and a little more room. */
  panel: { borderTop: `4px solid ${tokens.colorBrandBackground}`, boxShadow: tokens.shadow8, gap: '12px' },
  head: { display: 'flex', alignItems: 'center', gap: '12px' },
  icon: {
    width: '36px', height: '36px', borderRadius: tokens.borderRadiusLarge, display: 'grid', placeItems: 'center', flexShrink: 0,
    backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1,
  },
  eyebrow: {
    color: tokens.colorBrandForeground1, fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em', textTransform: 'uppercase',
  },
  actions: { display: 'flex', flexWrap: 'wrap', gap: '8px', alignItems: 'center', paddingTop: '8px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
});

/**
 * Stage 3, the only place with decision controls. Rendered only when the server says this caller may decide now:
 * the named Owner at the Owner stage, a Security member (not the Owner approver) at the Security stage.
 * It shows exactly what is being decided (who, tenant, environment, group, every Azure role, risk flags, the AI
 * review) above the decision. The decision is tied to the request id and to the review the approver is looking at.
 */
export function ApprovalPanel({ request, stage, project, tap = false, onDone, onStale }: {
  request: AccessRequest;
  stage: 'owner' | 'security';
  project: ProjectSummary;
  /** The AI agent mails a new account's sign-in details to the Enterprise ID on the request (agent TAP on). */
  tap?: boolean;
  onDone: (v: RequestView) => void;
  /** The review or the mapping sheet changed meanwhile: reload before deciding. */
  onStale: () => void;
}) {
  const s = useStyles();
  const d = useDecision();
  const [remarks, setRemarks] = useState('');
  const [read, setRead] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const scope = request.flow?.scope ?? null;
  const runId = scope?.reviewRunId ?? '';
  const grants = grantsOf(scope);
  const owner = request.flow?.approvals.filter((a) => a.stage === 'Owner' && !a.superseded).at(-1);
  const flags = scope?.riskFlags ?? [];

  async function decide(decision: 'Approved' | 'Rejected') {
    setBusy(true);
    setError(null);
    try {
      onDone(await api.respond(request.projectId, request.id, decision, remarks.trim(), runId));
    } catch (e) {
      setError(e);
      if (e instanceof ApiError && e.status === 409) onStale();
    } finally {
      setBusy(false);
    }
  }

  const row = (label: string, value: React.ReactNode) => (
    <div style={{ display: 'contents' }}>
      <dt><Caption1>{label}</Caption1></dt>
      <dd style={{ margin: 0 }}><Body1>{value}</Body1></dd>
    </div>
  );

  return (
    <section className={mergeClasses(s.section, d.panel)} aria-labelledby="decision-h">
      <div className={d.head}>
        <span className={d.icon} aria-hidden="true">{stage === 'owner' ? <PersonAvailable20Regular /> : <ShieldCheckmark20Regular />}</span>
        <div>
          <Caption1 block className={d.eyebrow}>Waiting on you</Caption1>
          <Subtitle2 as="h2" id="decision-h">{stage === 'owner' ? 'Your decision as the named Owner' : 'Your decision as Security'}</Subtitle2>
        </div>
      </div>
      <Body1>
        {stage === 'owner'
          ? 'Decide on the business need. Read what is requested below and the AI review summary.'
          : 'Decide on the risk and access level. Read what is requested below, the risk flags and the Owner decision.'}
      </Body1>

      <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(140px, max-content) 1fr', gap: '4px 16px', margin: 0 }}>
        {row('User', `${request.firstName ?? ''} ${request.lastName ?? ''}${request.guest ? ' (guest)' : ''}`)}
        {row('Enterprise ID', request.enterpriseId || '—')}
        {tap && !request.guest && request.enterpriseId
          ? row('Sign-in details go to', `${request.enterpriseId} — for a new account the AI agent mails the one-time pass and the initial password here. Reject if this is not the joiner’s own enterprise mailbox.`)
          : null}
        {row('Sign-in name to create', request.domainId || '—')}
        {row('Tenant', project.tenantName || project.name)}
        {request.environment ? row('Environment', request.environment) : null}
        {row('Resource / access level', `${request.accessLevel ?? '—'} on ${request.resource ?? '—'}`)}
        {row('Entra group', scope ? `${scope.entraGroup}${scope.groupState === 'ToBeCreated' ? ' (to be created)' : ''}` : '—')}
        {row('Role', scope ? `${scope.role}${scope.roleScope ? ` at ${scope.roleScope}` : ''}` : '—')}
        {row('Justification', <span className={s.pre}>{request.justification || '—'}</span>)}
        {stage === 'security' && owner ? row('Owner decision', `${owner.decision} by ${owner.byName} · ${dateTime(owner.at)}${owner.remarks ? ` — ${owner.remarks}` : ''}`) : null}
      </dl>

      {grants.length > 0 && (
        <div className={s.tableWrap}>
          <Table aria-label="Azure roles in this decision" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Azure role on the group</TableHeaderCell>
                <TableHeaderCell>Resource</TableHeaderCell>
                <TableHeaderCell>Scope</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {grants.map((g, i) => (
                <TableRow key={`${g.roleName}-${g.scope ?? g.resourceName}-${i}`}>
                  <TableCell>{g.roleName}</TableCell>
                  <TableCell>{g.resourceName ?? scopeName(g.scope)}{g.resourceType ? <Caption1 block>{g.resourceType}</Caption1> : null}</TableCell>
                  <TableCell><span className={s.pre}>{g.scope ?? 'not resolved: the role assignment team assigns it'}</span></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <div className={s.row} aria-label="Risk flags">
        <Caption1>Risk flags:</Caption1>
        {flags.length === 0 ? <Badge color="success">none</Badge> : flags.map((f) => <Badge key={f} color="warning">{f}</Badge>)}
      </div>
      {scope?.summary && (
        <details open>
          <summary><Body1><b>AI review summary</b></Body1></summary>
          <Body1 className={s.pre}>{scope.summary}</Body1>
        </details>
      )}

      <ErrorBar error={error} />
      <Checkbox checked={read} onChange={(_, d) => setRead(d.checked === true)}
        label="I have read the request, the Azure roles and the AI review summary above." />
      <Field label="Remarks" hint="Required when rejecting. Never paste passwords or passes.">
        <Textarea value={remarks} onChange={(_, d) => setRemarks(d.value)} maxLength={1000} />
      </Field>
      <div className={d.actions}>
        <Button appearance="primary" size="large" disabled={busy || !runId || !read} onClick={() => void decide('Approved')}>
          {stage === 'owner' ? APPROVE_LABEL.owner : APPROVE_LABEL.security}
        </Button>
        <Button size="large" disabled={busy || remarks.trim() === ''} onClick={() => void decide('Rejected')}>{APPROVE_LABEL.reject}</Button>
        {!read && <Caption1 className={s.muted}>Tick the box above first.</Caption1>}
      </div>
      <Caption1 className={s.muted}>Decisions count only here, from the named Owner and then a different Security approver. Nothing is approved automatically.</Caption1>
    </section>
  );
}

/** v1.1 (F3) labels of the ProdApprover decision. */
export const PROD_LABEL = { approve: 'Approve production access above Read', reject: 'Reject' };

/**
 * F3: a ProdApprover decides a production request above Read, after Owner and Security. Shown only for an Open ProdWrite
 * escalation to a ProdApprover; the server refuses the requester and the earlier approvers, and a decision on a changed
 * request (another revision): the decision is tied to the revision shown here.
 */
export function ProdDecisionPanel({ escalation, projectId, onDone }: {
  escalation: Escalation;
  projectId: string;
  onDone: () => void;
}) {
  const s = useStyles();
  const [remarks, setRemarks] = useState('');
  const [read, setRead] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function decide(decision: 'Approved' | 'Rejected') {
    setBusy(true);
    setError(null);
    try {
      await api.decideEscalation(projectId, escalation.id, decision, remarks.trim(), escalation.requestRevision ?? 0);
      onDone();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className={s.section} aria-label={`Prod decision ${escalation.id}`} style={{ borderWidth: 2 }}>
      <Subtitle2 as="h3">Your decision as ProdApprover</Subtitle2>
      <Body1>
        Production is read-only by default. {escalation.reason}. Owner and Security approved; nothing is granted until you decide.
        Request {escalation.requestId?.slice(0, 8)}, revision {escalation.requestRevision}: user {escalation.user}, group {escalation.group},
        level {escalation.level}, environment {escalation.environment}.
      </Body1>
      <ErrorBar error={error} />
      <Checkbox checked={read} onChange={(_, d) => setRead(d.checked === true)} label="I have read the request and its Azure roles (open the request to see the AI review)." />
      <Field label="Remarks" hint="Required when rejecting. Never paste passwords or passes.">
        <Textarea value={remarks} onChange={(_, d) => setRemarks(d.value)} maxLength={1000} />
      </Field>
      <div className={s.row}>
        <Button appearance="primary" disabled={busy || !read} onClick={() => void decide('Approved')}>{PROD_LABEL.approve}</Button>
        <Button disabled={busy || remarks.trim() === ''} onClick={() => void decide('Rejected')}>{PROD_LABEL.reject}</Button>
      </div>
      <Caption1 className={s.muted}>The requester, the Owner approver and the Security approver of this request cannot decide it.</Caption1>
    </section>
  );
}

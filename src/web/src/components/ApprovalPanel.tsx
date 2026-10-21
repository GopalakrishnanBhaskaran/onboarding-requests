import { useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Checkbox, Field, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea,
} from '@fluentui/react-components';
import { api, ApiError } from '../lib/api';
import { APPROVE_LABEL } from '../lib/flow';
import { dateTime, grantsOf, scopeName } from '../lib/format';
import type { AccessRequest, ProjectSummary, RequestView } from '../lib/types';
import { ErrorBar, useStyles } from './common';

/**
 * Stage 3, the only place with decision controls. Rendered only when the server says this caller may decide now:
 * the named Owner at the Owner stage, a Security member (not the Owner approver) at the Security stage.
 * It shows exactly what is being decided (who, tenant, environment, group, every Azure role, risk flags, the AI
 * review) above the decision. The decision is tied to the request id and to the review the approver is looking at.
 */
export function ApprovalPanel({ request, stage, project, onDone, onStale }: {
  request: AccessRequest;
  stage: 'owner' | 'security';
  project: ProjectSummary;
  onDone: (v: RequestView) => void;
  /** The review or the mapping sheet changed meanwhile: reload before deciding. */
  onStale: () => void;
}) {
  const s = useStyles();
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
    <section className={s.section} aria-labelledby="decision-h" style={{ borderWidth: 2 }}>
      <Subtitle2 as="h2" id="decision-h">{stage === 'owner' ? 'Your decision as the named Owner' : 'Your decision as Security'}</Subtitle2>
      <Body1>
        {stage === 'owner'
          ? 'Decide on the business need. Read what is requested below and the AI review summary.'
          : 'Decide on the risk and access level. Read what is requested below, the risk flags and the Owner decision.'}
      </Body1>

      <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(140px, max-content) 1fr', gap: '4px 16px', margin: 0 }}>
        {row('User', `${request.firstName ?? ''} ${request.lastName ?? ''}${request.guest ? ' (guest)' : ''}`)}
        {row('Enterprise ID', request.enterpriseId || '—')}
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
      <div className={s.row}>
        <Button appearance="primary" disabled={busy || !runId || !read} onClick={() => void decide('Approved')}>
          {stage === 'owner' ? APPROVE_LABEL.owner : APPROVE_LABEL.security}
        </Button>
        <Button disabled={busy || remarks.trim() === ''} onClick={() => void decide('Rejected')}>{APPROVE_LABEL.reject}</Button>
      </div>
      <Caption1 className={s.muted}>Decisions count only here, from the named Owner and then a different Security approver. Nothing is approved automatically.</Caption1>
    </section>
  );
}

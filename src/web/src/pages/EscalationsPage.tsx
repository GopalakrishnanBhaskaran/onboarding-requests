import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Body1, Caption1, Link, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import { dateTime } from '../lib/format';
import { href } from '../lib/router';
import type { Escalation, Item, ProjectSummary } from '../lib/types';
import { ProdDecisionPanel } from '../components/ApprovalPanel';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const KIND: Record<Escalation['kind'], string> = {
  ProdWrite: 'Production above Read',
  UnresolvedGroup: 'Group not resolved in Entra',
  TapRecipientMismatch: 'TAP recipient not verified',
  OffboardingLeftover: 'Offboarding left something',
};

/**
 * v1.1 escalations: what the system will not do without a person (F1 unresolved groups, F2 TAP recipient, F3 Prod
 * writes, F5 offboarding leftovers). A ProdApprover decides the open Prod ones here; everyone else reads.
 */
export function EscalationsPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [items, setItems] = useState<Item<Escalation>[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canDecide = project.roles.includes('ProdApprover');

  const load = useCallback(() => {
    setError(null);
    api.escalations(project.projectId).then((r) => setItems(r.items)).catch(setError);
  }, [project.projectId]);
  useEffect(load, [load]);

  if (!items) return <div className={s.stack}><PageTitle>Escalations</PageTitle><ErrorBar error={error} />{!error && <Loading />}</div>;
  const open = items.filter((i) => i.value.status === 'Open');
  return (
    <div className={s.stack}>
      <PageTitle>Escalations</PageTitle>
      <Body1>Decisions the system leaves to people. Nothing here is decided automatically.</Body1>
      <ErrorBar error={error} />
      {canDecide && open.filter((i) => i.value.kind === 'ProdWrite').map((i) => (
        <ProdDecisionPanel key={i.value.id} escalation={i.value} projectId={project.projectId} onDone={load} />
      ))}
      <Subtitle2 as="h2">All escalations ({open.length} open)</Subtitle2>
      {items.length === 0 ? <Empty>No escalations.</Empty> : (
        <div className={s.tableWrap}>
          <Table aria-label="Escalations" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Kind</TableHeaderCell>
                <TableHeaderCell>What</TableHeaderCell>
                <TableHeaderCell>Raised</TableHeaderCell>
                <TableHeaderCell>Decided</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map(({ value: e }) => (
                <TableRow key={e.id}>
                  <TableCell><Badge color={e.status === 'Open' ? 'warning' : e.status === 'Approved' ? 'success' : 'subtle'}>{e.status}</Badge></TableCell>
                  <TableCell>{KIND[e.kind]}</TableCell>
                  <TableCell>
                    <span className={s.wrap}>{e.reason}</span>
                    <Caption1 block>
                      {[e.group, e.level, e.environment, e.user].filter(Boolean).join(' · ')}
                      {e.requestId && <> · <Link href={href({ page: 'request', requestId: e.requestId })}>request {e.requestId.slice(0, 8)}</Link></>}
                    </Caption1>
                  </TableCell>
                  <TableCell>{dateTime(e.createdAt)}<Caption1 block>{e.requestedBy}</Caption1></TableCell>
                  <TableCell>{e.decidedAt ? <>{dateTime(e.decidedAt)}<Caption1 block>{e.decidedByName ?? e.decidedBy}{e.remarks ? ` — ${e.remarks}` : ''}</Caption1></> : '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

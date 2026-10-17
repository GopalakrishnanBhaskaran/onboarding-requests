import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href } from '../lib/router';
import type { ActionItem, ActionName } from '../lib/types';
import { statusColor } from '../lib/format';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const actionLabel: Record<ActionName, string> = {
  ApproveOwner: 'Owner decision',
  ApproveSecurity: 'Security decision',
  IdTeamTask: 'ID team task',
  RoleTeamTask: 'Role assignment task',
  Resubmit: 'Correct and resubmit',
};

/** Everything waiting on the signed-in person, across their projects (computed by the server). */
export function QueuePage({ onOpen }: { onOpen: (projectId: string) => void }) {
  const s = useStyles();
  const [items, setItems] = useState<ActionItem[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(() => {
    setError(null);
    api.queue().then((r) => setItems(r.items)).catch(setError);
  }, []);
  useEffect(load, [load]);

  return (
    <div className={s.stack}>
      <PageTitle>My actions</PageTitle>
      <div><Button icon={<ArrowClockwise20Regular />} onClick={load}>Refresh</Button></div>
      <ErrorBar error={error} />
      {items === null && !error ? (
        <Loading />
      ) : items?.length === 0 ? (
        <Empty>Nothing is waiting on you.</Empty>
      ) : items ? (
        <div className={s.tableWrap}>
          <Table aria-label="My actions" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Action</TableHeaderCell>
                <TableHeaderCell>Request</TableHeaderCell>
                <TableHeaderCell>Stage</TableHeaderCell>
                <TableHeaderCell>Time in stage</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((i) => (
                <TableRow key={`${i.status.requestId}-${i.action}`}>
                  <TableCell>{actionLabel[i.action]}</TableCell>
                  <TableCell>
                    <Link href={href({ page: 'request', requestId: i.status.requestId })} onClick={() => onOpen(i.status.projectId)}>
                      {i.status.requestId.slice(0, 8)}
                    </Link>{' '}
                    {i.status.domainId} · {i.status.accessLevel} on {i.status.resource}
                  </TableCell>
                  <TableCell><Badge color={statusColor[i.status.status]}>{i.status.currentStage}</Badge></TableCell>
                  <TableCell>{i.status.timeInStage}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </div>
  );
}

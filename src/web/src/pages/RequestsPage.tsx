import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Caption1, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { Add20Regular, ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href, navigate } from '../lib/router';
import type { ProjectSummary, StatusLine } from '../lib/types';
import { statusColor } from '../lib/format';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

/** Status tracking (always visible): Request ID | Requester | Current stage | Waiting on | Done | Next | Time in stage | ADO link. */
export function RequestsPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [items, setItems] = useState<StatusLine[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canSubmit = project.roles.includes('Requester');

  const load = useCallback(async () => {
    setError(null);
    try {
      setItems((await api.listRequests(project.projectId)).items);
    } catch (e) {
      setError(e);
    }
  }, [project.projectId]);

  useEffect(() => {
    setItems(null);
    void load();
  }, [load]);

  return (
    <div className={s.stack}>
      <PageTitle>Requests</PageTitle>
      <div className={s.row}>
        {canSubmit && (
          <Button appearance="primary" icon={<Add20Regular />} onClick={() => navigate({ page: 'new' })}>
            New request
          </Button>
        )}
        <Button icon={<ArrowClockwise20Regular />} onClick={() => void load()}>Refresh</Button>
      </div>
      <ErrorBar error={error} />
      {items === null && !error ? (
        <Loading />
      ) : items && items.length === 0 ? (
        <Empty>No requests yet{canSubmit ? '. Select “New request” to start.' : '.'}</Empty>
      ) : items ? (
        <div className={s.tableWrap}>
          <Table aria-label="Request status" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Request ID</TableHeaderCell>
                <TableHeaderCell>Requester</TableHeaderCell>
                <TableHeaderCell>Current stage</TableHeaderCell>
                <TableHeaderCell>Waiting on</TableHeaderCell>
                <TableHeaderCell>Done</TableHeaderCell>
                <TableHeaderCell>Next</TableHeaderCell>
                <TableHeaderCell>Time in stage</TableHeaderCell>
                <TableHeaderCell>ADO</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((r) => (
                <TableRow key={r.requestId}>
                  <TableCell>
                    <Link href={href({ page: 'request', requestId: r.requestId })}>{r.requestId.slice(0, 8)}</Link>
                    <Caption1 block className={s.muted}>{r.domainId} · {r.accessLevel} on {r.resource}</Caption1>
                  </TableCell>
                  <TableCell>{r.requester}</TableCell>
                  <TableCell><Badge appearance="filled" color={statusColor[r.status]}>{r.currentStage}</Badge></TableCell>
                  <TableCell>{r.waitingOnLabel}</TableCell>
                  <TableCell><Caption1>{r.done.join(' · ') || '—'}</Caption1></TableCell>
                  <TableCell><Caption1>{r.next}</Caption1></TableCell>
                  <TableCell>{r.timeInStage}</TableCell>
                  <TableCell>{r.adoLink ? <Link href={r.adoLink} target="_blank" rel="noopener noreferrer">Work item</Link> : '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </div>
  );
}

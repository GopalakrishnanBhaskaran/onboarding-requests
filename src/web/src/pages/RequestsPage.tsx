import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Caption1, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { Add20Regular, ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href, navigate } from '../lib/router';
import type { ProjectSummary, StatusLine } from '../lib/types';
import { statusColor } from '../lib/format';
import { Breakable, Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

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
      {!canSubmit && (
        <Caption1>
          New requests need the Requester role. Your roles in this project: {project.roles.join(', ') || 'none'}. Ask the pilot owner to add Requester.
        </Caption1>
      )}
      <ErrorBar error={error} />
      {items === null && !error ? (
        <Loading />
      ) : items && items.length === 0 ? (
        <Empty>No requests yet{canSubmit ? '. Select “New request” to start.' : '.'}</Empty>
      ) : items ? (
        <div className={s.tableWrap}>
          <Table aria-label="Request status" size="small" className={s.fixedTable}>
            <TableHeader>
              <TableRow>
                {/* Widths add up to 100%; the stage column fits the longest badge ("Awaiting Security", "Failed-Needs-Dev"). */}
                <TableHeaderCell style={{ width: '20%' }}>Request ID</TableHeaderCell>
                <TableHeaderCell style={{ width: '17%' }}>Requester</TableHeaderCell>
                <TableHeaderCell style={{ width: '14%' }}>Current stage</TableHeaderCell>
                <TableHeaderCell style={{ width: '9%' }}>Waiting on</TableHeaderCell>
                <TableHeaderCell style={{ width: '16%' }}>Done</TableHeaderCell>
                <TableHeaderCell style={{ width: '12%' }}>Next</TableHeaderCell>
                <TableHeaderCell style={{ width: '6%' }}>Time in stage</TableHeaderCell>
                <TableHeaderCell style={{ width: '6%' }}>ADO</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((r) => (
                <TableRow key={r.requestId}>
                  <TableCell className={s.wrap}>
                    <Link href={href({ page: 'request', requestId: r.requestId })}>{r.requestId.slice(0, 8)}</Link>
                    <Caption1 block className={s.muted}>
                      {r.resource ? <><Breakable text={r.domainId} /> · {r.accessLevel} on {r.resource}</> : 'v1 request (read-only)'}
                    </Caption1>
                  </TableCell>
                  <TableCell className={s.wrap}><Breakable text={r.requester} /></TableCell>
                  <TableCell><Badge appearance="filled" color={statusColor[r.status]}>{r.currentStage}</Badge></TableCell>
                  <TableCell className={s.wrap}>{r.waitingOnLabel}</TableCell>
                  <TableCell className={s.wrap}><Caption1>{r.done.join(' · ') || '—'}</Caption1></TableCell>
                  <TableCell className={s.wrap}><Caption1>{r.next}</Caption1></TableCell>
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

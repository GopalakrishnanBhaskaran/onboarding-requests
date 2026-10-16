import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href } from '../lib/router';
import type { AttentionItem } from '../lib/types';
import { dateTime, readinessLabel } from '../lib/format';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

/** Reviewer queue: latest run per request that failed or needs manual review. */
export function QueuePage({ onOpen }: { onOpen: (projectId: string) => void }) {
  const s = useStyles();
  const [items, setItems] = useState<AttentionItem[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(() => {
    setError(null);
    api.attention().then((r) => setItems(r.items)).catch(setError);
  }, []);
  useEffect(load, [load]);

  return (
    <div className={s.stack}>
      <PageTitle>Failed and manual-review queue</PageTitle>
      <div><Button icon={<ArrowClockwise20Regular />} onClick={load}>Refresh</Button></div>
      <ErrorBar error={error} />
      {items === null && !error ? (
        <Loading />
      ) : items?.length === 0 ? (
        <Empty>Nothing needs attention.</Empty>
      ) : items ? (
        <div className={s.tableWrap}>
          <Table aria-label="Attention queue" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Project</TableHeaderCell>
                <TableHeaderCell>Request</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Queued</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((i) => (
                <TableRow key={i.runId}>
                  <TableCell>{i.projectId}</TableCell>
                  <TableCell>
                    <Link href={href({ page: 'request', requestId: i.requestId })} onClick={() => onOpen(i.projectId)}>
                      {i.requestId.slice(0, 8)} · rev {i.requestRevision}
                    </Link>
                  </TableCell>
                  <TableCell>
                    {i.processingStatus === 'Failed'
                      ? <Badge color="danger">Failed ({i.errorCategory})</Badge>
                      : <Badge color="danger">{i.readiness ? readinessLabel[i.readiness] : i.processingStatus}</Badge>}
                  </TableCell>
                  <TableCell>{dateTime(i.queuedAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </div>
  );
}

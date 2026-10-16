import { useCallback, useEffect, useState } from 'react';
import {
  Button, Link, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { Add20Regular, ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href, navigate } from '../lib/router';
import type { AccessRequest, Item, ProjectSummary } from '../lib/types';
import { dateTime } from '../lib/format';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

export function RequestsPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [items, setItems] = useState<Item<AccessRequest>[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canCreate = project.roles.includes('PMO');
  const canView = project.roles.includes('PMO') || project.roles.includes('Reviewer');

  const load = useCallback(async () => {
    if (!canView) return;
    setError(null);
    try {
      setItems((await api.listRequests(project.projectId)).items);
    } catch (e) {
      setError(e);
    }
  }, [project.projectId, canView]);

  useEffect(() => {
    setItems(null);
    void load();
  }, [load]);

  return (
    <div className={s.stack}>
      <PageTitle>My requests</PageTitle>
      <div className={s.row}>
        {canCreate && (
          <Button appearance="primary" icon={<Add20Regular />} onClick={() => navigate({ page: 'new' })}>
            New request
          </Button>
        )}
        {canView && (
          <Button icon={<ArrowClockwise20Regular />} onClick={() => void load()}>
            Refresh
          </Button>
        )}
      </div>
      <ErrorBar error={error} />
      {!canView ? (
        <Empty>Your role in this project does not include viewing requests.</Empty>
      ) : items === null && !error ? (
        <Loading />
      ) : items && items.length === 0 ? (
        <Empty>No requests yet{canCreate ? '. Select “New request” to start.' : '.'}</Empty>
      ) : items ? (
        <div className={s.tableWrap}>
          <Table aria-label="Requests" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Request</TableHeaderCell>
                <TableHeaderCell>Type</TableHeaderCell>
                <TableHeaderCell>Business role</TableHeaderCell>
                <TableHeaderCell>Environment</TableHeaderCell>
                <TableHeaderCell>Start</TableHeaderCell>
                <TableHeaderCell>Revision</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map(({ value: r }) => (
                <TableRow key={r.id}>
                  <TableCell>
                    <Link href={href({ page: 'request', requestId: r.id })}>{r.id.slice(0, 8)}</Link>
                  </TableCell>
                  <TableCell>{r.requestType ?? '—'}</TableCell>
                  <TableCell>{r.role ?? '—'}</TableCell>
                  <TableCell>{r.environment ?? '—'}</TableCell>
                  <TableCell>{r.startDate ?? '—'}</TableCell>
                  <TableCell>{r.revision}</TableCell>
                  <TableCell>{dateTime(r.updatedAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : null}
    </div>
  );
}

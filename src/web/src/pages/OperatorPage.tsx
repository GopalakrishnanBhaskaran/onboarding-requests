import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Field, Input, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import type { DailySummary, DeveloperReport, Issue, ProjectSummary } from '../lib/types';
import { dateTime } from '../lib/format';
import { Empty, ErrorBar, PageTitle, useStyles } from '../components/common';

/** Issues list, daily summary (with wait time per team), developer issue report, stuck-review recovery. */
export function OperatorPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const canIssues = project.roles.includes('Operator') || project.roles.includes('Security');
  const isOperator = project.roles.includes('Operator');
  const [issues, setIssues] = useState<Issue[] | null>(null);
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [daily, setDaily] = useState<DailySummary | null>(null);
  const [dev, setDev] = useState<DeveloperReport | null>(null);
  const [recovered, setRecovered] = useState<string[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      if (canIssues) setIssues((await api.issues(project.projectId)).items);
    } catch (e) {
      setError(e);
    }
  }, [project.projectId, canIssues]);
  useEffect(() => void load(), [load]);

  async function run<T>(action: () => Promise<T>, set: (v: T) => void) {
    setError(null);
    try {
      set(await action());
    } catch (e) {
      setError(e);
    }
  }

  return (
    <div className={s.stack}>
      <PageTitle>Issues and reports</PageTitle>
      <ErrorBar error={error} />

      {canIssues && (
        <section className={s.section} aria-labelledby="issues-h">
          <Subtitle2 as="h2" id="issues-h">Issues list</Subtitle2>
          {issues?.length === 0 ? <Empty>No issues.</Empty> : (
            <div className={s.tableWrap}>
              <Table aria-label="Issues" size="small">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Kind</TableHeaderCell>
                    <TableHeaderCell>Request</TableHeaderCell>
                    <TableHeaderCell>Summary</TableHeaderCell>
                    <TableHeaderCell>Status</TableHeaderCell>
                    <TableHeaderCell>Last seen</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {issues?.map((i) => (
                    <TableRow key={i.id}>
                      <TableCell><Badge color={i.severity === 'High' ? 'danger' : i.severity === 'Warning' ? 'warning' : 'informative'}>{i.kind}</Badge></TableCell>
                      <TableCell>{i.requestId?.slice(0, 8) ?? '—'}</TableCell>
                      <TableCell><span className={s.pre}>{i.summary}</span>{i.detail && <Caption1 block className={s.pre}>{i.detail}</Caption1>}</TableCell>
                      <TableCell>{i.status} ×{i.occurrences}</TableCell>
                      <TableCell>{dateTime(i.lastSeenAt)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </section>
      )}

      <section className={s.section} aria-labelledby="daily-h">
        <Subtitle2 as="h2" id="daily-h">Daily summary</Subtitle2>
        <div className={s.row}>
          <Field label="Day (UTC)"><Input type="date" value={date} onChange={(_, d) => setDate(d.value)} /></Field>
          <Button onClick={() => void run(() => api.daily(project.projectId, date), setDaily)}>Show</Button>
        </div>
        {daily && <pre className={s.pre} style={{ margin: 0, fontSize: 12 }}>{daily.text}</pre>}
      </section>

      <section className={s.section} aria-labelledby="dev-h">
        <Subtitle2 as="h2" id="dev-h">Developer issue report</Subtitle2>
        <div><Button onClick={() => void run(() => api.developer(project.projectId), setDev)}>Build report</Button></div>
        {dev && <pre className={s.pre} style={{ margin: 0, fontSize: 12 }}>{dev.text}</pre>}
      </section>

      {isOperator && (
        <section className={s.section} aria-labelledby="stuck-h">
          <Subtitle2 as="h2" id="stuck-h">Stuck reviews</Subtitle2>
          <Body1>Marks reviews stuck in Queued or Running beyond the limit as Failed (Timeout). The agent then retries them, at most 3 times.</Body1>
          <div><Button onClick={() => void run(() => api.recoverStuck(project.projectId).then((r) => r.recovered), setRecovered)}>Recover stuck reviews</Button></div>
          <div aria-live="polite" role="status">
            {recovered && <Body1>{recovered.length === 0 ? 'No stuck reviews found.' : `Marked as failed: ${recovered.join(', ')}`}</Body1>}
          </div>
        </section>
      )}
    </div>
  );
}

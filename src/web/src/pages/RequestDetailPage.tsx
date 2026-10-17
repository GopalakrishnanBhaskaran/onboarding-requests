import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Link, Subtitle2, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular, Edit20Regular } from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href } from '../lib/router';
import type { CheckResult, Item, ProjectSummary, RequestView, ReviewedScope, ReviewRun } from '../lib/types';
import { checkColor, checkLabel, dateTime, fieldLabel, statusColor } from '../lib/format';
import { FLOW_OVERVIEW } from '../lib/flow';
import { ApprovalPanel } from '../components/ApprovalPanel';
import { IdTeamForm, RoleTeamForm, TaskSummary } from '../components/TeamTaskPanel';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const POLL_MS = 15000;
const POLL_LIMIT_MS = 12 * 60000;

export function RequestDetailPage({ project, requestId }: { project: ProjectSummary; requestId: string }) {
  const s = useStyles();
  const [view, setView] = useState<RequestView | null>(null);
  const [runs, setRuns] = useState<Item<ReviewRun>[]>([]);
  const [tab, setTab] = useState<'review' | 'notifications' | 'trail'>('review');
  const [trail, setTrail] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const pollStarted = useRef(0);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [v, r] = await Promise.all([api.getRequest(project.projectId, requestId), api.listRuns(project.projectId, requestId)]);
      setView(v);
      setRuns(r.items);
      setLastUpdated(new Date());
    } catch (e) {
      setError(e);
    }
  }, [project.projectId, requestId]);

  useEffect(() => {
    void load();
  }, [load]);

  // While the AI agent has the next step, refresh every 15 s for up to 12 minutes (covers two 5-minute worker runs).
  useEffect(() => {
    if (view?.status.waitingOn !== 'Agent') {
      pollStarted.current = 0;
      return;
    }
    if (!pollStarted.current) pollStarted.current = Date.now();
    if (Date.now() - pollStarted.current > POLL_LIMIT_MS) return;
    const t = window.setTimeout(() => void load(), POLL_MS);
    return () => window.clearTimeout(t);
  }, [view, load]);

  const done = (v: RequestView) => {
    setView(v);
    setLastUpdated(new Date());
    void api.listRuns(project.projectId, requestId).then((r) => setRuns(r.items)).catch(() => undefined);
  };

  if (!view && !error) return <Loading />;
  if (!view) return <ErrorBar error={error} />;
  const r = view.request.value;
  const f = r.flow;
  const st = view.status;
  const latest = runs.find((x) => x.value.id === f?.reviewRunId)?.value ?? runs[0]?.value ?? null;
  const isOperator = project.roles.includes('Operator');
  // The website's API cannot read Entra: a review with the Entra check pending waits for the worker's 5-minute run.
  const waitsForWorker = st.status === 'AiReview' && st.waitingOn === 'Agent' && latest?.outcome === 'Pending';
  const runStep = () =>
    void api.advance(project.projectId, r.id).then((v) => {
      done(v);
      setNote(v.request.value.flow?.events.length === f?.events.length
        ? 'No step to run from the website now. The Entra checks run in the AI agent worker every 5 minutes.'
        : null);
    }).catch(setError);

  return (
    <div className={s.stack}>
      <PageTitle>{`Request ${r.id.slice(0, 8)}`}</PageTitle>
      <ErrorBar error={error} />

      <section className={s.section} aria-labelledby="status-h">
        <div className={s.row}>
          <Subtitle2 as="h2" id="status-h">Status</Subtitle2>
          <Badge appearance="filled" color={statusColor[st.status]}>{st.currentStage}</Badge>
          <Caption1 className={s.muted}>waiting on {st.waitingOnLabel} · {st.timeInStage} in this stage</Caption1>
        </div>
        <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(120px, max-content) 1fr', gap: '4px 16px', margin: 0 }}>
          <dt><Caption1>Requester</Caption1></dt><dd style={{ margin: 0 }}><Body1>{st.requester}</Body1></dd>
          <dt><Caption1>Done</Caption1></dt><dd style={{ margin: 0 }}><Body1>{st.done.join(' · ') || '—'}</Body1></dd>
          <dt><Caption1>Next</Caption1></dt><dd style={{ margin: 0 }}><Body1>{st.next}</Body1></dd>
          <dt><Caption1>ADO work item</Caption1></dt>
          <dd style={{ margin: 0 }}><Body1>{st.adoLink ? <Link href={st.adoLink} target="_blank" rel="noopener noreferrer">Open</Link> : 'Not linked'}</Body1></dd>
        </dl>
        <ul aria-label="Flow progress" style={{ margin: 0, paddingLeft: 0, display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {FLOW_OVERVIEW.map((step, i) => (
            <li key={step} style={{ listStyle: 'none' }}>
              <Badge appearance={i < st.done.length ? 'filled' : 'outline'} color={i < st.done.length ? 'success' : 'informative'}>{step}</Badge>
            </li>
          ))}
        </ul>
        {waitsForWorker && (
          <Body1>
            The website has run the rule checks. The read-only Entra check runs in the AI agent worker every 5 minutes
            (at :00, :05, :10 … past each hour), then the request moves to Owner approval or back to you. This page refreshes itself.
            If it is still here after 10 minutes, an Operator checks Issues and reports.
          </Body1>
        )}
        {f?.lastReason && (st.status === 'Returned' || st.status === 'Rejected' || st.status === 'FailedNeedsDev') && (
          <Body1 className={s.pre}><b>Reason:</b> {f.lastReason}</Body1>
        )}
        <div className={s.row}>
          {view.actions.includes('Resubmit') && (
            <Button appearance="primary" icon={<Edit20Regular />} as="a" href={href({ page: 'edit', requestId: r.id })}>Correct and resubmit</Button>
          )}
          {isOperator && st.status === 'FailedNeedsDev' && (
            <Button appearance="primary" onClick={() => void api.resume(project.projectId, r.id).then(done).catch(setError)}>Resume after fix</Button>
          )}
          {isOperator && <Button onClick={runStep}>Run agent step now</Button>}
          <Button icon={<ArrowClockwise20Regular />} onClick={() => void load()}>Refresh</Button>
          <Caption1 className={s.muted}>Last updated {lastUpdated ? lastUpdated.toLocaleTimeString() : '—'}</Caption1>
        </div>
        {note && <Caption1 role="status">{note}</Caption1>}
      </section>

      <section className={s.section} aria-labelledby="req-h">
        <Subtitle2 as="h2" id="req-h">Request · revision {r.revision}</Subtitle2>
        <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(120px, max-content) 1fr', gap: '4px 16px', margin: 0 }}>
          {(['enterpriseId', 'firstName', 'lastName', 'domainId', 'resource', 'accessLevel', 'owner', 'justification'] as const).map((k) => (
            <div key={k} style={{ display: 'contents' }}>
              <dt><Caption1>{fieldLabel[k]}</Caption1></dt>
              <dd style={{ margin: 0 }} className={s.pre}><Body1>{r[k] || '—'}</Body1></dd>
            </div>
          ))}
          <dt><Caption1>{fieldLabel.guest}</Caption1></dt><dd style={{ margin: 0 }}><Body1>{r.guest ? 'Yes' : 'No'}</Body1></dd>
        </dl>
      </section>

      {view.actions.includes('ApproveOwner') && <ApprovalPanel request={r} stage="owner" onDone={done} />}
      {view.actions.includes('ApproveSecurity') && <ApprovalPanel request={r} stage="security" onDone={done} />}

      {f?.approvals.length ? (
        <section className={s.section} aria-labelledby="decisions-h">
          <Subtitle2 as="h2" id="decisions-h">Decisions</Subtitle2>
          {f.approvals.map((a, i) => (
            <Body1 key={i}>
              {a.stage}: {a.decision} by {a.byName} · {dateTime(a.at)} · via {a.channel}{a.superseded ? ' (superseded: the reviewed group or role changed)' : ''}{a.remarks ? ` — ${a.remarks}` : ''}
            </Body1>
          ))}
        </section>
      ) : null}

      {f?.idTeamTask && (
        <section className={s.section} aria-labelledby="idteam-h">
          <span id="idteam-h" hidden>ID team</span>
          <TaskSummary task={f.idTeamTask} title="ID team" />
          {view.actions.includes('IdTeamTask') && <IdTeamForm request={r} onDone={done} />}
        </section>
      )}
      {f?.roleTeamTask && (
        <section className={s.section} aria-labelledby="roleteam-h">
          <span id="roleteam-h" hidden>Role assignment team</span>
          <TaskSummary task={f.roleTeamTask} title="Role assignment team" />
          {view.actions.includes('RoleTeamTask') && <RoleTeamForm request={r} onDone={done} />}
        </section>
      )}
      {f?.finalChecks.length ? (
        <section className={s.section} aria-labelledby="final-h">
          <Subtitle2 as="h2" id="final-h">Final verification</Subtitle2>
          <Checks checks={f.finalChecks} label="Final verification" />
        </section>
      ) : null}

      <section className={s.section} aria-labelledby="time-h">
        <Subtitle2 as="h2" id="time-h">Time per stage · total {view.timings.total.text}</Subtitle2>
        <div className={s.row} style={{ alignItems: 'flex-start', gap: 32 }}>
          <div>
            <Caption1>By stage</Caption1>
            {view.timings.perStage.map((t) => <Body1 key={t.name} block>{t.name}: {t.text}</Body1>)}
          </div>
          <div>
            <Caption1>Wait time per team</Caption1>
            {view.timings.perTeam.map((t) => <Body1 key={t.name} block>{t.name}: {t.text}</Body1>)}
          </div>
        </div>
      </section>

      <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)} aria-label="Request details">
        <Tab value="review">AI review ({runs.length})</Tab>
        <Tab value="notifications">Notifications ({view.notifications.length})</Tab>
        <Tab value="trail">Audit trail{view.issues.length ? ` · ${view.issues.length} issue(s)` : ''}</Tab>
      </TabList>

      {tab === 'review' && (latest ? <ReviewView run={latest} runs={runs} scope={f?.scope} /> : <Empty>No review yet.</Empty>)}
      {tab === 'notifications' && <Notifications view={view} />}
      {tab === 'trail' && (
        <section className={s.section} aria-labelledby="trail-h">
          <Subtitle2 as="h2" id="trail-h">Audit trail</Subtitle2>
          {view.issues.map((i) => (
            <Body1 key={i.id}><Badge color={i.severity === 'High' ? 'danger' : i.severity === 'Warning' ? 'warning' : 'informative'}>{i.kind}</Badge> {i.status} · ×{i.occurrences} · {i.summary}</Body1>
          ))}
          {trail === null
            ? <div><Button onClick={() => void api.trail(project.projectId, r.id).then((t) => setTrail(t.text)).catch(setError)}>Show full trail</Button></div>
            : <pre className={s.pre} style={{ margin: 0, fontSize: 12 }}>{trail}</pre>}
        </section>
      )}
    </div>
  );
}

function Checks({ checks, label }: { checks: CheckResult[]; label: string }) {
  const s = useStyles();
  return (
    <div className={s.tableWrap}>
      <Table aria-label={label} size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Group</TableHeaderCell>
            <TableHeaderCell>Check</TableHeaderCell>
            <TableHeaderCell>Result</TableHeaderCell>
            <TableHeaderCell>Detail</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {checks.map((c) => (
            <TableRow key={c.code}>
              <TableCell><Caption1>{c.group}</Caption1></TableCell>
              <TableCell><Caption1>{c.code}</Caption1></TableCell>
              <TableCell><Badge color={checkColor[c.status]}>{checkLabel[c.status]}</Badge></TableCell>
              <TableCell><span className={s.pre}>{c.detail}</span></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function ReviewView({ run, runs, scope }: { run: ReviewRun; runs: Item<ReviewRun>[]; scope?: ReviewedScope | null }) {
  const s = useStyles();
  const result = run.result;
  return (
    <div className={s.stack}>
      <div className={s.row}>
        <Badge appearance="outline">{run.processingStatus}</Badge>
        {run.outcome && <Badge appearance="filled" color={run.outcome === 'Pass' ? 'success' : run.outcome === 'Fail' ? 'danger' : 'informative'}>Review {run.outcome}</Badge>}
        <Caption1 className={s.muted}>Run {run.id} · {run.trigger ?? 'review'} · reviewer {run.reviewerProvider} {run.reviewerVersion} · {dateTime(run.completedAt ?? run.queuedAt)}</Caption1>
      </div>
      {run.processingStatus === 'Failed' && <Body1>The review could not complete ({run.errorCategory ?? 'unknown'}). The agent retries it at most 3 times.</Body1>}
      {result && (
        <>
          <section className={s.section} aria-labelledby="summary-h">
            <Subtitle2 as="h2" id="summary-h">AI summary for the approvers</Subtitle2>
            <Body1 className={s.pre}>{result.summary}</Body1>
            {scope?.azureRoleDefinitionId && (
              <Body1>Azure role in this approval: {scope.azureRoleDefinitionId} at {scope.azureScope} on group {scope.entraGroup}.</Body1>
            )}
            {result.riskFlags.length > 0 && (
              <div className={s.row}>{result.riskFlags.map((flag) => <Badge key={flag} color="warning">{flag}</Badge>)}</div>
            )}
          </section>
          {result.returnReasons.length > 0 && (
            <section className={s.section} aria-labelledby="reasons-h">
              <Subtitle2 as="h2" id="reasons-h">Why it was returned</Subtitle2>
              <ul style={{ margin: 0 }}>{result.returnReasons.map((x, i) => <li key={i}><Body1>{x}</Body1></li>)}</ul>
            </section>
          )}
          <section className={s.section} aria-labelledby="checks-h">
            <Subtitle2 as="h2" id="checks-h">Checks ({result.checks.filter((c) => c.status === 'Pass').length} passed)</Subtitle2>
            <Checks checks={result.checks} label="Review checks" />
          </section>
          <section className={s.section} aria-labelledby="limits-h">
            <Subtitle2 as="h2" id="limits-h">Mapping and limitations</Subtitle2>
            <Body1>{result.mapping.status}{result.mapping.rowId ? `: row ${result.mapping.rowId} v${result.mapping.rowVersion} → ${result.mapping.entraGroup} / ${result.mapping.role}` : ''}</Body1>
            <ul style={{ margin: 0 }}>{result.limitations.map((l, i) => <li key={i}><Caption1>{l}</Caption1></li>)}</ul>
            <Caption1 className={s.muted}>Snapshot {run.snapshotHash.slice(0, 12)}… · sheet fingerprint {run.sheetFingerprint?.slice(0, 12) ?? '—'}…</Caption1>
          </section>
        </>
      )}
      {runs.length > 1 && (
        <Caption1 className={s.muted}>
          History: {runs.map((x) => `${x.value.id.split('.').slice(1).join('.')} ${x.value.processingStatus}${x.value.outcome ? `/${x.value.outcome}` : ''}`).join(' · ')}
        </Caption1>
      )}
    </div>
  );
}

function Notifications({ view }: { view: RequestView }) {
  const s = useStyles();
  if (view.notifications.length === 0) return <Empty>No notifications yet.</Empty>;
  return (
    <div className={s.stack}>
      <Caption1 className={s.muted}>In the pilot, notifications are kept here (status Recorded); none is mailed.</Caption1>
      {view.notifications.map((n) => (
        <details key={n.id} className={s.section}>
          <summary><Body1>{dateTime(n.createdAt)} · <b>{n.kind}</b> · to {n.to.join(', ') || '—'} · {n.status} — {n.subject}</Body1></summary>
          <pre className={s.pre} style={{ margin: 0, fontSize: 12 }}>{n.body}</pre>
        </details>
      ))}
    </div>
  );
}

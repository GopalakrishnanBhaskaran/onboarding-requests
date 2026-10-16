import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Field, Link, Radio, RadioGroup, Subtitle2, Tab, TabList,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular, Copy20Regular, Edit20Regular, Search20Regular } from '@fluentui/react-icons';
import { api, newNonce } from '../lib/api';
import { href } from '../lib/router';
import type { AccessRequest, DecisionOutcome, Item, ProjectSummary, ReviewDecision, ReviewRun } from '../lib/types';
import { dateTime, fieldLabel, inProgress, readinessColor, readinessLabel, severityColor } from '../lib/format';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const POLL_MS = 3000;
const POLL_LIMIT_MS = 60000;

export function RequestDetailPage({ project, requestId }: { project: ProjectSummary; requestId: string }) {
  const s = useStyles();
  const isPmo = project.roles.includes('PMO');
  const isReviewer = project.roles.includes('Reviewer');

  const [request, setRequest] = useState<Item<AccessRequest> | null>(null);
  const [runs, setRuns] = useState<Item<ReviewRun>[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [tab, setTab] = useState<'results' | 'history'>('results');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const pollStarted = useRef<number>(0);
  const nonce = useRef<string>(newNonce());

  const load = useCallback(async () => {
    setError(null);
    try {
      const [req, list] = await Promise.all([
        api.getRequest(project.projectId, requestId),
        api.listRuns(project.projectId, requestId),
      ]);
      setRequest(req);
      setRuns(list.items);
      setSelected((cur) => cur ?? list.items[0]?.value.id ?? null);
      setLastUpdated(new Date());
      return list.items;
    } catch (e) {
      setError(e);
      return [];
    }
  }, [project.projectId, requestId]);

  useEffect(() => {
    void load();
  }, [load]);

  const current = runs.find((r) => r.value.id === selected)?.value ?? runs[0]?.value ?? null;

  // Poll every 3 s for up to 60 s while the selected run is Queued/Running.
  useEffect(() => {
    if (!current || !inProgress(current.processingStatus)) return;
    if (!pollStarted.current) pollStarted.current = Date.now();
    if (Date.now() - pollStarted.current > POLL_LIMIT_MS) {
      setStatus('Still processing. Use Refresh to check again.');
      return;
    }
    const t = window.setTimeout(() => void load(), POLL_MS);
    return () => window.clearTimeout(t);
  }, [current, load]);

  async function start(kind: 'analyze' | 'retry', runId?: string) {
    setBusy(true);
    setError(null);
    setStatus(kind === 'analyze' ? 'Analysis requested…' : 'Retry requested…');
    try {
      const res = kind === 'analyze'
        ? await api.analyze(project.projectId, requestId, nonce.current)
        : await api.retry(project.projectId, runId!, nonce.current);
      nonce.current = newNonce(); // one nonce per deliberate click; double-clicks reuse it
      pollStarted.current = Date.now();
      setSelected(res.runId);
      setTab('results');
      setStatus(res.duplicate ? `A review for this revision already exists (${res.processingStatus}).` : `Review ${res.processingStatus.toLowerCase()}.`);
      await load();
    } catch (e) {
      setError(e);
      setStatus('');
    } finally {
      setBusy(false);
    }
  }

  if (!request && !error) return <Loading />;
  if (!request) return <ErrorBar error={error} />;
  const r = request.value;

  return (
    <div className={s.stack}>
      <PageTitle>{`Request ${r.id.slice(0, 8)} · revision ${r.revision}`}</PageTitle>
      <ErrorBar error={error} />

      <section className={s.section} aria-labelledby="req-heading">
        <Subtitle2 as="h2" id="req-heading">Request</Subtitle2>
        <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(120px, max-content) 1fr', gap: '4px 16px', margin: 0 }}>
          {(['requestType', 'role', 'environment', 'manager', 'startDate', 'endDate', 'justification'] as const).map((k) => (
            <div key={k} style={{ display: 'contents' }}>
              <dt><Caption1>{fieldLabel[k]}</Caption1></dt>
              <dd style={{ margin: 0 }} className={s.pre}><Body1>{r[k] || '—'}</Body1></dd>
            </div>
          ))}
        </dl>
        <div className={s.row}>
          {isPmo && (
            <>
              <Button appearance="primary" icon={<Search20Regular />} disabled={busy} onClick={() => void start('analyze')}>
                {busy ? 'Analyzing…' : 'Analyze request'}
              </Button>
              <Button icon={<Edit20Regular />} as="a" href={href({ page: 'edit', requestId: r.id })}>Edit</Button>
            </>
          )}
          <Button icon={<ArrowClockwise20Regular />} onClick={() => void load()}>Refresh</Button>
          <Caption1 className={s.muted}>Last updated {lastUpdated ? lastUpdated.toLocaleTimeString() : '—'}</Caption1>
        </div>
        <div aria-live="polite" role="status"><Caption1>{status}</Caption1></div>
      </section>

      <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'results' | 'history')} aria-label="Review views">
        <Tab value="results">Review results</Tab>
        <Tab value="history">Review history ({runs.length})</Tab>
      </TabList>

      {tab === 'results' ? (
        current ? (
          <RunView
            run={current}
            project={project}
            currentRevision={r.revision}
            canRetry={isPmo}
            canReview={isReviewer}
            onRetry={(id) => void start('retry', id)}
          />
        ) : (
          <Empty>No review yet.{isPmo ? ' Select “Analyze request”.' : ''}</Empty>
        )
      ) : (
        <History runs={runs} onSelect={(id) => { setSelected(id); setTab('results'); }} />
      )}
    </div>
  );
}

function RunStatus({ run }: { run: ReviewRun }) {
  return (
    <>
      <Badge appearance="outline" color={run.processingStatus === 'Failed' ? 'danger' : run.processingStatus === 'Superseded' ? 'subtle' : 'brand'}>
        {run.processingStatus === 'Superseded' ? `Superseded by revision ${run.supersededByRevision}` : run.processingStatus}
      </Badge>
      {run.readiness && run.processingStatus !== 'Failed' && (
        <Badge appearance="filled" color={readinessColor[run.readiness]}>{readinessLabel[run.readiness]}</Badge>
      )}
    </>
  );
}

function RunView(props: {
  run: ReviewRun;
  project: ProjectSummary;
  currentRevision: number;
  canRetry: boolean;
  canReview: boolean;
  onRetry: (runId: string) => void;
}) {
  const { run, project, currentRevision, canRetry, canReview, onRetry } = props;
  const s = useStyles();
  const result = run.result;
  const [copied, setCopied] = useState(false);

  async function copyQuestions() {
    if (!result) return;
    try {
      await navigator.clipboard.writeText(result.clarificationQuestions.map((q, i) => `${i + 1}. ${q}`).join('\n'));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className={s.stack}>
      <div className={s.row}>
        <RunStatus run={run} />
        <Caption1 className={s.muted}>
          Run {run.id} · revision {run.requestRevision}{run.requestRevision !== currentRevision ? ' (older revision)' : ''} · reviewer {run.reviewerProvider} {run.reviewerVersion} · {dateTime(run.completedAt ?? run.queuedAt)}
        </Caption1>
      </div>

      {inProgress(run.processingStatus) && <Body1>Review is {run.processingStatus.toLowerCase()}…</Body1>}

      {run.processingStatus === 'Failed' && (
        <section className={s.section}>
          <Body1>The review could not complete ({run.errorCategory ?? 'unknown'}). No result was produced.</Body1>
          {canRetry && <div><Button onClick={() => onRetry(run.id)}>Retry review</Button></div>}
        </section>
      )}

      {result && (
        <>
          <section className={s.section} aria-labelledby="summary-h">
            <Subtitle2 as="h2" id="summary-h">Summary</Subtitle2>
            <Body1 className={s.pre}>{result.summary}</Body1>
          </section>

          <section className={s.section} aria-labelledby="findings-h">
            <Subtitle2 as="h2" id="findings-h">Findings ({result.findings.length})</Subtitle2>
            {result.findings.length === 0 ? (
              <Empty>No findings.</Empty>
            ) : (
              <div className={s.tableWrap}>
                <Table aria-label="Findings" size="small">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Severity</TableHeaderCell>
                      <TableHeaderCell>Field</TableHeaderCell>
                      <TableHeaderCell>Finding</TableHeaderCell>
                      <TableHeaderCell>Evidence</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {result.findings.map((f, i) => (
                      <TableRow key={`${f.code}-${i}`}>
                        <TableCell><Badge color={severityColor[f.severity]}>{f.severity}</Badge></TableCell>
                        <TableCell>{f.field ? fieldLabel[f.field] ?? f.field : '—'}</TableCell>
                        <TableCell><span className={s.pre}>{f.message}</span> <Caption1 className={s.muted}>({f.code})</Caption1></TableCell>
                        <TableCell>{f.ruleId ? `${f.ruleId} v${f.ruleVersion} · ${f.sourceReference ?? ''}` : '—'}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}
          </section>

          <section className={s.section} aria-labelledby="mapping-h">
            <Subtitle2 as="h2" id="mapping-h">Role mapping assessment</Subtitle2>
            <div className={s.row}>
              <Badge appearance="outline">{result.mappingAssessment.status}</Badge>
              {result.mappingAssessment.mappingId && (
                <Caption1>{result.mappingAssessment.mappingId} v{result.mappingAssessment.mappingVersion}</Caption1>
              )}
            </div>
            <Body1 className={s.pre}>{result.mappingAssessment.explanation}</Body1>
          </section>

          <section className={s.section} aria-labelledby="questions-h">
            <div className={s.row}>
              <Subtitle2 as="h2" id="questions-h">Clarification questions</Subtitle2>
              {result.clarificationQuestions.length > 0 && (
                <Button size="small" icon={<Copy20Regular />} onClick={() => void copyQuestions()}>
                  {copied ? 'Copied' : 'Copy'}
                </Button>
              )}
            </div>
            {result.clarificationQuestions.length === 0 ? (
              <Empty>None.</Empty>
            ) : (
              <ol style={{ margin: 0 }}>
                {result.clarificationQuestions.map((q, i) => <li key={i}><Body1>{q}</Body1></li>)}
              </ol>
            )}
          </section>

          <section className={s.section} aria-labelledby="limits-h">
            <Subtitle2 as="h2" id="limits-h">Limitations and evidence versions</Subtitle2>
            <ul style={{ margin: 0 }}>
              {result.limitations.map((l, i) => <li key={i}><Caption1>{l}</Caption1></li>)}
            </ul>
            <Caption1 className={s.muted}>
              Rules: {run.ruleVersions.map((v) => `${v.id} v${v.version}`).join(', ') || '—'} · Mappings: {run.mappingVersions.map((v) => `${v.id} v${v.version}`).join(', ') || '—'} · Snapshot {run.snapshotHash.slice(0, 12)}…
            </Caption1>
          </section>

          {canReview && (run.processingStatus === 'Completed' || run.processingStatus === 'Superseded') && (
            <Feedback projectId={project.projectId} runId={run.id} />
          )}
        </>
      )}
    </div>
  );
}

function History({ runs, onSelect }: { runs: Item<ReviewRun>[]; onSelect: (id: string) => void }) {
  const s = useStyles();
  if (runs.length === 0) return <Empty>No reviews yet.</Empty>;
  return (
    <div className={s.tableWrap}>
      <Table aria-label="Review history" size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Run</TableHeaderCell>
            <TableHeaderCell>Revision</TableHeaderCell>
            <TableHeaderCell>Status</TableHeaderCell>
            <TableHeaderCell>Rule / mapping versions</TableHeaderCell>
            <TableHeaderCell>Reviewer</TableHeaderCell>
            <TableHeaderCell>Queued</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {runs.map(({ value: run }) => (
            <TableRow key={run.id}>
              <TableCell>
                <Link as="button" onClick={() => onSelect(run.id)}>{run.id.split('.').slice(1).join('.') || run.id}</Link>
                {run.retryOfRunId && <Caption1 className={s.muted}> retry</Caption1>}
              </TableCell>
              <TableCell>{run.requestRevision}</TableCell>
              <TableCell><div className={s.row}><RunStatus run={run} /></div></TableCell>
              <TableCell>
                <Caption1>
                  {run.ruleVersions.length} rules · {run.mappingVersions.map((v) => `${v.id} v${v.version}`).join(', ') || 'no mapping'}
                </Caption1>
              </TableCell>
              <TableCell>{run.reviewerProvider}</TableCell>
              <TableCell>{dateTime(run.queuedAt)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

/** Reviewer feedback on review QUALITY. It is not an access decision. */
function Feedback({ projectId, runId }: { projectId: string; runId: string }) {
  const s = useStyles();
  const [decisions, setDecisions] = useState<Item<ReviewDecision>[] | null>(null);
  const [outcome, setOutcome] = useState<DecisionOutcome>('Accepted');
  const [comment, setComment] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(() => {
    api.listDecisions(projectId, runId).then((d) => setDecisions(d.items)).catch(setError);
  }, [projectId, runId]);
  useEffect(load, [load]);

  const needsComment = outcome !== 'Accepted' && comment.trim() === '';

  async function submit() {
    setSaving(true);
    setError(null);
    try {
      await api.recordDecision(projectId, runId, outcome, comment.trim());
      setComment('');
      load();
    } catch (e) {
      setError(e);
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className={s.section} aria-labelledby="feedback-h">
      <Subtitle2 as="h2" id="feedback-h">Reviewer feedback on this review</Subtitle2>
      <Caption1 className={s.muted}>Rates the quality of the automated review. It does not approve the request.</Caption1>
      <ErrorBar error={error} />
      {decisions?.map(({ value: d }) => (
        <Body1 key={d.id}>{d.outcome} by {d.reviewer} · {dateTime(d.timestamp)}{d.comment ? ` — ${d.comment}` : ''}</Body1>
      ))}
      <RadioGroup layout="horizontal" value={outcome} onChange={(_, d) => setOutcome(d.value as DecisionOutcome)} aria-label="Feedback outcome">
        <Radio value="Accepted" label="Accepted" />
        <Radio value="Corrected" label="Corrected" />
        <Radio value="Escalated" label="Escalated" />
      </RadioGroup>
      <Field label="Comment" hint={outcome === 'Accepted' ? 'Optional.' : 'Required for Corrected or Escalated.'}>
        <Textarea value={comment} onChange={(_, d) => setComment(d.value)} maxLength={1000} />
      </Field>
      <div>
        <Button onClick={() => void submit()} disabled={saving || needsComment}>Save feedback</Button>
      </div>
    </section>
  );
}

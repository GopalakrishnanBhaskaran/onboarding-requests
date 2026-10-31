import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge, Body1, Body1Strong, Button, Caption1, Link, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Subtitle2, Tab, TabList,
  makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwise20Regular, ArrowRight16Regular, Clock16Regular, Edit20Regular, Mail20Regular, Sparkle20Regular,
} from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href } from '../lib/router';
import type { Item, ProjectSummary, RequestView, ReviewedScope, ReviewRun } from '../lib/types';
import { dateTime, fieldLabel, grantsOf, scopeName, statusColor } from '../lib/format';
import { journey, stepSeconds, waitingLong } from '../lib/tracker';
import { useActionQueue } from '../lib/queue';
import { ApprovalPanel } from '../components/ApprovalPanel';
import { IdTeamForm, RoleTeamForm, TaskSummary } from '../components/TeamTaskPanel';
import { Breakable, Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';
import { OffboardingPanel } from '../components/OffboardingPanel';
import { JourneyStepper } from '../components/Journey';
import { CheckDigest } from '../components/CheckDigest';

const POLL_MS = 15000;
const POLL_LIMIT_MS = 12 * 60000;

const useLocal = makeStyles({
  hero: { gap: '18px', paddingTop: '22px', paddingBottom: '22px' },
  heroTop: { display: 'flex', flexWrap: 'wrap', justifyContent: 'space-between', alignItems: 'flex-start', gap: '12px 24px' },
  who: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px 10px', color: tokens.colorNeutralForeground2 },
  chips: { display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '6px' },
  layout: {
    display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 340px', gap: '20px', alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  aside: { display: 'flex', flexDirection: 'column', gap: '20px', minWidth: 0 },
  wait: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px 8px', fontSize: tokens.fontSizeBase400 },
  waitLong: { color: tokens.colorStatusWarningForeground1 },
  next: {
    display: 'flex', alignItems: 'flex-start', gap: '10px', padding: '12px 14px', borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2, '& svg': { flexShrink: 0, marginTop: '2px', color: tokens.colorBrandForeground1 },
  },
  bars: { display: 'grid', gridTemplateColumns: 'minmax(90px, max-content) minmax(0, 1fr) max-content', gap: '6px 10px', alignItems: 'center' },
  track: { height: '8px', borderRadius: '4px', backgroundColor: tokens.colorNeutralBackground3, overflow: 'hidden' },
  fill: { height: '100%', borderRadius: '4px', backgroundColor: tokens.colorBrandBackground, minWidth: '2px' },
  fillTeam: { backgroundColor: tokens.colorPaletteTealBackground2 },
  decision: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '10px 12px', borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground2 },
  quote: { borderLeft: `3px solid ${tokens.colorNeutralStroke1}`, paddingLeft: '8px', marginTop: '4px', color: tokens.colorNeutralForeground2 },
  aiHead: { display: 'flex', alignItems: 'center', gap: '8px', '& svg': { color: tokens.colorBrandForeground1 } },
  note: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '12px 14px', borderRadius: tokens.borderRadiusLarge, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  noteHead: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px 10px', cursor: 'pointer' },
});

export function RequestDetailPage({ project, requestId }: { project: ProjectSummary; requestId: string }) {
  const s = useStyles();
  const l = useLocal();
  const queue = useActionQueue();
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
  const polling = view?.status.waitingOn === 'Agent' && (!pollStarted.current || Date.now() - pollStarted.current <= POLL_LIMIT_MS);
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
    queue.refresh();
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
  const steps = journey(st.status, st.done);
  const long = waitingLong(st);
  const name = `${r.firstName ?? ''} ${r.lastName ?? ''}`.trim();
  const runStep = () =>
    void api.advance(project.projectId, r.id).then((v) => {
      done(v);
      setNote(v.request.value.flow?.events.length === f?.events.length
        ? 'No step to run from the website now. The Entra checks run in the AI agent worker every 5 minutes.'
        : null);
    }).catch(setError);
  const maxStage = Math.max(1, ...view.timings.perStage.map((t) => t.seconds));
  const maxTeam = Math.max(1, ...view.timings.perTeam.map((t) => t.seconds));

  return (
    <div className={s.stack}>
      <section className={mergeClasses(s.section, l.hero)} aria-label="Journey">
        <div className={l.heroTop}>
          <div style={{ minWidth: 0 }}>
            <Caption1 className={s.eyebrow}>Onboarding request · revision {r.revision}</Caption1>
            <PageTitle>{`Request ${r.id.slice(0, 8)}`}</PageTitle>
            <div className={l.who}>
              {name && <Body1Strong>{name}</Body1Strong>}
              {r.enterpriseId && <Body1 className={s.wrap}><Breakable text={r.enterpriseId} /></Body1>}
              {r.guest && <Badge appearance="tint" color="warning">Guest</Badge>}
            </div>
            <div className={l.chips}>
              {r.resource && <Badge appearance="tint" color="brand" size="large">{`${r.accessLevel ?? '—'} on ${r.resource}`}</Badge>}
              {r.environment && <Badge appearance="outline" size="large">{`Environment ${r.environment}`}</Badge>}
              <Badge appearance="outline" size="large">{`Tenant ${project.tenantName || project.name}`}</Badge>
            </div>
          </div>
          <div style={{ textAlign: 'right' }}>
            <Caption1 block className={s.muted}>Total so far</Caption1>
            <Subtitle2>{view.timings.total.text}</Subtitle2>
          </div>
        </div>
        <JourneyStepper steps={steps} seconds={stepSeconds(view.timings.perStage)} />
      </section>
      <ErrorBar error={error} />

      <div className={l.layout}>
        <div className={s.stack} style={{ minWidth: 0 }}>
          <section className={s.section} aria-labelledby="status-h">
            <div className={s.row}>
              <Subtitle2 as="h2" id="status-h">Status</Subtitle2>
              <Badge appearance="filled" color={statusColor[st.status]} size="large">{st.currentStage}</Badge>
              {polling && <Spinner size="extra-tiny" label="Live: refreshes while the AI agent works" labelPosition="after" />}
            </div>
            <div className={mergeClasses(l.wait, long && l.waitLong)}>
              <Clock16Regular aria-hidden="true" />
              <span>Waiting on <b>{st.waitingOnLabel || '—'}</b> · {st.timeInStage} in this stage{long ? ' (more than 24 hours)' : ''}</span>
            </div>
            {st.next && (
              <div className={l.next}>
                <ArrowRight16Regular aria-hidden="true" />
                <span><Caption1 block className={s.muted}>What happens next</Caption1><Body1>{st.next}</Body1></span>
              </div>
            )}
            {waitsForWorker && (
              <Body1>
                The website has run the rule checks. The read-only Entra check runs in the AI agent worker every 5 minutes
                (at :00, :05, :10 … past each hour), then the request moves to Owner approval or back to you. This page refreshes itself.
                If it is still here after 10 minutes, an Operator checks Issues and reports.
              </Body1>
            )}
            {f?.lastReason && (st.status === 'Returned' || st.status === 'Rejected' || st.status === 'FailedNeedsDev') && (
              <MessageBar intent={st.status === 'Returned' ? 'warning' : 'error'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{st.status === 'Returned' ? 'Why it came back to the requester' : st.status === 'Rejected' ? 'Why it stopped' : 'What failed'}</MessageBarTitle>
                  <span className={s.pre}><b>Reason:</b> {f.lastReason}</span>
                </MessageBarBody>
              </MessageBar>
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

          {view.actions.includes('ApproveOwner') && <ApprovalPanel request={r} stage="owner" project={project} onDone={done} onStale={() => void load()} />}
          {view.actions.includes('ApproveSecurity') && <ApprovalPanel request={r} stage="security" project={project} onDone={done} onStale={() => void load()} />}
          {view.approvalHint && (
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody><MessageBarTitle>Waiting for a decision</MessageBarTitle>{view.approvalHint}</MessageBarBody>
            </MessageBar>
          )}

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
              <CheckDigest checks={f.finalChecks} label="Final verification" />
            </section>
          ) : null}

          {(project.roles.includes('PMO') || project.roles.includes('Operator')) && r.domainId && (
            <OffboardingPanel project={project} enterpriseId={r.domainId} />
          )}

          <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)} aria-label="Request details">
            <Tab value="review" icon={<Sparkle20Regular />}>AI review ({runs.length})</Tab>
            <Tab value="notifications" icon={<Mail20Regular />}>Notifications ({view.notifications.length})</Tab>
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

        <aside className={l.aside} aria-label="Request facts">
          <section className={s.section} aria-labelledby="req-h">
            <Subtitle2 as="h2" id="req-h">Request · revision {r.revision}</Subtitle2>
            <dl className={s.facts}>
              <dt><Caption1>Requester</Caption1></dt><dd><Body1 className={s.wrap}><Breakable text={st.requester} /></Body1></dd>
              <dt><Caption1>Tenant</Caption1></dt><dd><Body1>{project.tenantName || project.name}</Body1></dd>
              {(['enterpriseId', 'firstName', 'lastName', 'domainId', 'resource', 'accessLevel', 'environment', 'owner'] as const).map((k) => (
                <div key={k} style={{ display: 'contents' }}>
                  <dt><Caption1>{fieldLabel[k]}</Caption1></dt>
                  <dd className={s.wrap}><Body1>{r[k] ? <Breakable text={r[k]} /> : '—'}</Body1></dd>
                </div>
              ))}
              <dt><Caption1>{fieldLabel.guest}</Caption1></dt><dd><Body1>{r.guest ? 'Yes' : 'No'}</Body1></dd>
              <dt><Caption1>ADO work item</Caption1></dt>
              <dd><Body1>{st.adoLink ? <Link href={st.adoLink} target="_blank" rel="noopener noreferrer">Open</Link> : 'Not linked'}</Body1></dd>
            </dl>
            <Caption1 className={s.eyebrow} style={{ marginTop: 6 }}>{fieldLabel.justification}</Caption1>
            <Body1 className={s.pre}>{r.justification || '—'}</Body1>
          </section>

          {f?.approvals.length ? (
            <section className={s.section} aria-labelledby="decisions-h">
              <Subtitle2 as="h2" id="decisions-h">Decisions</Subtitle2>
              {f.approvals.map((a, i) => (
                <div key={i} className={l.decision}>
                  <div className={s.row}>
                    <Badge appearance="filled" color={a.superseded ? 'subtle' : a.decision === 'Approved' ? 'success' : 'danger'}>{a.stage}</Badge>
                    <Caption1 className={s.muted}>{dateTime(a.at)} · via {a.channel}</Caption1>
                  </div>
                  <Body1 className={s.wrap}>
                    {a.stage}: {a.decision} by {a.byName}{a.superseded ? ' (superseded: the reviewed group or role changed)' : ''}
                  </Body1>
                  {a.remarks && <Body1 className={mergeClasses(l.quote, s.pre)}>{a.remarks}</Body1>}
                </div>
              ))}
            </section>
          ) : null}

          <section className={s.section} aria-labelledby="time-h">
            <Subtitle2 as="h2" id="time-h">Time per stage · total {view.timings.total.text}</Subtitle2>
            <Caption1 className={s.eyebrow}>By stage</Caption1>
            <div className={l.bars}>
              {view.timings.perStage.map((t) => (
                <div key={t.name} style={{ display: 'contents' }}>
                  <Caption1>{t.name}</Caption1>
                  <span className={l.track}><span className={l.fill} style={{ width: `${(100 * t.seconds) / maxStage}%` }} /></span>
                  <Caption1>{t.text}</Caption1>
                </div>
              ))}
            </div>
            <Caption1 className={s.eyebrow} style={{ marginTop: 8 }}>Wait time per team</Caption1>
            <div className={l.bars}>
              {view.timings.perTeam.map((t) => (
                <div key={t.name} style={{ display: 'contents' }}>
                  <Caption1>{t.name}</Caption1>
                  <span className={l.track}><span className={mergeClasses(l.fill, l.fillTeam)} style={{ width: `${(100 * t.seconds) / maxTeam}%` }} /></span>
                  <Caption1>{t.text}</Caption1>
                </div>
              ))}
            </div>
          </section>
        </aside>
      </div>
    </div>
  );
}

function ReviewView({ run, runs, scope }: { run: ReviewRun; runs: Item<ReviewRun>[]; scope?: ReviewedScope | null }) {
  const s = useStyles();
  const l = useLocal();
  const result = run.result;
  const passed = result ? result.checks.filter((c) => c.status === 'Pass').length : 0;
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
            <div className={l.aiHead}>
              <Sparkle20Regular aria-hidden="true" />
              <Subtitle2 as="h2" id="summary-h">AI summary for the approvers</Subtitle2>
            </div>
            <Body1 className={s.pre}>{result.summary}</Body1>
            {grantsOf(scope).length > 0 && (
              <ul aria-label="Azure roles in this review" style={{ margin: 0 }}>
                {grantsOf(scope).map((g, i) => (
                  <li key={i}><Body1>{g.roleName} on {g.resourceName ?? scopeName(g.scope)}{g.scope ? '' : ' (scope not resolved: role assignment team)'}</Body1></li>
                ))}
              </ul>
            )}
            {result.riskFlags.length > 0 && (
              <div className={s.row}>{result.riskFlags.map((flag) => <Badge key={flag} color="warning">{flag}</Badge>)}</div>
            )}
          </section>
          {result.returnReasons.length > 0 && (
            <section className={s.section} aria-labelledby="reasons-h" style={{ borderLeft: `4px solid ${tokens.colorStatusWarningBorder1}` }}>
              <Subtitle2 as="h2" id="reasons-h">Why it was returned</Subtitle2>
              <ul style={{ margin: 0 }}>{result.returnReasons.map((x, i) => <li key={i}><Body1>{x}</Body1></li>)}</ul>
            </section>
          )}
          <section className={s.section} aria-labelledby="checks-h">
            <Subtitle2 as="h2" id="checks-h">Checks · {passed} of {result.checks.length} passed</Subtitle2>
            <CheckDigest checks={result.checks} label="Review checks" />
          </section>
          <section className={s.section} aria-labelledby="limits-h">
            <Subtitle2 as="h2" id="limits-h">Mapping and limitations</Subtitle2>
            <Body1>{result.mapping.status}{result.mapping.rowId ? `: row ${result.mapping.rowId} v${result.mapping.rowVersion} → ${result.mapping.entraGroup} / ${result.mapping.role}` : ''}</Body1>
            <ul style={{ margin: 0 }}>{result.limitations.map((x, i) => <li key={i}><Caption1>{x}</Caption1></li>)}</ul>
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

const NOTE_COLOR: Record<string, 'success' | 'informative' | 'warning' | 'danger'> = { Sent: 'success', Recorded: 'informative', Pending: 'warning', Failed: 'danger' };

function Notifications({ view }: { view: RequestView }) {
  const s = useStyles();
  const l = useLocal();
  if (view.notifications.length === 0) return <Empty>No notifications yet.</Empty>;
  return (
    <div className={s.stack} style={{ gap: 8 }}>
      <Caption1 className={s.muted}>
        The AI agent worker mails pending notifications every 5 minutes when mail is switched on (status Sent); otherwise they are kept here (status Recorded).
        Over the mail limit a mail stays Pending and goes out on a later run, most urgent first. Team addresses come from the Contact inventory.
        In a test environment every mail goes to the test address.
      </Caption1>
      {view.notifications.map((n) => (
        <details key={n.id} className={l.note}>
          <summary className={l.noteHead}>
            <Badge appearance="tint" color="brand">{n.kind}</Badge>
            <Body1Strong>{n.subject}</Body1Strong>
            <Badge appearance="outline" color={NOTE_COLOR[n.status] ?? 'informative'}>{n.status}</Badge>
          </summary>
          <Caption1 className={mergeClasses(s.muted, s.wrap)}>
            {dateTime(n.createdAt)} · to {n.to.join(', ') || '—'}
            {n.deliveredTo && n.deliveredTo.join(',') !== n.to.join(',') ? ` (test: delivered to ${n.deliveredTo.join(', ')})` : ''}
            {n.lastError ? ` · ${n.lastError === 'MailThrottled' ? 'waiting: mail limit' : n.lastError === 'NoRecipients' ? 'no address: see Contact inventory' : n.lastError}` : ''}
            {n.skipped?.length ? ` · not mailed (not a mail address): ${n.skipped.join(', ')}` : ''}
          </Caption1>
          <pre className={s.pre} style={{ margin: '8px 0 0', fontSize: 12 }}>{n.body}</pre>
        </details>
      ))}
    </div>
  );
}

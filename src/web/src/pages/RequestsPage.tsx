import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Badge, Body1, Body1Strong, Button, Caption1, Link, SearchBox, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
  ToggleButton, makeStyles, mergeClasses, shorthands, tokens,
} from '@fluentui/react-components';
import {
  Add20Regular, ArrowClockwise20Regular, ArrowRight16Regular, ArrowSort16Regular, Clock16Regular, DocumentBulletList20Regular, Open16Regular,
  TaskListSquareLtr20Regular,
} from '@fluentui/react-icons';
import { api } from '../lib/api';
import { href, navigate } from '../lib/router';
import type { ProjectSummary, StatusLine } from '../lib/types';
import { statusColor } from '../lib/format';
import { useActionQueue } from '../lib/queue';
import {
  LANES, applyFilter, countBy, inBucket, journey, laneOf, waitingLong, type Bucket, type TrackerFilter,
} from '../lib/tracker';
import { JourneyDots } from '../components/Journey';
import { Breakable, ErrorBar, Loading, PageHeader, useStyles } from '../components/common';
import { ACTION_LABEL } from './QueuePage';

const BUCKETS: { key: Bucket; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'active', label: 'In progress' },
  { key: 'attention', label: 'Needs attention' },
  { key: 'completed', label: 'Completed' },
  { key: 'closed', label: 'Closed or stopped' },
];

const useStyles2 = makeStyles({
  callout: {
    display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '12px 20px', padding: '16px 20px',
    borderRadius: tokens.borderRadiusXLarge, backgroundColor: tokens.colorBrandBackground2, border: `1px solid ${tokens.colorBrandStroke2}`,
  },
  calloutIcon: {
    width: '40px', height: '40px', borderRadius: '50%', display: 'grid', placeItems: 'center', flexShrink: 0,
    backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand,
  },
  calloutList: { display: 'flex', flexWrap: 'wrap', gap: '8px', flexGrow: 1, minWidth: 0, margin: 0, padding: 0, listStyle: 'none' },
  chipLink: {
    display: 'inline-flex', alignItems: 'center', gap: '6px', padding: '4px 10px', borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1, textDecoration: 'none', fontSize: tokens.fontSizeBase200,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    ':hover': { ...shorthands.borderColor(tokens.colorBrandStroke1) },
  },
  lanes: { display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: '8px', '@media (max-width: 760px)': { gridTemplateColumns: 'repeat(4, minmax(0, 1fr))' } },
  lane: {
    display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px', padding: '10px 12px', minWidth: 0, cursor: 'pointer',
    borderRadius: tokens.borderRadiusLarge, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1, textAlign: 'left', fontFamily: 'inherit',
    ':hover': { ...shorthands.borderColor(tokens.colorBrandStroke1) },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  laneOn: { ...shorthands.borderColor(tokens.colorBrandStroke1), backgroundColor: tokens.colorBrandBackground2, boxShadow: `inset 0 0 0 1px ${tokens.colorBrandStroke1}` },
  laneWarn: { borderLeftWidth: '4px', borderLeftColor: tokens.colorStatusWarningBorder1 },
  laneCount: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold },
  laneZero: { color: tokens.colorNeutralForeground4 },
  laneLabel: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '100%' },
  toolbar: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px 12px' },
  search: { flexGrow: 1, minWidth: '240px', maxWidth: '460px' },
  rowLink: { cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  id: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  long: { color: tokens.colorStatusWarningForeground1, fontWeight: tokens.fontWeightSemibold, display: 'inline-flex', alignItems: 'center', gap: '4px' },
  emptyCard: { alignItems: 'center', textAlign: 'center', padding: '40px 20px' },
  emptyIcon: { color: tokens.colorBrandForeground1, fontSize: '40px' },
});

/**
 * The request tracker: where every request of this tenant stands and who it waits on. A pipeline of the flow's steps
 * (each a filter), overview buckets, instant search ("/" jumps to it) and one row per request with its 7-step journey.
 */
export function RequestsPage({ project, projects = [project], onOpenProject }: {
  project: ProjectSummary;
  projects?: ProjectSummary[];
  onOpenProject?: (projectId: string) => void;
}) {
  const s = useStyles();
  const t = useStyles2();
  const [items, setItems] = useState<StatusLine[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [filter, setFilter] = useState<TrackerFilter>({ bucket: 'all', lane: null, query: '', longestFirst: false });
  const search = useRef<HTMLInputElement>(null);
  const queue = useActionQueue();
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

  // "/" focuses the search, as in most trackers (not while typing somewhere else).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement | null;
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
      if (el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName))) return;
      e.preventDefault();
      search.current?.focus();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const all = items ?? [];
  const shown = useMemo(() => applyFilter(all, filter), [all, filter]);
  const buckets = useMemo(() => countBy(all, BUCKETS.map((b) => b.key), inBucket), [all]);
  const lanes = useMemo(() => countBy(all, LANES.map((l) => l.key), (r, k) => laneOf(r.status) === k), [all]);
  const longWait = useMemo(() => Object.fromEntries(LANES.map((l) => [l.key, all.some((r) => laneOf(r.status) === l.key && waitingLong(r))])), [all]);
  const filtered = filter.bucket !== 'all' || filter.lane !== null || filter.query.trim() !== '';
  const mine = queue.items ?? [];
  const tenantOf = (id: string) => projects.find((p) => p.projectId === id);

  return (
    <div className={s.stack}>
      <PageHeader
        title="Requests"
        actions={<>
          {canSubmit && <Button appearance="primary" icon={<Add20Regular />} onClick={() => navigate({ page: 'new' })}>New request</Button>}
          <Button icon={<ArrowClockwise20Regular />} onClick={() => void load()}>Refresh</Button>
        </>}
      >
        Every onboarding request in {project.tenantName || project.name}: where it stands, who it waits on, and what happens next.
      </PageHeader>

      {mine.length > 0 && (
        <section className={t.callout} aria-labelledby="mine-h">
          <span className={t.calloutIcon} aria-hidden="true"><TaskListSquareLtr20Regular /></span>
          <div style={{ minWidth: 180 }}>
            <Body1Strong id="mine-h" as="h2" style={{ margin: 0, fontSize: 'inherit' }}>
              {mine.length === 1 ? '1 request is waiting on you' : `${mine.length} requests are waiting on you`}
            </Body1Strong>
            <Caption1 block className={s.muted}>Nothing moves until you act.</Caption1>
          </div>
          <ul className={t.calloutList}>
            {mine.slice(0, 4).map((i) => (
              <li key={`${i.status.requestId}-${i.action}`}>
                <a className={t.chipLink} href={href({ page: 'request', requestId: i.status.requestId })}
                  onClick={() => onOpenProject?.(i.status.projectId)}>
                  <b>{ACTION_LABEL[i.action]}</b>
                  <span className={t.id}>{i.status.requestId.slice(0, 8)}</span>
                  {i.status.projectId !== project.projectId && <span className={s.muted}>· {tenantOf(i.status.projectId)?.tenantName ?? tenantOf(i.status.projectId)?.name}</span>}
                </a>
              </li>
            ))}
          </ul>
          <Button as="a" href={href({ page: 'queue' })} appearance="primary" icon={<ArrowRight16Regular />} iconPosition="after">Open My actions</Button>
        </section>
      )}

      {!canSubmit && (
        <Caption1>
          New requests need the Requester role. Your roles in this project: {project.roles.join(', ') || 'none'}. Ask the pilot owner to add Requester.
        </Caption1>
      )}
      <ErrorBar error={error} />

      {items === null && !error ? (
        <Loading />
      ) : items && items.length === 0 ? (
        <section className={mergeClasses(s.section, t.emptyCard)}>
          <DocumentBulletList20Regular className={t.emptyIcon} aria-hidden="true" />
          <Body1Strong>No requests yet</Body1Strong>
          <Body1 className={s.muted}>
            {canSubmit ? 'Start one: pick the joiner, the access from the mapping sheet and the Owner. The AI review checks it within seconds.' : 'Requests of this tenant appear here as they come in.'}
          </Body1>
          {canSubmit && <Button appearance="primary" icon={<Add20Regular />} onClick={() => navigate({ page: 'new' })}>New request</Button>}
        </section>
      ) : items ? (
        <>
          <section aria-label="Pipeline" className={s.stack} style={{ gap: 10 }}>
            <div className={s.row} role="group" aria-label="Show">
              {BUCKETS.map((b) => (
                <ToggleButton key={b.key} size="small" shape="circular" checked={filter.bucket === b.key && !filter.lane}
                  appearance={filter.bucket === b.key && !filter.lane ? 'primary' : 'outline'}
                  onClick={() => setFilter((f) => ({ ...f, bucket: b.key, lane: null }))}>
                  {b.label} · {buckets[b.key]}
                </ToggleButton>
              ))}
            </div>
            <div className={t.lanes} role="group" aria-label="In progress, by step">
              {LANES.map((l) => {
                const on = filter.lane === l.key;
                return (
                  <button key={l.key} type="button" aria-pressed={on}
                    className={mergeClasses(t.lane, on && t.laneOn, (longWait[l.key] || (l.key === 'returned' && lanes[l.key] > 0)) && t.laneWarn)}
                    title={longWait[l.key] ? 'A request waits here more than 24 hours' : undefined}
                    onClick={() => setFilter((f) => ({ ...f, bucket: 'all', lane: on ? null : l.key }))}>
                    <span className={mergeClasses(t.laneCount, lanes[l.key] === 0 && t.laneZero)}>{lanes[l.key]}</span>
                    <span className={t.laneLabel}>{l.label}</span>
                  </button>
                );
              })}
            </div>
          </section>

          <div className={t.toolbar}>
            <SearchBox ref={search} className={t.search} value={filter.query} aria-label="Search requests"
              placeholder="Search ID, joiner, resource, requester…  ( / )"
              onChange={(_, d) => setFilter((f) => ({ ...f, query: d.value }))}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && shown.length === 1) navigate({ page: 'request', requestId: shown[0].requestId });
              }} />
            <ToggleButton size="small" icon={<ArrowSort16Regular />} checked={filter.longestFirst}
              onClick={() => setFilter((f) => ({ ...f, longestFirst: !f.longestFirst }))}>Longest in stage first</ToggleButton>
            <Caption1 className={s.muted} role="status">{filtered ? `Showing ${shown.length} of ${all.length}` : `${all.length} request${all.length === 1 ? '' : 's'}`}</Caption1>
            {filtered && <Button size="small" appearance="transparent" onClick={() => setFilter({ bucket: 'all', lane: null, query: '', longestFirst: filter.longestFirst })}>Clear filters</Button>}
          </div>

          {shown.length === 0 ? (
            <section className={mergeClasses(s.section, t.emptyCard)}>
              <Body1Strong>No request matches</Body1Strong>
              <Body1 className={s.muted}>Try fewer words, or clear the filters.</Body1>
            </section>
          ) : (
            <div className={mergeClasses(s.section, s.tableWrap)} style={{ padding: '4px 8px' }}>
              <Table aria-label="Request status" className={s.fixedTable} style={{ minWidth: 780 }}>
                <TableHeader>
                  <TableRow>
                    {/* Widths add up to 100%; the progress column fits the longest badge ("Awaiting Prod approval"). */}
                    <TableHeaderCell style={{ width: '29%' }}>Request</TableHeaderCell>
                    <TableHeaderCell style={{ width: '17%' }}>Requester</TableHeaderCell>
                    <TableHeaderCell style={{ width: '20%' }}>Progress</TableHeaderCell>
                    <TableHeaderCell style={{ width: '24%' }}>Waiting on · next</TableHeaderCell>
                    <TableHeaderCell style={{ width: '10%' }}>In stage</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {shown.map((r) => {
                    const long = waitingLong(r);
                    return (
                      <TableRow key={r.requestId} className={t.rowLink}
                        onClick={(e) => {
                          if ((e.target as HTMLElement).closest('a,button')) return;
                          navigate({ page: 'request', requestId: r.requestId });
                        }}>
                        <TableCell className={s.wrap} style={{ verticalAlign: 'top', paddingTop: 10, paddingBottom: 10 }}>
                          <Link href={href({ page: 'request', requestId: r.requestId })} className={t.id}>{r.requestId.slice(0, 8)}</Link>
                          {r.adoLink && <> <Link href={r.adoLink} target="_blank" rel="noopener noreferrer" aria-label="ADO work item"><Open16Regular /></Link></>}
                          <Body1 block><Breakable text={r.domainId} /></Body1>
                          <Caption1 block className={s.muted}>{r.resource ? `${r.accessLevel} on ${r.resource}` : 'v1 request (read-only)'}</Caption1>
                        </TableCell>
                        <TableCell className={s.wrap} style={{ verticalAlign: 'top', paddingTop: 10 }}><Caption1><Breakable text={r.requester} /></Caption1></TableCell>
                        <TableCell style={{ verticalAlign: 'top', paddingTop: 10 }}>
                          <Badge appearance="filled" color={statusColor[r.status]}>{r.currentStage}</Badge>
                          <JourneyDots steps={journey(r.status, r.done)} />
                        </TableCell>
                        <TableCell className={s.wrap} style={{ verticalAlign: 'top', paddingTop: 10 }}>
                          <Body1Strong block>{r.waitingOnLabel || '—'}</Body1Strong>
                          <Caption1 block className={s.muted}>{r.next || '—'}</Caption1>
                        </TableCell>
                        <TableCell style={{ verticalAlign: 'top', paddingTop: 10 }}>
                          {long
                            ? <span className={t.long} title="Waiting on a person for more than 24 hours"><Clock16Regular aria-hidden="true" />{r.timeInStage}</span>
                            : r.timeInStage}
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </div>
          )}
        </>
      ) : null}
    </div>
  );
}

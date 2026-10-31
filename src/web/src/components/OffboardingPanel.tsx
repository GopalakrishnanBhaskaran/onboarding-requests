import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Checkbox, MessageBar, MessageBarBody, MessageBarTitle, Subtitle2,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { ApiError, api } from '../lib/api';
import { canConfirm, canPlan, changes, inProgress, planExpired, switchedOff } from '../lib/offboarding';
import type { OffboardingView, ProjectSummary } from '../lib/types';
import { ErrorBar, useStyles } from './common';

const STATUS: Record<OffboardingView['status'], string> = {
  Planning: 'Planning (the AI agent reads Entra and Azure)',
  Planned: 'Planned — nothing changed yet',
  Executing: 'Executing',
  Completed: 'Completed — verified, PMO informed',
  PartiallyCompleted: 'Partially completed — something is left',
  Failed: 'Failed',
};

/**
 * v1.1 F5: "Offboard user" on the request page (PMO and Operators). Plan (dry run, shown in full) → explicit confirm (a
 * second person in a production tenant) → the AI agent executes, verifies and audits; only the PMO is mailed, and only
 * when nothing is left. The page refreshes itself while the worker plans or executes (every 5 minutes).
 */
export function OffboardingPanel({ project, enterpriseId }: { project: ProjectSummary; enterpriseId: string }) {
  const s = useStyles();
  const [run, setRun] = useState<OffboardingView | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [checked, setChecked] = useState(false);

  const load = useCallback(() => {
    api.offboarding(project.projectId, enterpriseId).then(setRun).catch((e) => {
      if (e instanceof ApiError && e.status === 404) setRun(null); else setError(e);
    });
  }, [project.projectId, enterpriseId]);
  useEffect(load, [load]);
  useEffect(() => {
    if (!inProgress(run)) return undefined;
    const t = window.setInterval(load, 15000);
    return () => window.clearInterval(t);
  }, [run, load]);

  const act = async (call: () => Promise<OffboardingView>) => {
    setBusy(true); setError(null);
    try {
      setRun(await call()); setChecked(false);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className={s.section} aria-label="Offboarding">
      <div className={s.row}>
        <Subtitle2 as="h2">Offboarding — {enterpriseId}</Subtitle2>
        {run && <Badge color={run.status === 'Completed' ? 'success' : run.status === 'Planned' ? 'brand' : run.status === 'PartiallyCompleted' || run.status === 'Failed' ? 'danger' : 'informative'}>{STATUS[run.status]}</Badge>}
      </div>
      <Caption1>
        Removes this person&apos;s access in Entra and Azure: sessions, Temporary Access Pass, group memberships, Azure role assignments, then disables the
        account (and deletes it, restorable for 30 days, when account deletion is switched on). You see the full plan first; nothing changes until it is
        confirmed.
      </Caption1>
      {switchedOff(error) ? (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Offboarding changes are switched off</MessageBarTitle>
            Nothing was changed. Use the plan below as the checklist: the ID team and the role assignment team remove this access by hand.
          </MessageBarBody>
        </MessageBar>
      ) : <ErrorBar error={error} />}
      {canPlan(run, planExpired(error)) && (
        <div><Button appearance="primary" disabled={busy} onClick={() => void act(() => api.planOffboarding(project.projectId, enterpriseId))}>
          {planExpired(error) ? 'Make a new plan' : 'Offboard user'}
        </Button></div>
      )}
      {inProgress(run) && <Body1 role="status">The AI agent works on it within about 5 minutes; this panel refreshes itself.</Body1>}
      {run && run.steps.length > 0 && (
        <div className={s.tableWrap}>
          <Table aria-label="Offboarding steps" size="small">
            <TableHeader><TableRow>
              <TableHeaderCell>Step</TableHeaderCell><TableHeaderCell>What</TableHeaderCell><TableHeaderCell>Result</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {run.steps.map((st, i) => (
                <TableRow key={i}>
                  <TableCell>{st.name}</TableCell>
                  <TableCell><span className={s.wrap}>{st.detail}</span><Caption1 block className={s.wrap}>{st.target}</Caption1></TableCell>
                  <TableCell><Badge appearance="outline" color={st.result === 'Failed' ? 'danger' : st.result === 'Done' ? 'success' : st.result === 'Planned' ? 'brand' : 'subtle'}>{st.result}</Badge></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {run && run.leftovers.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <b>Left for people</b> (an escalation was raised; the PMO was not told it is complete):
            <ul>{run.leftovers.map((l) => <li key={l}>{l}</li>)}</ul>
          </MessageBarBody>
        </MessageBar>
      )}
      {run && run.notCovered.length > 0 && (
        <Caption1>Not covered: {run.notCovered.join(' · ')}</Caption1>
      )}
      {run?.status === 'Planned' && !planExpired(error) && (
        <div className={s.row}>
          <Checkbox label={`I checked the plan: ${changes(run)} change(s) for ${run.enterpriseId}`} checked={checked} onChange={(_, d) => setChecked(!!d.checked)} />
          <Button appearance="primary" disabled={busy || !canConfirm(run, checked)}
            onClick={() => void act(() => api.confirmOffboarding(project.projectId, enterpriseId, run.offboardingId))}>Confirm offboarding</Button>
          <Button disabled={busy} onClick={() => void act(() => api.planOffboarding(project.projectId, enterpriseId, true))}>Make a new plan</Button>
          <Caption1 block>
            Plans keep the settings and the Entra state of the moment they were made. After a change (for example account deletion switched on), make a
            new plan: this one is closed unconfirmed and the AI agent reads Entra and Azure again.
          </Caption1>
        </div>
      )}
      {run && (run.status === 'Completed' || run.status === 'PartiallyCompleted') && (
        <Caption1>PMO notified: {run.pmoNotified ? 'yes' : 'no'} · reference {run.correlationId}</Caption1>
      )}
    </section>
  );
}

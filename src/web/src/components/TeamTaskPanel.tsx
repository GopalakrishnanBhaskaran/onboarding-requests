import { useState } from 'react';
import { Badge, Body1, Button, Caption1, Checkbox, Field, Input, MessageBar, MessageBarBody, Subtitle2, Textarea } from '@fluentui/react-components';
import { api } from '../lib/api';
import type { AccessRequest, RequestView, TeamTask } from '../lib/types';
import { checkColor, checkLabel, dateTime, grantsOf, localInput, taskLabel } from '../lib/format';
import { ErrorBar, useStyles } from './common';

/**
 * A task the AI agent left to the team can go back to it, picked up or not (e.g. after privileged rows were switched
 * on or the mapping row was corrected). Not an approval: the agent runs every check again and may leave it again.
 */
export function HandBack({ request, team, onDone }: { request: AccessRequest; team: 'id-team' | 'role-team'; onDone: (v: RequestView) => void }) {
  const s = useStyles();
  const task = team === 'id-team' ? request.flow?.idTeamTask : request.flow?.roleTeamTask;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  if (!task?.agent?.manual || task.state !== 'Open') return null;
  async function handBack() {
    setBusy(true);
    setError(null);
    try {
      onDone(await api.handBack(request.projectId, request.id, team));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className={s.stack}>
      <ErrorBar error={error} />
      <div className={s.row}>
        <Button onClick={() => void handBack()} disabled={busy}>Hand back to the AI agent</Button>
        <Caption1 className={s.muted}>If the reason above no longer holds. The agent checks everything again on its next run (within about 5 minutes).</Caption1>
      </div>
    </div>
  );
}

/** Task view for both teams: instructions, state, the AI verification and the last difference. */
export function TaskSummary({ task, title }: { task: TeamTask; title: string }) {
  const s = useStyles();
  return (
    <div className={s.stack}>
      <div className={s.row}>
        <Subtitle2 as="h3">{title}</Subtitle2>
        <Badge appearance="outline">{taskLabel[task.state]}</Badge>
        {task.pickedUpBy && <Caption1 className={s.muted}>picked up by {task.pickedUpBy} · {dateTime(task.pickedUpAt)}</Caption1>}
      </div>
      {task.agent?.manual && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>The AI agent left this task to your team: {task.agent.manual}.</MessageBarBody>
        </MessageBar>
      )}
      {task.agent?.refusedSince && !task.agent.completed && !task.agent.manual && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            The AI agent is waiting for its newly granted permission (refused since {dateTime(task.agent.refusedSince)}). It retries every run for up to
            24 hours; a new permission can take that long to reach it. After that this task comes to your team.
          </MessageBarBody>
        </MessageBar>
      )}
      {task.agent?.completed && !task.agent.manual && (
        <Caption1>
          {task.agent.roleAssigned
            ? `Done by the AI agent after both approvals (${dateTime(task.agent.at)}): role assigned and response document updated.`
            : `Done by the AI agent after both approvals (${dateTime(task.agent.at)}): user ${task.agent.userCreated ? 'created' : 'existed'}, group ${task.agent.groupCreated ? 'created' : 'existed'}, membership ${task.agent.memberAdded ? 'added' : 'in place'}.`}
        </Caption1>
      )}
      <Body1 className={s.pre}>{task.instructions}</Body1>
      {task.state === 'Returned' && task.lastDifference && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>Returned by the AI verification ({task.returnCount}×):<br /><span className={s.pre}>{task.lastDifference}</span></MessageBarBody>
        </MessageBar>
      )}
      {task.checks.length > 0 && (
        <ul style={{ margin: 0, paddingLeft: 18 }} aria-label={`${title} verification`}>
          {task.checks.map((c) => (
            <li key={c.code}><Badge size="small" color={checkColor[c.status]}>{checkLabel[c.status]}</Badge> <Caption1>{c.detail}</Caption1></li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * Stage 4: the ID team records what it did. The Temporary Access Pass itself is never entered here: only when it was
 * issued, when it expires, that it is one-time, and who issued it. Give the pass to the user directly.
 */
export function IdTeamForm({ request, onDone }: { request: AccessRequest; onDone: (v: RequestView) => void }) {
  const s = useStyles();
  const scope = request.flow?.scope;
  const now = new Date();
  // v2.1: the AI agent already created the user, group and membership; only the TAP (for a new account) is left.
  const agent = request.flow?.idTeamTask?.agent;
  const byAgent = !!agent?.completed && !agent.manual && agent.membership;
  const [upn, setUpn] = useState(request.domainId ?? '');
  const [group, setGroup] = useState(scope?.entraGroup ?? '');
  const [userCreated, setUserCreated] = useState(true);
  const [groupCreated, setGroupCreated] = useState(false);
  const [added, setAdded] = useState(false);
  const [sheetUpdated, setSheetUpdated] = useState(false);
  const [tapIssued, setTapIssued] = useState(true);
  const [issuedAt, setIssuedAt] = useState(localInput(now));
  const [expiresAt, setExpiresAt] = useState(localInput(new Date(now.getTime() + 60 * 60000)));
  const [oneTime, setOneTime] = useState(true);
  const [issuedBy, setIssuedBy] = useState('');
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function pickUp() {
    setError(null);
    try {
      onDone(await api.pickUp(request.projectId, request.id, 'id-team'));
    } catch (e) {
      setError(e);
    }
  }

  async function record() {
    setBusy(true);
    setError(null);
    try {
      onDone(await api.reportIdTeam(request.projectId, request.id, {
        userPrincipalName: upn.trim(),
        groupName: group.trim(),
        userCreated,
        groupCreated,
        addedToGroup: added,
        mappingSheetUpdated: sheetUpdated,
        tap: tapIssued ? { issuedAt: new Date(issuedAt).toISOString(), expiresAt: new Date(expiresAt).toISOString(), oneTime, issuedBy: issuedBy.trim() || undefined } : null,
        notes: notes.trim() || undefined,
      }));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className={s.stack} onSubmit={(e) => { e.preventDefault(); void record(); }} aria-label="ID team completion">
      <ErrorBar error={error} />
      <HandBack request={request} team="id-team" onDone={onDone} />
      {!request.flow?.idTeamTask?.pickedUpAt && <div><Button onClick={() => void pickUp()}>Pick up task</Button></div>}
      {byAgent ? (
        <Body1>User {request.domainId} and group {scope?.entraGroup} are done by the AI agent. Issue the one-time pass in Entra, then record its metadata here.</Body1>
      ) : (
        <>
          <div className={s.row}>
            <Field label="User ID created or used"><Input value={upn} onChange={(_, d) => setUpn(d.value)} /></Field>
            <Field label="Entra group"><Input value={group} onChange={(_, d) => setGroup(d.value)} /></Field>
          </div>
          <Checkbox checked={userCreated} onChange={(_, d) => setUserCreated(d.checked === true)} label="I created a new account (it needs a one-time pass)" />
          <Checkbox checked={groupCreated} onChange={(_, d) => setGroupCreated(d.checked === true)} label="I created the group" />
          <Checkbox checked={sheetUpdated} onChange={(_, d) => setSheetUpdated(d.checked === true)} label="I updated the mapping sheet (group now exists)" />
          <Checkbox checked={added} onChange={(_, d) => setAdded(d.checked === true)} label="The user is a member of the group" />
        </>
      )}
      <Checkbox checked={tapIssued} onChange={(_, d) => setTapIssued(d.checked === true)} label="I issued a Temporary Access Pass (after the membership)" />
      {tapIssued && (
        <div className={s.row}>
          <Field label="Issued at"><Input type="datetime-local" value={issuedAt} onChange={(_, d) => setIssuedAt(d.value)} /></Field>
          <Field label="Expires at" hint="Shortest lifetime."><Input type="datetime-local" value={expiresAt} onChange={(_, d) => setExpiresAt(d.value)} /></Field>
          <Field label="Issued by (optional)"><Input value={issuedBy} onChange={(_, d) => setIssuedBy(d.value)} /></Field>
          <Checkbox checked={oneTime} onChange={(_, d) => setOneTime(d.checked === true)} label="One-time use" />
        </div>
      )}
      <Caption1>Record metadata only. Give the pass to the user directly, separately from the Enterprise ID — never in mail, tickets, chat or this app.</Caption1>
      <Field label="Notes (optional)"><Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} /></Field>
      <div><Button appearance="primary" type="submit" disabled={busy}>Record completion</Button></div>
    </form>
  );
}

/** Stage 5: the role assignment team records exactly what it assigned; that updates the response document. */
export function RoleTeamForm({ request, onDone }: { request: AccessRequest; onDone: (v: RequestView) => void }) {
  const s = useStyles();
  const scope = request.flow?.scope;
  const approved = grantsOf(scope);
  // One line per role and scope, prefilled with what was approved (the team edits it to what it really did).
  const [lines, setLines] = useState(approved.length > 0
    ? approved.map((g) => `${g.roleName} | ${g.scope ?? g.resourceName ?? ''}`).join('\n')
    : `${scope?.role ?? ''}${scope?.roleScope ? ` | ${scope.roleScope}` : ''}`);
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function pickUp() {
    setError(null);
    try {
      onDone(await api.pickUp(request.projectId, request.id, 'role-team'));
    } catch (e) {
      setError(e);
    }
  }

  async function record() {
    setBusy(true);
    setError(null);
    try {
      const roles = lines.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
        const [role = '', at = ''] = l.split('|').map((x) => x.trim());
        return { role, scope: at || null };
      });
      onDone(await api.reportRoleTeam(request.projectId, request.id, roles, notes.trim()));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className={s.stack} onSubmit={(e) => { e.preventDefault(); void record(); }} aria-label="Role assignment completion">
      <ErrorBar error={error} />
      <HandBack request={request} team="role-team" onDone={onDone} />
      {!request.flow?.roleTeamTask?.pickedUpAt && <div><Button onClick={() => void pickUp()}>Pick up task</Button></div>}
      <Field label="Roles in place (one per line: role | scope)" hint="Prefilled with the approved roles; change it to exactly what is in place.">
        <Textarea value={lines} onChange={(_, d) => setLines(d.value)} rows={Math.max(2, approved.length)} resize="vertical" />
      </Field>
      <Caption1>List exactly what is in place. The AI verification checks it matches the approved roles: no extra roles, none missing.</Caption1>
      <Field label="Notes (optional)"><Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} /></Field>
      <div><Button appearance="primary" type="submit" disabled={busy || lines.trim() === ''}>Record completion</Button></div>
    </form>
  );
}

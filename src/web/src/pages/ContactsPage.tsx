import { useCallback, useEffect, useState } from 'react';
import {
  Button, Caption1, Field, Input, MessageBar, MessageBarBody, MessageBarTitle, Subtitle2, Textarea,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import type { ContactInventory, ContactInventoryInput, ProjectSummary } from '../lib/types';
import { MAX_PER_LIST, joinList, normalizeMail, parseList } from '../lib/contacts';
import { dateTime } from '../lib/format';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

type ListKey = 'security' | 'idTeam' | 'roleTeam' | 'pmo' | 'operators';
type OneKey = 'securityManager' | 'idTeamManager' | 'roleTeamManager' | 'ownerManagerFallback';

/** Each team list: what it is called, whether a request needs it, and which mails it gets. */
const LISTS: { key: ListKey; label: string; required: boolean; gets: string }[] = [
  { key: 'security', label: 'Security', required: true, gets: 'Security approval requests, security alerts, what the AI agent created, the completion mail, the daily summary.' },
  { key: 'idTeam', label: 'ID team', required: true, gets: 'ID team tasks (user, group, Temporary Access Pass), returned tasks, the completion mail.' },
  { key: 'roleTeam', label: 'Role assignment team', required: true, gets: 'Role assignment tasks, returned tasks, the completion mail.' },
  { key: 'pmo', label: 'PMO', required: false, gets: 'Returned requests, the completion mail, the daily summary, contact inventory changes.' },
  { key: 'operators', label: 'Operators', required: false, gets: 'Developer reports, stuck-request reminders, the daily summary.' },
];

const ONES: { key: OneKey; label: string }[] = [
  { key: 'securityManager', label: 'Security manager' },
  { key: 'idTeamManager', label: 'ID team manager' },
  { key: 'roleTeamManager', label: 'Role assignment team manager' },
  { key: 'ownerManagerFallback', label: 'Owner’s manager (when Entra has none)' },
];

function formOf(v: ContactInventory): ContactInventoryInput {
  const c = v.contacts;
  return {
    security: joinList(c.security), idTeam: joinList(c.idTeam), roleTeam: joinList(c.roleTeam), pmo: joinList(c.pmo), operators: joinList(c.operators),
    securityManager: c.securityManager ?? '', idTeamManager: c.idTeamManager ?? '', roleTeamManager: c.roleTeamManager ?? '',
    ownerManagerFallback: c.ownerManagerFallback ?? '',
  };
}

/** The problem with one list as typed, or undefined (same rule as the server, which decides). */
function listError(text: string, required: boolean): string | undefined {
  const { valid, invalid } = parseList(text);
  if (invalid.length > 0) return `Not a mail address: ${invalid.join(', ')}`;
  if (valid.length > MAX_PER_LIST) return `At most ${MAX_PER_LIST} addresses (use a mail-enabled group for more).`;
  if (required && valid.length === 0) return 'At least one address: approval requests or team tasks go here.';
  return undefined;
}

const oneError = (text: string) => (text.trim() && !normalizeMail(text) ? 'One mail address.' : undefined);

/**
 * Contact inventory: where every team mail of this tenant goes. One box per team, addresses separated by semicolons.
 * PMO and Operators change it; everyone in the project reads it. It never decides who may approve.
 */
export function ContactsPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [item, setItem] = useState<{ value: ContactInventory; etag: string } | null>(null);
  const [form, setForm] = useState<ContactInventoryInput | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try {
      const v = await api.contacts(project.projectId);
      setItem(v);
      setForm(formOf(v.value));
    } catch (e) {
      setError(e);
    }
  }, [project.projectId]);
  useEffect(() => void load(), [load]);

  if (!item || !form) return <div className={s.stack}><PageTitle>Contact inventory</PageTitle><ErrorBar error={error} />{!error && <Loading />}</div>;

  const v = item.value;
  const edit = v.canEdit;
  const errors = [
    ...LISTS.map((l) => listError(form[l.key], l.required)),
    ...ONES.map((o) => oneError(form[o.key])),
  ];
  const invalid = errors.some(Boolean);
  const changed = JSON.stringify(form) !== JSON.stringify(formOf(v));

  async function save() {
    if (!form || !item) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const next = await api.saveContacts(project.projectId, form, item.etag);
      setItem(next);
      setForm(formOf(next.value));
      setSaved(true);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={s.stack}>
      <PageTitle>Contact inventory</PageTitle>
      <Caption1 className={s.muted}>
        Tenant {project.tenantName || project.name}: where every team mail goes. Separate addresses with a semicolon (;); a paste from Outlook
        (Name &lt;address&gt;; …) works. The requester is mailed at their sign-in name and the joiner at the Enterprise ID, so neither is set here.
        Who decides an approval is not set here either: that stays the app roles.
      </Caption1>
      <ErrorBar error={error} />
      {saved && (
        <MessageBar intent="success">
          <MessageBarBody>Saved as version {v.version}. Security and PMO were mailed what changed; new mails use these addresses from now on.</MessageBarBody>
        </MessageBar>
      )}
      {v.gaps.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Not mailed as things stand</MessageBarTitle>
            {v.gaps.map((g) => <Caption1 key={g} block>{g}</Caption1>)}
          </MessageBarBody>
        </MessageBar>
      )}

      <form
        className={s.section}
        aria-labelledby="teams-h"
        onSubmit={(e) => {
          e.preventDefault();
          if (edit && !invalid && changed) void save();
        }}
      >
        <Subtitle2 as="h2" id="teams-h">Teams</Subtitle2>
        {LISTS.map((l, i) => {
          const count = parseList(form[l.key]).valid.length;
          return (
            <Field
              key={l.key}
              label={`${l.label}${edit ? '' : ` (${count})`}`}
              required={edit && l.required}
              validationMessage={edit ? errors[i] : undefined}
              hint={`${l.gets}${edit ? ` ${count} address${count === 1 ? '' : 'es'}.` : ''}`}
            >
              <Textarea
                value={form[l.key]}
                readOnly={!edit}
                rows={2}
                resize="vertical"
                placeholder={edit ? 'name@company.com; team-mailbox@company.com' : '—'}
                onChange={(_, d) => setForm({ ...form, [l.key]: d.value })}
              />
            </Field>
          );
        })}

        <Subtitle2 as="h2">Escalation (a task or approval waiting past twice its SLA)</Subtitle2>
        <div className={s.row}>
          {ONES.map((o, i) => (
            <Field key={o.key} label={o.label} validationMessage={edit ? errors[LISTS.length + i] : undefined}>
              <Input value={form[o.key]} readOnly={!edit} placeholder={edit ? 'name@company.com' : '—'} onChange={(_, d) => setForm({ ...form, [o.key]: d.value })} />
            </Field>
          ))}
        </div>

        {edit && (
          <div className={s.row}>
            <Button appearance="primary" type="submit" disabled={busy || invalid || !changed}>Save contacts</Button>
            <Button disabled={busy || !changed} onClick={() => setForm(formOf(v))}>Undo changes</Button>
          </div>
        )}
        <Caption1 className={s.muted}>
          {v.version > 0 ? `Version ${v.version} · saved by ${v.updatedBy ?? '—'} on ${dateTime(v.updatedAt)}.` : 'Not saved in the app yet (imported or seeded).'}
          {edit ? ' Saving needs every address to be valid; nothing is saved otherwise.' : ' PMO and Operators can change it.'}
        </Caption1>
      </form>
    </div>
  );
}

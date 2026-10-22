import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Caption1, Checkbox, Field, Input, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import type { AzureRoleGrant, Item, MappingRow, ProjectSummary, ResponseRecord } from '../lib/types';
import { dateTime, scopeName } from '../lib/format';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const blank = {
  rowId: '', resource: '', accessLevel: '', environment: '', entraGroup: '', role: '', roleScope: '', sourceReference: '', privileged: false,
  sensitiveResource: false, toBeCreated: false,
};
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** What PMO can correct on an existing row (Azure roles have their own box below). */
type EditForm = {
  resource: string; accessLevel: string; environment: string; entraGroup: string; groupObjectId: string; role: string; roleScope: string;
  privileged: boolean; sensitiveResource: boolean; toBeCreated: boolean; flag: 'None' | 'Amber' | 'Red'; notes: string;
};

function editFormOf(r: MappingRow): EditForm {
  return {
    resource: r.resource, accessLevel: r.accessLevel, environment: r.environment ?? '', entraGroup: r.entraGroup, groupObjectId: r.groupObjectId ?? '',
    role: r.role, roleScope: r.roleScope ?? '', privileged: r.privileged, sensitiveResource: r.sensitiveResource,
    toBeCreated: r.groupState === 'ToBeCreated', flag: r.flag ?? 'None', notes: r.notes ?? '',
  };
}

/** The row's Azure roles: the list, else the single pair of older rows. */
function grants(r: MappingRow): AzureRoleGrant[] {
  if (r.azureRoles && r.azureRoles.length > 0) return r.azureRoles;
  return r.azureRoleDefinitionId && r.azureScope
    ? [{ roleName: r.azureRoleDefinitionId, roleDefinitionId: r.azureRoleDefinitionId, scope: r.azureScope, resourceName: null, resourceType: null }]
    : [];
}

/** One response-document line per role: "Role name | resource name | Azure scope (optional)". */
export function toLines(list: AzureRoleGrant[]): string {
  return list.map((g) => [g.roleName, g.resourceName ?? '', g.scope ?? ''].join(' | ')).join('\n');
}

export function fromLines(text: string): AzureRoleGrant[] {
  return text.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
    const [role = '', resource = '', scope = ''] = l.split('|').map((x) => x.trim());
    return {
      roleName: role, roleDefinitionId: GUID.test(role) ? role : null, resourceName: resource || (scope ? scopeName(scope) : null),
      scope: scope || null, resourceType: null,
    };
  });
}

/**
 * The two sources of truth. PMO owns the mapping sheet (resource + access level → Entra group → role); the ID team may
 * record a group it created. Every change sends dependent in-flight requests back through the AI review.
 */
export function MappingSheetPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const isPmo = project.roles.includes('PMO');
  const isIdTeam = project.roles.includes('IdTeam');
  const [rows, setRows] = useState<Item<MappingRow>[] | null>(null);
  const [records, setRecords] = useState<Item<ResponseRecord>[] | null>(null);
  const [form, setForm] = useState(blank);
  const [azure, setAzure] = useState({ rowId: '', lines: '' });
  const [editing, setEditing] = useState<{ row: MappingRow; etag: string; form: EditForm } | null>(null);
  const [envFilter, setEnvFilter] = useState('');
  const [error, setError] = useState<unknown>(null);
  const environments = project.environments ?? [];

  const load = useCallback(async () => {
    setError(null);
    try {
      const [m, r] = await Promise.all([api.mappingSheet(project.projectId), api.responseDocument(project.projectId)]);
      setRows(m.items);
      setRecords(r.items);
    } catch (e) {
      setError(e);
    }
  }, [project.projectId]);
  useEffect(() => void load(), [load]);

  async function run(action: () => Promise<unknown>) {
    setError(null);
    try {
      await action();
      await load();
    } catch (e) {
      setError(e);
    }
  }

  const body = (r: MappingRow, change: Partial<MappingRow>) => ({
    resource: r.resource, accessLevel: r.accessLevel, entraGroup: r.entraGroup, role: r.role, roleScope: r.roleScope,
    privileged: r.privileged, sensitiveResource: r.sensitiveResource, deprecated: r.deprecated, groupState: r.groupState,
    sourceReference: r.sourceReference, azureRoleDefinitionId: r.azureRoleDefinitionId, azureScope: r.azureScope,
    azureRoles: r.azureRoles ?? [], environment: r.environment ?? null, groupObjectId: r.groupObjectId ?? null, jobRoles: r.jobRoles ?? null,
    flag: r.flag ?? 'None', notes: r.notes ?? null, ...change,
  });
  const shown = rows?.filter(({ value: r }) => !envFilter || !r.environment || r.environment === envFilter);

  return (
    <div className={s.stack}>
      <PageTitle>Mapping sheet and response document</PageTitle>
      <ErrorBar error={error} />
      {rows === null && !error ? <Loading /> : null}

      <section className={s.section} aria-labelledby="sheet-h">
        <Subtitle2 as="h2" id="sheet-h">PMO-to-Entra group mapping sheet</Subtitle2>
        <Caption1 className={s.muted}>
          Tenant {project.tenantName || project.name} only. Read fresh by every AI review. A change re-runs the review of in-flight requests for that resource.
          Red rows are never acted on; amber rows carry a caveat the approvers see.
        </Caption1>
        {environments.length > 0 && (
          <Field label="Environment">
            <select value={envFilter} onChange={(e) => setEnvFilter(e.target.value)}>
              <option value="">All</option>
              {environments.map((e) => <option key={e} value={e}>{e}</option>)}
            </select>
          </Field>
        )}
        <div className={s.tableWrap}>
          <Table aria-label="Mapping sheet" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Row</TableHeaderCell>
                <TableHeaderCell>Resource / access level / environment</TableHeaderCell>
                <TableHeaderCell>Entra group</TableHeaderCell>
                <TableHeaderCell>Role (scope)</TableHeaderCell>
                <TableHeaderCell>Flags</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
                <TableHeaderCell>Change</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown?.map(({ value: r, etag }) => (
                <TableRow key={r.id}>
                  <TableCell>{r.rowId} v{r.version}<Caption1 block className={s.muted}>{r.sourceReference}</Caption1></TableCell>
                  <TableCell>
                    {r.resource} · {r.accessLevel}{r.environment ? ` · ${r.environment}` : ''}
                    {r.jobRoles && <Caption1 block className={s.muted}>For: {r.jobRoles}</Caption1>}
                  </TableCell>
                  <TableCell>
                    {r.entraGroup} {r.groupState === 'ToBeCreated' && <Badge color="warning">to be created</Badge>}
                    {r.groupObjectId && <Caption1 block className={s.muted}>{r.groupObjectId}</Caption1>}
                  </TableCell>
                  <TableCell>
                    {r.role}{r.roleScope ? ` (${r.roleScope})` : ''}
                    {grants(r).map((g, i) => (
                      <Caption1 key={i} block className={s.muted}>Azure: {g.roleName} on {g.resourceName ?? scopeName(g.scope)}{g.scope ? '' : ' (scope not resolved)'}</Caption1>
                    ))}
                  </TableCell>
                  <TableCell>
                    <div className={s.row}>
                      {r.privileged && <Badge color="danger">privileged</Badge>}
                      {r.sensitiveResource && <Badge color="warning">sensitive</Badge>}
                      {r.deprecated && <Badge color="subtle">deprecated</Badge>}
                      {r.flag === 'Red' && <Badge color="danger">red: needs a decision</Badge>}
                      {r.flag === 'Amber' && <Badge color="warning">amber: caveat</Badge>}
                    </div>
                    {r.notes && r.flag && r.flag !== 'None' && <Caption1 block className={s.pre}>{r.notes}</Caption1>}
                  </TableCell>
                  <TableCell><Caption1>{r.updatedBy} · {dateTime(r.updatedAt)}</Caption1></TableCell>
                  <TableCell>
                    <div className={s.row}>
                      {isPmo && (
                        <Button size="small" appearance="primary" onClick={() => {
                          setEditing({ row: r, etag, form: editFormOf(r) });
                          window.setTimeout(() => document.getElementById('edit-h')?.scrollIntoView({ behavior: 'smooth' }), 0);
                        }}>
                          Edit
                        </Button>
                      )}
                      {isPmo && (
                        <Button size="small" onClick={() => void run(() => api.updateMappingRow(project.projectId, r.rowId, body(r, { deprecated: !r.deprecated }), etag))}>
                          {r.deprecated ? 'Undeprecate' : 'Deprecate'}
                        </Button>
                      )}
                      {isIdTeam && r.groupState === 'ToBeCreated' && (
                        <Button size="small" onClick={() => void run(() => api.updateMappingGroup(project.projectId, r.rowId, r.entraGroup, 'Exists', etag))}>
                          Group created
                        </Button>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      </section>

      {isPmo && editing && (() => {
        const f = editing.form;
        const set = (change: Partial<EditForm>) => setEditing({ ...editing, form: { ...f, ...change } });
        const idError = f.groupObjectId.trim() && !GUID.test(f.groupObjectId.trim())
          ? 'The object id is a GUID, e.g. 00000000-0000-0000-0000-000000000000.' : undefined;
        return (
          <form
            className={s.section}
            aria-labelledby="edit-h"
            onSubmit={(e) => {
              e.preventDefault();
              if (idError) return;
              void run(async () => {
                await api.updateMappingRow(project.projectId, editing.row.rowId, body(editing.row, {
                  resource: f.resource, accessLevel: f.accessLevel, environment: f.environment || null, entraGroup: f.entraGroup,
                  groupObjectId: f.groupObjectId.trim() || null, role: f.role, roleScope: f.roleScope || null, privileged: f.privileged,
                  sensitiveResource: f.sensitiveResource, groupState: f.toBeCreated ? 'ToBeCreated' : 'Exists', flag: f.flag, notes: f.notes || null,
                }), editing.etag);
                setEditing(null);
              });
            }}
          >
            <Subtitle2 as="h2" id="edit-h">Edit row {editing.row.rowId} v{editing.row.version} (PMO)</Subtitle2>
            <Caption1 className={s.muted}>Saving bumps the version: in-flight requests for this resource are reviewed again; a returned request is reviewed when it is resubmitted.</Caption1>
            <div className={s.row}>
              <Field label="Resource" required><Input value={f.resource} onChange={(_, d) => set({ resource: d.value })} /></Field>
              <Field label="Access level" required><Input value={f.accessLevel} onChange={(_, d) => set({ accessLevel: d.value })} /></Field>
              {environments.length > 0 && (
                <Field label="Environment">
                  <select value={f.environment} onChange={(e) => set({ environment: e.target.value })}>
                    <option value="">Every environment</option>
                    {environments.map((e) => <option key={e} value={e}>{e}</option>)}
                  </select>
                </Field>
              )}
            </div>
            <div className={s.row}>
              <Field label="Entra group" required><Input value={f.entraGroup} onChange={(_, d) => set({ entraGroup: d.value })} /></Field>
              <Field label="Group object id" validationMessage={idError}
                hint="This tenant's Entra object id of the group (Entra admin center → Groups → the group → Object ID). Empty = the review matches by name only.">
                <Input value={f.groupObjectId} onChange={(_, d) => set({ groupObjectId: d.value })} />
              </Field>
            </div>
            <div className={s.row}>
              <Field label="Role" required><Input value={f.role} onChange={(_, d) => set({ role: d.value })} /></Field>
              <Field label="Role scope"><Input value={f.roleScope} onChange={(_, d) => set({ roleScope: d.value })} /></Field>
              <Field label="Flag">
                <select value={f.flag} onChange={(e) => set({ flag: e.target.value as EditForm['flag'] })}>
                  <option value="None">None</option>
                  <option value="Amber">Amber: caveat shown to the approvers</option>
                  <option value="Red">Red: never sent for approval</option>
                </select>
              </Field>
            </div>
            <Field label="Notes"><Textarea value={f.notes} onChange={(_, d) => set({ notes: d.value })} rows={2} resize="vertical" /></Field>
            <div className={s.row}>
              <Checkbox label="Privileged" checked={f.privileged} onChange={(_, d) => set({ privileged: d.checked === true })} />
              <Checkbox label="Sensitive resource" checked={f.sensitiveResource} onChange={(_, d) => set({ sensitiveResource: d.checked === true })} />
              <Checkbox label="Group to be created by the ID team" checked={f.toBeCreated} onChange={(_, d) => set({ toBeCreated: d.checked === true })} />
            </div>
            <div className={s.row}>
              <Button appearance="primary" type="submit" disabled={Boolean(idError)}>Save row</Button>
              <Button onClick={() => setEditing(null)}>Cancel</Button>
            </div>
          </form>
        );
      })()}

      {isPmo && (
        <form
          className={s.section}
          aria-label="Add mapping row"
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              await api.addMappingRow(project.projectId, {
                rowId: form.rowId || null, resource: form.resource, accessLevel: form.accessLevel, environment: form.environment || null,
                entraGroup: form.entraGroup, role: form.role,
                roleScope: form.roleScope || null, sourceReference: form.sourceReference, privileged: form.privileged,
                sensitiveResource: form.sensitiveResource, groupState: form.toBeCreated ? 'ToBeCreated' : 'Exists',
              });
              setForm(blank);
            });
          }}
        >
          <Subtitle2 as="h2">Add a row (PMO)</Subtitle2>
          <div className={s.row}>
            <Field label="Row id (optional)"><Input value={form.rowId} onChange={(_, d) => setForm({ ...form, rowId: d.value })} /></Field>
            <Field label="Resource" required><Input value={form.resource} onChange={(_, d) => setForm({ ...form, resource: d.value })} /></Field>
            <Field label="Access level" required><Input value={form.accessLevel} onChange={(_, d) => setForm({ ...form, accessLevel: d.value })} /></Field>
            {environments.length > 0 && (
              <Field label="Environment">
                <select value={form.environment} onChange={(e) => setForm({ ...form, environment: e.target.value })}>
                  <option value="">Every environment</option>
                  {environments.map((e) => <option key={e} value={e}>{e}</option>)}
                </select>
              </Field>
            )}
          </div>
          <div className={s.row}>
            <Field label="Entra group" required><Input value={form.entraGroup} onChange={(_, d) => setForm({ ...form, entraGroup: d.value })} /></Field>
            <Field label="Role" required><Input value={form.role} onChange={(_, d) => setForm({ ...form, role: d.value })} /></Field>
            <Field label="Role scope"><Input value={form.roleScope} onChange={(_, d) => setForm({ ...form, roleScope: d.value })} /></Field>
            <Field label="Source reference" required><Input value={form.sourceReference} onChange={(_, d) => setForm({ ...form, sourceReference: d.value })} /></Field>
          </div>
          <div className={s.row}>
            <Checkbox label="Privileged" checked={form.privileged} onChange={(_, d) => setForm({ ...form, privileged: d.checked === true })} />
            <Checkbox label="Sensitive resource" checked={form.sensitiveResource} onChange={(_, d) => setForm({ ...form, sensitiveResource: d.checked === true })} />
            <Checkbox label="Group to be created by the ID team" checked={form.toBeCreated} onChange={(_, d) => setForm({ ...form, toBeCreated: d.checked === true })} />
          </div>
          <div><Button appearance="primary" type="submit">Add row</Button></div>
        </form>
      )}

      {isPmo && rows && (
        <form
          className={s.section}
          aria-label="Azure role for the AI agent"
          onSubmit={(e) => {
            e.preventDefault();
            const row = rows.find((x) => x.value.rowId === azure.rowId);
            if (!row) return;
            // The list replaces the single role of older rows.
            void run(() => api.updateMappingRow(project.projectId, row.value.rowId, body(row.value, {
              azureRoles: fromLines(azure.lines), azureRoleDefinitionId: null, azureScope: null,
            }), row.etag));
          }}
        >
          <Subtitle2 as="h2">Azure roles the AI agent assigns (PMO)</Subtitle2>
          <Caption1 className={s.muted}>
            The group's Azure roles from the response document, one per line: <code>Role name | resource name | Azure scope</code>, for example
            <code> Reader | rg-data-dev-001 | /subscriptions/&lt;id&gt;/resourceGroups/rg-data-dev-001</code>. Built-in role names
            (Reader, Storage Blob Data Reader/Contributor, Key Vault …) resolve to their ids. After both approvals the AI agent sets up every
            line if all roles and scopes are on its approved allow-list; otherwise the role assignment team does it. Empty clears the list.
          </Caption1>
          <div className={s.row} style={{ alignItems: 'flex-start' }}>
            <Field label="Row" required>
              <select value={azure.rowId} onChange={(e) => {
                const r = rows.find((x) => x.value.rowId === e.target.value)?.value;
                setAzure({ rowId: e.target.value, lines: r ? toLines(grants(r)) : '' });
              }}>
                <option value="">Select a row</option>
                {rows.map(({ value: r }) => <option key={r.rowId} value={r.rowId}>{r.rowId} · {r.resource} · {r.accessLevel}{r.environment ? ` · ${r.environment}` : ''}</option>)}
              </select>
            </Field>
            <Field label="Azure roles (one per line)" style={{ flexGrow: 1, minWidth: 320 }}>
              <Textarea value={azure.lines} onChange={(_, d) => setAzure({ ...azure, lines: d.value })} rows={5} resize="vertical" />
            </Field>
          </div>
          <div><Button type="submit" disabled={azure.rowId === ''}>Save Azure roles</Button></div>
        </form>
      )}

      <section className={s.section} aria-labelledby="resp-h">
        <Subtitle2 as="h2" id="resp-h">Response document</Subtitle2>
        <Caption1 className={s.muted}>Role assignment records. The agent records what was reviewed and approved; the role assignment team records what it assigned.</Caption1>
        <div className={s.tableWrap}>
          <Table aria-label="Response document" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>User</TableHeaderCell>
                <TableHeaderCell>Resource / level</TableHeaderCell>
                <TableHeaderCell>Group / role</TableHeaderCell>
                <TableHeaderCell>State</TableHeaderCell>
                <TableHeaderCell>Recorded roles</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {records?.map(({ value: x }) => (
                <TableRow key={x.id}>
                  <TableCell>{x.userId}</TableCell>
                  <TableCell>{x.resource} · {x.accessLevel}{x.environment ? ` · ${x.environment}` : ''}</TableCell>
                  <TableCell>{x.entraGroup} · {x.role}</TableCell>
                  <TableCell><Badge appearance="outline">{x.state}</Badge></TableCell>
                  <TableCell>{x.assignedRoles.map((a) => a.scope ? `${a.role} (${a.scope})` : a.role).join(', ') || '—'}{x.assignedBy ? ` · ${x.assignedBy}` : ''}</TableCell>
                  <TableCell><Caption1>{dateTime(x.updatedAt)}</Caption1></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      </section>
    </div>
  );
}

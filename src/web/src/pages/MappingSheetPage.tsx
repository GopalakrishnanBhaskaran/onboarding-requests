import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Caption1, Checkbox, Field, Input, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import type { Item, MappingRow, ProjectSummary, ResponseRecord } from '../lib/types';
import { dateTime } from '../lib/format';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const blank = { rowId: '', resource: '', accessLevel: '', entraGroup: '', role: '', roleScope: '', sourceReference: '', privileged: false, sensitiveResource: false, toBeCreated: false };

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
  const [error, setError] = useState<unknown>(null);

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
    sourceReference: r.sourceReference, ...change,
  });

  return (
    <div className={s.stack}>
      <PageTitle>Mapping sheet and response document</PageTitle>
      <ErrorBar error={error} />
      {rows === null && !error ? <Loading /> : null}

      <section className={s.section} aria-labelledby="sheet-h">
        <Subtitle2 as="h2" id="sheet-h">PMO-to-Entra group mapping sheet</Subtitle2>
        <Caption1 className={s.muted}>Read fresh by every AI review. A change re-runs the review of in-flight requests for that resource.</Caption1>
        <div className={s.tableWrap}>
          <Table aria-label="Mapping sheet" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Row</TableHeaderCell>
                <TableHeaderCell>Resource / access level</TableHeaderCell>
                <TableHeaderCell>Entra group</TableHeaderCell>
                <TableHeaderCell>Role (scope)</TableHeaderCell>
                <TableHeaderCell>Flags</TableHeaderCell>
                <TableHeaderCell>Updated</TableHeaderCell>
                <TableHeaderCell>Change</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows?.map(({ value: r, etag }) => (
                <TableRow key={r.id}>
                  <TableCell>{r.rowId} v{r.version}<Caption1 block className={s.muted}>{r.sourceReference}</Caption1></TableCell>
                  <TableCell>{r.resource} · {r.accessLevel}</TableCell>
                  <TableCell>{r.entraGroup} {r.groupState === 'ToBeCreated' && <Badge color="warning">to be created</Badge>}</TableCell>
                  <TableCell>{r.role}{r.roleScope ? ` (${r.roleScope})` : ''}</TableCell>
                  <TableCell>
                    <div className={s.row}>
                      {r.privileged && <Badge color="danger">privileged</Badge>}
                      {r.sensitiveResource && <Badge color="warning">sensitive</Badge>}
                      {r.deprecated && <Badge color="subtle">deprecated</Badge>}
                    </div>
                  </TableCell>
                  <TableCell><Caption1>{r.updatedBy} · {dateTime(r.updatedAt)}</Caption1></TableCell>
                  <TableCell>
                    <div className={s.row}>
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

      {isPmo && (
        <form
          className={s.section}
          aria-label="Add mapping row"
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              await api.addMappingRow(project.projectId, {
                rowId: form.rowId || null, resource: form.resource, accessLevel: form.accessLevel, entraGroup: form.entraGroup, role: form.role,
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
                  <TableCell>{x.resource} · {x.accessLevel}</TableCell>
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

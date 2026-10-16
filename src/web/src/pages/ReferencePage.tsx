import { useCallback, useEffect, useState } from 'react';
import {
  Badge, Button, Dropdown, Field, Input, Link, Option, Subtitle2, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { api } from '../lib/api';
import type { Item, ProjectSummary, PublicationStatus, RequirementRule, RoleMapping } from '../lib/types';
import { fieldLabel } from '../lib/format';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const RULE_KINDS = ['Required', 'MinLength', 'MaxDurationDays', 'AllowedValues'];

/** Maintainer screen for SAMPLE requirement rules and role mappings. Server enforces the Maintainer role. */
export function ReferencePage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const canManage = project.roles.includes('Maintainer');
  const [rules, setRules] = useState<Item<RequirementRule>[] | null>(null);
  const [mappings, setMappings] = useState<Item<RoleMapping>[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [r, m] = await Promise.all([api.listRules(project.projectId), api.listMappings(project.projectId)]);
      setRules(r.items);
      setMappings(m.items);
    } catch (e) {
      setError(e);
    }
  }, [project.projectId]);
  useEffect(() => void load(), [load]);

  async function transition(kind: 'rule' | 'mapping', id: string, status: PublicationStatus, etag: string) {
    setError(null);
    try {
      if (kind === 'rule') await api.setRuleStatus(project.projectId, id, status, etag);
      else await api.setMappingStatus(project.projectId, id, status, etag);
      await load();
    } catch (e) {
      setError(e);
    }
  }

  const actions = (kind: 'rule' | 'mapping', id: string, status: PublicationStatus, etag: string) =>
    !canManage ? null : status === 'Draft' ? (
      <Button size="small" onClick={() => void transition(kind, id, 'Published', etag)}>Publish</Button>
    ) : status === 'Published' ? (
      <Button size="small" onClick={() => void transition(kind, id, 'Retired', etag)}>Retire</Button>
    ) : null;

  return (
    <div className={s.stack}>
      <PageTitle>Reference data (SAMPLE)</PageTitle>
      <ErrorBar error={error} />
      {rules === null && !error ? <Loading /> : null}

      <section className={s.section} aria-labelledby="rules-h">
        <Subtitle2 as="h2" id="rules-h">Requirement rules</Subtitle2>
        <div className={s.tableWrap}>
          <Table aria-label="Requirement rules" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Rule</TableHeaderCell>
                <TableHeaderCell>Type / field</TableHeaderCell>
                <TableHeaderCell>Requirement</TableHeaderCell>
                <TableHeaderCell>Effective</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Action</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rules?.map(({ value: r, etag }) => (
                <TableRow key={r.id}>
                  <TableCell>{r.ruleId} v{r.version}</TableCell>
                  <TableCell>{r.requestType} · {fieldLabel[r.field] ?? r.field} · {r.rule}{r.parameter ? ` (${r.parameter})` : ''}</TableCell>
                  <TableCell>{r.requirementText}</TableCell>
                  <TableCell>{r.effectiveFrom} → {r.effectiveTo ?? 'open'}</TableCell>
                  <TableCell><Badge appearance="outline">{r.publicationStatus}</Badge></TableCell>
                  <TableCell>{actions('rule', r.id, r.publicationStatus, etag)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
        {canManage && <RuleDraftForm project={project} onCreated={load} onError={setError} />}
      </section>

      <section className={s.section} aria-labelledby="maps-h">
        <Subtitle2 as="h2" id="maps-h">Role mappings (reference only, nothing is assigned)</Subtitle2>
        <div className={s.tableWrap}>
          <Table aria-label="Role mappings" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Mapping</TableHeaderCell>
                <TableHeaderCell>Business role</TableHeaderCell>
                <TableHeaderCell>Environment</TableHeaderCell>
                <TableHeaderCell>Group reference</TableHeaderCell>
                <TableHeaderCell>Entra access package</TableHeaderCell>
                <TableHeaderCell>Effective</TableHeaderCell>
                <TableHeaderCell>Status</TableHeaderCell>
                <TableHeaderCell>Action</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {mappings?.map(({ value: m, etag }) => (
                <TableRow key={m.id}>
                  <TableCell>{m.mappingId} v{m.version}</TableCell>
                  <TableCell>{m.businessRole}</TableCell>
                  <TableCell>{m.environment}</TableCell>
                  <TableCell>{m.groupReference}</TableCell>
                  <TableCell>
                    {m.myAccessLink
                      ? <Link href={m.myAccessLink} target="_blank" rel="noopener noreferrer">{m.accessPackageId?.slice(0, 8)}…</Link>
                      : '—'}
                  </TableCell>
                  <TableCell>{m.effectiveFrom} → {m.effectiveTo ?? 'open'}</TableCell>
                  <TableCell><Badge appearance="outline">{m.publicationStatus}</Badge></TableCell>
                  <TableCell>{actions('mapping', m.id, m.publicationStatus, etag)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
        {canManage && <MappingDraftForm project={project} onCreated={load} onError={setError} />}
      </section>
    </div>
  );
}

type FormProps = { project: ProjectSummary; onCreated: () => Promise<void>; onError: (e: unknown) => void };

function RuleDraftForm({ project, onCreated, onError }: FormProps) {
  const s = useStyles();
  const [f, setF] = useState({ ruleId: '', requestType: project.supportedRequestTypes[0] ?? '', field: 'role', rule: 'Required', parameter: '', requirementText: 'SAMPLE: ', effectiveFrom: '', sourceReference: 'SAMPLE-' });
  const set = (k: keyof typeof f) => (v: string) => setF((x) => ({ ...x, [k]: v }));

  async function submit() {
    try {
      await api.createRule(project.projectId, { ...f, parameter: f.parameter || null, effectiveFrom: f.effectiveFrom || null });
      await onCreated();
    } catch (e) {
      onError(e);
    }
  }

  return (
    <form className={s.row} onSubmit={(e) => { e.preventDefault(); void submit(); }} aria-label="New draft rule">
      <Field label="Rule id"><Input value={f.ruleId} onChange={(_, d) => set('ruleId')(d.value)} /></Field>
      <Field label="Request type"><Input value={f.requestType} onChange={(_, d) => set('requestType')(d.value)} /></Field>
      <Field label="Field">
        <Dropdown value={fieldLabel[f.field]} selectedOptions={[f.field]} onOptionSelect={(_, d) => set('field')(d.optionValue ?? 'role')}>
          {Object.entries(fieldLabel).map(([k, v]) => <Option key={k} value={k}>{v}</Option>)}
        </Dropdown>
      </Field>
      <Field label="Rule">
        <Dropdown value={f.rule} selectedOptions={[f.rule]} onOptionSelect={(_, d) => set('rule')(d.optionValue ?? 'Required')}>
          {RULE_KINDS.map((k) => <Option key={k} value={k}>{k}</Option>)}
        </Dropdown>
      </Field>
      <Field label="Parameter"><Input value={f.parameter} onChange={(_, d) => set('parameter')(d.value)} /></Field>
      <Field label="Requirement text"><Input value={f.requirementText} onChange={(_, d) => set('requirementText')(d.value)} /></Field>
      <Field label="Effective from"><Input type="date" value={f.effectiveFrom} onChange={(_, d) => set('effectiveFrom')(d.value)} /></Field>
      <Field label="Source reference"><Input value={f.sourceReference} onChange={(_, d) => set('sourceReference')(d.value)} /></Field>
      <Button type="submit">Create draft rule</Button>
    </form>
  );
}

function MappingDraftForm({ project, onCreated, onError }: FormProps) {
  const s = useStyles();
  const [f, setF] = useState({ mappingId: '', businessRole: 'SAMPLE ', environment: project.supportedEnvironments[0] ?? '', groupReference: 'SAMPLE-GRP-', myAccessLink: '', effectiveFrom: '', sourceReference: 'SAMPLE-' });
  const set = (k: keyof typeof f) => (v: string) => setF((x) => ({ ...x, [k]: v }));

  async function submit() {
    try {
      await api.createMapping(project.projectId, { ...f, myAccessLink: f.myAccessLink || null, effectiveFrom: f.effectiveFrom || null });
      await onCreated();
    } catch (e) {
      onError(e);
    }
  }

  return (
    <form className={s.row} onSubmit={(e) => { e.preventDefault(); void submit(); }} aria-label="New draft mapping">
      <Field label="Mapping id"><Input value={f.mappingId} onChange={(_, d) => set('mappingId')(d.value)} /></Field>
      <Field label="Business role"><Input value={f.businessRole} onChange={(_, d) => set('businessRole')(d.value)} /></Field>
      <Field label="Environment"><Input value={f.environment} onChange={(_, d) => set('environment')(d.value)} /></Field>
      <Field label="Group reference"><Input value={f.groupReference} onChange={(_, d) => set('groupReference')(d.value)} /></Field>
      <Field label="My Access portal link (optional)" hint="From the Entra access package overview. Enables the Entra hand-off.">
        <Input value={f.myAccessLink} onChange={(_, d) => set('myAccessLink')(d.value)} placeholder="https://myaccess.microsoft.com/@tenant#/access-packages/…" />
      </Field>
      <Field label="Effective from"><Input type="date" value={f.effectiveFrom} onChange={(_, d) => set('effectiveFrom')(d.value)} /></Field>
      <Field label="Source reference"><Input value={f.sourceReference} onChange={(_, d) => set('sourceReference')(d.value)} /></Field>
      <Button type="submit">Create draft mapping</Button>
    </form>
  );
}

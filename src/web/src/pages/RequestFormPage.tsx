import { useEffect, useState } from 'react';
import {
  Body1, Button, Caption1, Checkbox, Field, Input, MessageBar, MessageBarBody, MessageBarTitle, Select, Subtitle2, Textarea,
} from '@fluentui/react-components';
import { api, ApiError } from '../lib/api';
import { navigate } from '../lib/router';
import type { IntakeFields, MappingRow, ProjectSummary } from '../lib/types';
import { FLOW_OVERVIEW, TEMPLATE_PLACEHOLDER, justificationTemplate, whoDoesWhat } from '../lib/flow';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

type FormFields = { [K in keyof IntakeFields]-?: NonNullable<IntakeFields[K]> };

const empty: FormFields = {
  enterpriseId: '', firstName: '', lastName: '', resource: '', accessLevel: '', environment: '', justification: '', owner: '', guest: false,
};
const SIGN_IN = /^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;
/** Same rule as the API: the Enterprise ID is the joiner's enterprise mail address. */
const ENTERPRISE_MAIL = /^[A-Za-z0-9][A-Za-z0-9._'+-]{0,63}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;

/** Stage 1: intake. The flow is stated upfront. A returned request is corrected here and submitted again. */
export function RequestFormPage({ project, projects = [project], onProject, requestId, agent = false, tap = false }: {
  project: ProjectSummary;
  /** Tenants (one project each) the user may request in; switching resets resource, access level and environment. */
  projects?: ProjectSummary[];
  onProject?: (projectId: string) => void;
  requestId?: string;
  agent?: boolean;
  tap?: boolean;
}) {
  const s = useStyles();
  const [fields, setFields] = useState<FormFields>(empty);
  const [etag, setEtag] = useState<string | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const [loading, setLoading] = useState(Boolean(requestId));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [missing, setMissing] = useState<string[]>([]);
  const [stale, setStale] = useState(false);
  const [sheet, setSheet] = useState<MappingRow[] | null>(null);
  const [sheetError, setSheetError] = useState(false);
  const [copied, setCopied] = useState(false);
  const environments = project.environments ?? [];
  const tenants = projects.filter((p) => p.roles.includes('Requester') || p.projectId === project.projectId);

  // Resource and access level come only from the tenant's mapping sheet (read fresh): dropdowns, never free text.
  useEffect(() => {
    setSheetError(false);
    api.mappingSheet(project.projectId)
      .then((r) => setSheet(r.items.map((i) => i.value).filter((x) => !x.deprecated)))
      .catch(() => { setSheet([]); setSheetError(true); });
  }, [project.projectId]);
  // Another tenant: its groups, environments and resources differ, so those choices start again.
  useEffect(() => {
    if (!requestId) setFields((f) => ({ ...f, resource: '', accessLevel: '', environment: '' }));
  }, [project.projectId, requestId]);
  const same = (a?: string | null, b?: string | null) => (a ?? '').trim().toLowerCase() === (b ?? '').trim().toLowerCase();
  const inEnv = (row: MappingRow) => !row.environment || !fields.environment || same(row.environment, fields.environment);
  const resources = sheet ? [...new Set(sheet.filter(inEnv).map((x) => x.resource))].sort() : [];
  const levels = sheet ? [...new Set(sheet.filter((x) => inEnv(x) && same(x.resource, fields.resource)).map((x) => x.accessLevel))].sort() : [];
  const derived = fields.enterpriseId
    ? `${fields.enterpriseId.split('@')[0].toLowerCase().replace(/[^a-z0-9._'-]/g, '')}${project.domainIdSuffix ?? (fields.enterpriseId.includes('@') ? '@' + fields.enterpriseId.split('@')[1] : '')}`
    : '';

  useEffect(() => {
    if (!requestId) return;
    api.getRequest(project.projectId, requestId)
      .then(({ request }) => {
        const v = request.value;
        setFields({
          enterpriseId: v.enterpriseId ?? '', firstName: v.firstName ?? '', lastName: v.lastName ?? '', resource: v.resource ?? '',
          accessLevel: v.accessLevel ?? '', environment: v.environment ?? '', justification: v.justification ?? '', owner: v.owner ?? '',
          guest: v.guest ?? false,
        });
        setEtag(request.etag);
        setReason(v.flow?.lastReason ?? null);
      })
      .catch(setError)
      .finally(() => setLoading(false));
  }, [project.projectId, requestId]);

  const set = (key: Exclude<keyof IntakeFields, 'guest'>) => (value: string) => setFields((f) => ({ ...f, [key]: value }));
  const ownerError = fields.owner && !SIGN_IN.test(fields.owner) ? 'Use the Owner’s sign-in name, e.g. owner.name@contoso.com.' : undefined;
  const mailError = fields.enterpriseId && !ENTERPRISE_MAIL.test(fields.enterpriseId.trim())
    ? 'Use the joiner’s enterprise mail address, e.g. jane.doe@company.com.' : undefined;
  const template = justificationTemplate(fields.accessLevel, fields.resource, fields.environment);
  const templateLeft = TEMPLATE_PLACEHOLDER.test(fields.justification)
    ? 'Replace every [ … ] of the template with your own details.' : undefined;
  const invalid = Boolean(ownerError || mailError || templateLeft);

  async function copyTemplate() {
    try {
      await navigator.clipboard.writeText(template);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setFields((f) => ({ ...f, justification: f.justification || template })); // no clipboard: put it in the field
    }
  }

  async function submit() {
    setSaving(true);
    setError(null);
    setMissing([]);
    const body: IntakeFields = Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, v === '' ? null : v]));
    try {
      const view = requestId && etag
        ? await api.resubmit(project.projectId, requestId, body, etag)
        : await api.submit(project.projectId, body);
      navigate({ page: 'request', requestId: view.request.value.id });
    } catch (e) {
      if (e instanceof ApiError && e.status === 412) setStale(true);
      else if (e instanceof ApiError && e.code === 'incomplete_request') setMissing(e.details ?? []);
      else setError(e);
    } finally {
      setSaving(false);
    }
  }

  if (loading) return <Loading />;

  return (
    <div className={s.stack} style={{ maxWidth: 720 }}>
      <PageTitle>{requestId ? 'Correct and resubmit request' : 'New onboarding request'}</PageTitle>

      <section className={s.section} aria-labelledby="flow-h">
        <Subtitle2 as="h2" id="flow-h">How this works</Subtitle2>
        <ul style={{ margin: 0, paddingLeft: 20 }}>
          {FLOW_OVERVIEW.map((step) => <li key={step} style={{ listStyle: 'none' }}><Body1>{step}</Body1></li>)}
        </ul>
        <dl style={{ display: 'grid', gridTemplateColumns: 'minmax(120px, max-content) 1fr', gap: '2px 12px', margin: 0 }}>
          {whoDoesWhat(agent, tap).map(([who, what]) => (
            <div key={who} style={{ display: 'contents' }}>
              <dt><Body1><b>{who}</b></Body1></dt>
              <dd style={{ margin: 0 }}><Body1>{what}</Body1></dd>
            </div>
          ))}
        </dl>
      </section>

      {reason && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody><MessageBarTitle>Returned by the AI review</MessageBarTitle>{reason}</MessageBarBody>
        </MessageBar>
      )}
      {stale && (
        <MessageBar intent="warning" role="alert">
          <MessageBarBody>This request was changed elsewhere. Reload the page to see the latest version.</MessageBarBody>
        </MessageBar>
      )}
      {missing.length > 0 && (
        <MessageBar intent="error" role="alert" layout="multiline">
          <MessageBarBody><MessageBarTitle>The request is incomplete</MessageBarTitle>Missing: {missing.join(', ')}.</MessageBarBody>
        </MessageBar>
      )}
      <ErrorBar error={error} />

      <form
        className={s.stack}
        onSubmit={(e) => {
          e.preventDefault();
          if (!invalid) void submit();
        }}
      >
        <Field label="Tenant" required hint="Requests, groups and roles never cross tenants: pick the tenant the joiner needs access in.">
          <Select value={project.projectId} disabled={Boolean(requestId) || !onProject || tenants.length < 2}
            onChange={(_, d) => onProject?.(d.value)}>
            {tenants.map((p) => <option key={p.projectId} value={p.projectId}>{p.tenantName || p.name}{p.tenantName && p.tenantName !== p.name ? ` (${p.name})` : ''}</option>)}
          </Select>
        </Field>
        <div className={s.row}>
          <Field label="Enterprise ID (mail)" required validationMessage={mailError}
            hint={derived && !mailError
              ? `${tap ? 'The sign-in details and notifications go' : 'Notifications go'} to this address. Sign-in name to create: ${derived}`
              : `The joiner’s enterprise mail address, e.g. jane.doe@company.com. ${tap ? 'The sign-in details (one-time pass, MFA steps) and notifications go here.' : 'Notifications go here.'}`}>
            <Input type="email" value={fields.enterpriseId} onChange={(_, d) => set('enterpriseId')(d.value)} maxLength={200} autoComplete="off" />
          </Field>
          <Field label="First name" required>
            <Input value={fields.firstName} onChange={(_, d) => set('firstName')(d.value)} maxLength={100} />
          </Field>
          <Field label="Last name" required>
            <Input value={fields.lastName} onChange={(_, d) => set('lastName')(d.value)} maxLength={100} />
          </Field>
        </div>
        {environments.length > 0 && (
          <Field label="Environment" required hint="Groups and roles differ per environment. A missing environment (e.g. Prod with no groups yet) is returned by the AI review.">
            <Select value={fields.environment} onChange={(_, d) => setFields((f) => ({ ...f, environment: d.value, resource: '', accessLevel: '' }))}>
              <option value="">Choose…</option>
              {environments.map((e) => <option key={e} value={e}>{e}</option>)}
            </Select>
          </Field>
        )}
        {sheet !== null && resources.length === 0 && (
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              {sheetError ? 'The mapping sheet could not be read; reload the page.'
                : `The mapping sheet of ${project.tenantName || project.name} has no resources${fields.environment ? ` for ${fields.environment}` : ''} yet. PMO imports it from the group classification and the response document.`}
            </MessageBarBody>
          </MessageBar>
        )}
        <Field label="Resource" required hint="From the PMO mapping sheet of this tenant.">
          <Select value={fields.resource} disabled={resources.length === 0 || (environments.length > 0 && !fields.environment)}
            onChange={(_, d) => setFields((f) => ({ ...f, resource: d.value, accessLevel: '' }))}>
            <option value="">{environments.length > 0 && !fields.environment ? 'Choose the environment first' : 'Choose…'}</option>
            {fields.resource && !resources.includes(fields.resource) && <option value={fields.resource}>{fields.resource} (not in the mapping sheet now)</option>}
            {resources.map((x) => <option key={x} value={x}>{x}</option>)}
          </Select>
        </Field>
        <Field label="Access level" required hint="The levels the mapping sheet has for that resource and environment.">
          <Select value={fields.accessLevel} disabled={!fields.resource} onChange={(_, d) => set('accessLevel')(d.value)}>
            <option value="">{fields.resource ? 'Choose…' : 'Choose the resource first'}</option>
            {fields.accessLevel && !levels.includes(fields.accessLevel) && <option value={fields.accessLevel}>{fields.accessLevel} (not in the mapping sheet now)</option>}
            {levels.map((x) => <option key={x} value={x}>{x}</option>)}
          </Select>
        </Field>
        <section className={s.section} aria-labelledby="template-h">
          <Subtitle2 as="h2" id="template-h">Justification template</Subtitle2>
          <Caption1>Copy or insert it, then replace every [ … ] with the joiner’s details. Never paste passwords or passes.</Caption1>
          <pre className={s.pre} style={{ margin: 0, fontFamily: 'inherit' }}>{template}</pre>
          <div className={s.row}>
            <Button size="small" onClick={() => void copyTemplate()}>{copied ? 'Copied' : 'Copy template'}</Button>
            <Button size="small" onClick={() => setFields((f) => ({ ...f, justification: template }))}>Insert into justification</Button>
          </div>
        </section>
        <Field label="Justification" required validationMessage={templateLeft}
          hint="The business reason, consistent with the access level: role, tasks that need the access, start and duration, reference.">
          <Textarea value={fields.justification} onChange={(_, d) => set('justification')(d.value)} rows={6} resize="vertical" maxLength={2000} />
        </Field>
        <Field label="Owner (sign-in name)" required hint="Only this person’s decision counts at the Owner stage." validationMessage={ownerError}>
          <Input type="email" value={fields.owner} onChange={(_, d) => set('owner')(d.value)} maxLength={200} autoComplete="off" />
        </Field>
        <Checkbox checked={fields.guest} onChange={(_, d) => setFields((f) => ({ ...f, guest: d.checked === true }))} label="Guest (external) user" />
        <div className={s.row}>
          <Button appearance="primary" type="submit" disabled={saving || invalid || stale}>
            {saving ? 'Submitting…' : requestId ? 'Resubmit request' : 'Submit request'}
          </Button>
          <Button onClick={() => navigate(requestId ? { page: 'request', requestId } : { page: 'requests' })}>Cancel</Button>
        </div>
      </form>
    </div>
  );
}

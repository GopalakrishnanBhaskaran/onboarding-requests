import { useEffect, useState } from 'react';
import { Button, Dropdown, Field, Input, MessageBar, MessageBarBody, Option, Textarea } from '@fluentui/react-components';
import { api, ApiError } from '../lib/api';
import { navigate } from '../lib/router';
import type { ProjectSummary, RequestFields } from '../lib/types';
import { ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

const empty: RequestFields = { requestType: '', role: '', environment: '', manager: '', justification: '', startDate: '', endDate: '', joinerUpn: '' };
const UPN = /^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;

/** Minimum fields only. Fields may be left blank; the review reports what is missing. */
export function RequestFormPage({ project, requestId }: { project: ProjectSummary; requestId?: string }) {
  const s = useStyles();
  const [fields, setFields] = useState<RequestFields>(empty);
  const [etag, setEtag] = useState<string | null>(null);
  const [loading, setLoading] = useState(Boolean(requestId));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [stale, setStale] = useState(false);

  useEffect(() => {
    if (!requestId) return;
    api.getRequest(project.projectId, requestId)
      .then(({ value, etag }) => {
        setFields(Object.fromEntries(Object.keys(empty).map((k) => [k, value[k as keyof RequestFields] ?? ''])) as RequestFields);
        setEtag(etag);
      })
      .catch(setError)
      .finally(() => setLoading(false));
  }, [project.projectId, requestId]);

  const set = (key: keyof RequestFields) => (value: string) => setFields((f) => ({ ...f, [key]: value }));
  const text = (key: keyof RequestFields) => (fields[key] as string) ?? '';

  const lengthError = (key: keyof RequestFields, max: number) => (text(key).length > max ? `At most ${max} characters.` : undefined);
  const dateError = fields.startDate && fields.endDate && fields.endDate < fields.startDate ? 'End date is before start date.' : undefined;
  const errors = {
    role: lengthError('role', 200),
    manager: lengthError('manager', 200),
    justification: lengthError('justification', 2000),
    joinerUpn: text('joinerUpn') && !UPN.test(text('joinerUpn')) ? 'Use a sign-in name such as sample.joiner@contoso.onmicrosoft.com.' : undefined,
  };
  const blocked = Object.values(errors).some(Boolean);

  async function save() {
    setSaving(true);
    setError(null);
    const body: RequestFields = Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, v === '' ? null : v]));
    try {
      const saved = requestId && etag
        ? await api.updateRequest(project.projectId, requestId, body, etag)
        : await api.createRequest(project.projectId, body);
      navigate({ page: 'request', requestId: saved.value.id });
    } catch (e) {
      if (e instanceof ApiError && e.status === 412) setStale(true);
      else setError(e);
    } finally {
      setSaving(false);
    }
  }

  if (loading) return <Loading />;

  return (
    <form
      className={s.stack}
      style={{ maxWidth: 640 }}
      onSubmit={(e) => {
        e.preventDefault();
        if (!blocked) void save();
      }}
    >
      <PageTitle>{requestId ? 'Edit request' : 'New request'}</PageTitle>
      {stale && (
        <MessageBar intent="warning" role="alert">
          <MessageBarBody>This request was changed elsewhere. Reload the page to see the latest version before saving.</MessageBarBody>
        </MessageBar>
      )}
      <ErrorBar error={error} />

      <Field label="Request type">
        <Dropdown
          placeholder="Select a request type"
          selectedOptions={text('requestType') ? [text('requestType')] : []}
          value={text('requestType')}
          onOptionSelect={(_, d) => set('requestType')(d.optionValue ?? '')}
        >
          {project.supportedRequestTypes.map((t) => (
            <Option key={t} value={t}>{t}</Option>
          ))}
        </Dropdown>
      </Field>
      <Field label="Business role" hint="As written in the project's role mapping, e.g. “SAMPLE Data Engineer”." validationMessage={errors.role}>
        <Input value={text('role')} onChange={(_, d) => set('role')(d.value)} maxLength={200} />
      </Field>
      <Field label="Environment">
        <Dropdown
          placeholder="Select an environment"
          selectedOptions={text('environment') ? [text('environment')] : []}
          value={text('environment')}
          onOptionSelect={(_, d) => set('environment')(d.optionValue ?? '')}
        >
          {project.supportedEnvironments.map((t) => (
            <Option key={t} value={t}>{t}</Option>
          ))}
        </Dropdown>
      </Field>
      <Field
        label="New joiner sign-in name"
        hint="The person who will get access, as their Entra sign-in (UPN). Used only to track their request in Microsoft Entra."
        validationMessage={errors.joinerUpn}
      >
        <Input type="email" value={text('joinerUpn')} onChange={(_, d) => set('joinerUpn')(d.value)} maxLength={200} autoComplete="off" />
      </Field>
      <Field label="Line manager" hint="Name only. No email, phone or employee number." validationMessage={errors.manager}>
        <Input value={text('manager')} onChange={(_, d) => set('manager')(d.value)} maxLength={200} />
      </Field>
      <Field label="Business justification" validationMessage={errors.justification}>
        <Textarea value={text('justification')} onChange={(_, d) => set('justification')(d.value)} rows={4} resize="vertical" />
      </Field>
      <div className={s.row}>
        <Field label="Start date">
          <Input type="date" value={text('startDate')} onChange={(_, d) => set('startDate')(d.value)} />
        </Field>
        <Field label="End date" validationMessage={dateError} validationState={dateError ? 'warning' : 'none'}>
          <Input type="date" value={text('endDate')} onChange={(_, d) => set('endDate')(d.value)} />
        </Field>
      </div>
      <div className={s.row}>
        <Button appearance="primary" type="submit" disabled={saving || blocked || stale}>
          {saving ? 'Saving…' : 'Save request'}
        </Button>
        <Button onClick={() => navigate(requestId ? { page: 'request', requestId } : { page: 'requests' })}>Cancel</Button>
      </div>
    </form>
  );
}

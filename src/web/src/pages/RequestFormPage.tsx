import { useEffect, useState } from 'react';
import {
  Badge, Body1, Body1Strong, Button, Caption1, Checkbox, Field, Input, MessageBar, MessageBarBody, MessageBarTitle, Select, Subtitle2, Switch, Table,
  TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Textarea, makeStyles, tokens,
} from '@fluentui/react-components';
import { Copy16Regular, Send20Regular, TextAdd20Regular } from '@fluentui/react-icons';
import { api, ApiError } from '../lib/api';
import { navigate } from '../lib/router';
import type { IntakeFields, MappingRow, ProjectSummary, ResolveResult, ResolvedAccess, StatusLine } from '../lib/types';
import { nameError } from '../lib/names';
import { ALL_ENVIRONMENTS, NOT_NEEDED, chosenLevels, environmentsFor, levelsByResource, levelsComplete, plan, settleLevels, unionOptions, unresolvedFor, type LevelChoice } from '../lib/access';
import { correctionFor, missingTasks, privilegedJobRows, returnedSiblings } from '../lib/resubmit';
import { canSubmit, forApi, fromDropdown, toDropdown, toggle, type SelectionMode } from '../lib/resources';
import { FLOW_OVERVIEW, TEMPLATE_PLACEHOLDER, justificationTemplate, whoDoesWhat } from '../lib/flow';
import { ErrorBar, Loading, PageHeader, useStyles } from '../components/common';
import { RequestSummary, type ReadinessItem } from '../components/RequestSummary';

type FormFields = { [K in keyof IntakeFields]-?: NonNullable<IntakeFields[K]> };

const empty: FormFields = {
  enterpriseId: '', firstName: '', lastName: '', resource: '', accessLevel: '', environment: '', justification: '', owner: '', guest: false,
};
const STATUS: Record<ResolvedAccess['status'], { label: string; color: 'success' | 'warning' | 'danger' }> = {
  Auto: { label: 'Auto', color: 'success' },
  NeedsHumanApproval: { label: 'Needs human approval', color: 'warning' },
  Unresolved: { label: 'Unresolved — escalated', color: 'danger' },
};
/** Same rule as the API (Environments.IsProduction): Prod and any name that starts with prod or prd. */
const isProduction = (e: string) => /^(prod|prd)/.test(e.replace(/[^A-Za-z0-9]/g, '').toLowerCase()) || /^(production|live)$/.test(e.replace(/[^A-Za-z0-9]/g, '').toLowerCase());
const SIGN_IN = /^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;
/** Same rule as the API: the Enterprise ID is the joiner's enterprise mail address. */
// Dots only between characters, at most 64 characters before '@' (Entra refuses a dot right before '@', F136).
const ENTERPRISE_MAIL = /^(?=[^@]{1,64}@)[A-Za-z0-9][A-Za-z0-9_'+-]*(\.[A-Za-z0-9_'+-]+)*@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$/;

/** Stage 1: intake. The flow is stated upfront. A returned request is corrected here and submitted again. */
export function RequestFormPage({ project, projects = [project], onProject, requestId, agent = false, tap = false, privileged = false }: {
  project: ProjectSummary;
  /** Tenants (one project each) the user may request in; switching resets resource, access level and environment. */
  projects?: ProjectSummary[];
  onProject?: (projectId: string) => void;
  requestId?: string;
  agent?: boolean;
  tap?: boolean;
  privileged?: boolean;
}) {
  const s = useStyles();
  const f = useFormStyles();
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
  // v1.1 F1 (new requests): Resource* options of this tenant, dropdown or checkboxes, the resolved list and the ticked groups.
  const isNew = !requestId;
  // Developer (4 Nov 2026): a simulation runs every stage with nothing created in Entra or Azure; only Developers see it.
  const developer = project.roles.includes('Developer');
  const requester = project.roles.includes('Requester');
  const [simulate, setSimulate] = useState(developer && !requester);
  const [mode, setMode] = useState<SelectionMode>('dropdown');
  const [options, setOptions] = useState<string[] | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  // 7 Nov 2026: the requester picks the access level; the groups of that level come from the mapping sheet (one request each).
  const [resolvedList, setResolvedList] = useState<ResolveResult[]>([]);
  const [optionsByEnv, setOptionsByEnv] = useState<Record<string, string[]>>({});
  const [levelChoice, setLevelChoice] = useState<LevelChoice>({});
  const [resolveError, setResolveError] = useState<unknown>(null);
  // A returned request of a bundle ("All" or several resources): the other returned requests of the same submission.
  const [siblings, setSiblings] = useState<StatusLine[]>([]);
  const [include, setInclude] = useState<string[]>([]);
  // A resubmission (requests) or, for a new request with All environments, the submissions per environment.
  const [partial, setPartial] = useState<{ done: number; failed: { label: string; why: string }[]; created?: boolean } | null>(null);
  const environments = project.environments ?? [];
  const allEnvironments = fields.environment === ALL_ENVIRONMENTS;
  // Live simulations (7 Nov 2026): the server decides per simulation; null means this project's simulations write for real.
  const liveSimulation = project.liveSimulationRefused === null && !fields.guest;
  const chosenEnvironments = environmentsFor(fields.environment, environments);
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
  const inEnv = (row: MappingRow) => !row.environment || !fields.environment || chosenEnvironments.some((e) => same(row.environment, e));
  const resources = sheet ? [...new Set(sheet.filter(inEnv).map((x) => x.resource))].sort() : [];
  const levels = sheet ? [...new Set(sheet.filter((x) => inEnv(x) && same(x.resource, fields.resource)).map((x) => x.accessLevel))].sort() : [];
  const envChosen = environments.length === 0 || Boolean(fields.environment);

  // F1: options come only from this tenant's mapping sheet (filtered by the server); a new environment or tenant starts again.
  useEffect(() => {
    if (!isNew) return;
    setSelected([]);
    setResolvedList([]);
    setLevelChoice({});
    // At once, not when the new options arrive: nothing is picked or resolved under the previous environment (finding 10).
    setOptions(null);
    setOptionsByEnv({});
    setPartial(null);
    if (!envChosen) return;
    let current = true; // a slower answer for an earlier choice never overwrites the latest one
    // One environment, or every one ("All environments"): the resources any of them offers.
    const envs = environments.length === 0 ? [fields.environment] : chosenEnvironments;
    Promise.all(envs.map((e) => api.resourceOptions(project.projectId, e).then((r) => [e, r.options] as const)))
      .then((pairs) => {
        if (!current) return;
        setOptionsByEnv(Object.fromEntries(pairs));
        setOptions(unionOptions(pairs.map(([, o]) => o)));
      })
      .catch((e) => { if (current) { setOptions([]); setOptionsByEnv({}); setResolveError(e); } });
    return () => { current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isNew, project.projectId, fields.environment, envChosen]);
  const chosen = options ? forApi(selected, options) : [];
  useEffect(() => {
    if (!isNew || chosen.length === 0) { setResolvedList([]); setLevelChoice({}); return; }
    setResolveError(null);
    let current = true; // ticking fast sends several resolves: only the answer for the latest selection counts
    // Each environment resolves only the chosen resources it offers.
    const jobs = Object.entries(optionsByEnv)
      .map(([env, opts]) => [env, chosen.filter((c) => opts.includes(c))] as const)
      .filter(([, list]) => list.length > 0);
    Promise.all(jobs.map(([env, list]) => api.resolve(project.projectId, env, list).then((r) => ({ ...r, environment: env, selectedResources: list }))))
      .then((results) => {
        if (!current) return;
        setResolvedList(results);
        // A resource with one level: chosen for the requester; several: they choose (never more access than asked for).
        setLevelChoice((cur) => settleLevels(cur, levelsByResource(results, chosen)));
      })
      .catch((e) => { if (current) setResolveError(e); });
    return () => { current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isNew, project.projectId, chosen.join('|'), Object.keys(optionsByEnv).join('|')]);
  const levelOptions = levelsByResource(resolvedList, chosen);
  const levelsChosen = levelsComplete(levelChoice, levelOptions);
  const submissions = levelsChosen ? plan(resolvedList, levelChoice) : [];
  const tickedAccess = submissions.flatMap((x) => x.access);
  const ticked = submissions.flatMap((x) => x.groups);
  const unresolved = unresolvedFor(resolvedList, levelChoice);
  const envLabel = allEnvironments ? chosenEnvironments.join(', ') : fields.environment;
  // Rows flagged Privileged under a job-role level (e.g. Data Engineer): the AI review asks for the tasks in the justification.
  const chosenRows = (sheet ?? []).filter((r) => inEnv(r) && (isNew
    ? tickedAccess.some((a) => same(a.groupName, r.entraGroup))
    : same(r.resource, fields.resource) && same(r.accessLevel, fields.accessLevel)));
  const privilegedRows = privilegedJobRows(chosenRows);

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
        if (v.bundleId) {
          api.listRequests(project.projectId)
            .then((list) => {
              const others = returnedSiblings(list.items, v);
              setSiblings(others);
              setInclude(others.map((x) => x.requestId));
            })
            .catch(() => setSiblings([]));
        }
      })
      .catch(setError)
      .finally(() => setLoading(false));
  }, [project.projectId, requestId]);

  const set = (key: Exclude<keyof IntakeFields, 'guest'>) => (value: string) => setFields((f) => ({ ...f, [key]: value }));
  const ownerError = fields.owner && !SIGN_IN.test(fields.owner) ? 'Use the Owner’s sign-in name, e.g. owner.name@contoso.com.' : undefined;
  const mailError = fields.enterpriseId && !ENTERPRISE_MAIL.test(fields.enterpriseId.trim())
    ? 'Use the joiner’s enterprise mail address, e.g. jane.doe@company.com.' : undefined;
  const template = isNew
    ? justificationTemplate(chosenLevels(levelChoice).join(', ') || [...new Set(tickedAccess.map((a) => a.accessLevel))].join(', '), chosen.join(', '), envLabel)
    : justificationTemplate(fields.accessLevel, fields.resource, fields.environment);
  const templateLeft = TEMPLATE_PLACEHOLDER.test(fields.justification)
    ? 'Replace every [ … ] of the template with your own details.' : undefined;
  const firstNameError = nameError(fields.firstName);
  const lastNameError = nameError(fields.lastName);
  const invalid = Boolean(ownerError || mailError || templateLeft || firstNameError || lastNameError) || (isNew && !canSubmit(selected, ticked));

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
      if (isNew) {
        // F1: one request per group of the chosen level, each through the whole flow; one submission per environment.
        const created: string[] = [];
        const failed: { label: string; why: string }[] = [];
        for (const x of submissions) {
          try {
            const result = await api.submitBundle(project.projectId, {
              enterpriseId: body.enterpriseId ?? null, firstName: body.firstName ?? null, lastName: body.lastName ?? null,
              environment: environments.length === 0 ? body.environment ?? null : x.environment,
              justification: body.justification ?? null, owner: body.owner ?? null, guest: fields.guest, selectedResources: x.selectedResources, groups: x.groups,
            }, simulate);
            created.push(...result.requests.map((r) => r.requestId));
          } catch (e) {
            // The first one refused: the same details go to every environment, so say it once (nothing was created).
            if (submissions.length === 1 || created.length === 0 && failed.length === 0) throw e;
            failed.push({ label: x.environment || 'request', why: e instanceof Error ? e.message : 'not submitted' });
          }
        }
        if (failed.length > 0) {
          if (created.length === 0) throw new Error(`No request was created: ${failed.map((x) => `${x.label} (${x.why})`).join('; ')}`);
          setPartial({ done: created.length, failed, created: true });
          return;
        }
        navigate(created.length === 1 ? { page: 'request', requestId: created[0] } : { page: 'requests' });
        return;
      }
      const view = requestId && etag
        ? await api.resubmit(project.projectId, requestId, body, etag)
        : await api.submit(project.projectId, body);
      const others = siblings.filter((x) => include.includes(x.requestId));
      if (others.length === 0) {
        navigate({ page: 'request', requestId: view.request.value.id });
        return;
      }
      // The same correction for the other returned requests of the submission; each keeps its own resource and level.
      const failed: { label: string; why: string }[] = [];
      for (const other of others) {
        const label = `${other.resource ?? other.requestId} · ${other.accessLevel ?? '—'}`;
        try {
          const { request } = await api.getRequest(project.projectId, other.requestId);
          await api.resubmit(project.projectId, other.requestId, correctionFor(request.value, body), request.etag);
        } catch (e) {
          failed.push({ label, why: e instanceof Error ? e.message : 'not resubmitted' });
        }
      }
      if (failed.length === 0) {
        navigate({ page: 'requests' });
        return;
      }
      setEtag(null);
      setPartial({ done: 1 + others.length - failed.length, failed });
    } catch (e) {
      if (e instanceof ApiError && e.status === 412) setStale(true);
      else if (e instanceof ApiError && e.code === 'incomplete_request') setMissing(e.details ?? []);
      else setError(e);
    } finally {
      setSaving(false);
    }
  }

  if (loading) return <Loading />;

  const tenantName = project.tenantName || project.name;
  const accessOk = isNew
    ? envChosen && chosen.length > 0 && ticked.length > 0
    : envChosen && Boolean(fields.resource) && Boolean(fields.accessLevel);
  const readiness: ReadinessItem[] = [
    {
      label: 'Joiner details',
      ok: Boolean(fields.enterpriseId.trim() && !mailError && fields.firstName.trim() && fields.lastName.trim() && !firstNameError && !lastNameError),
      hint: 'Enterprise mail address, first and last name.',
    },
    {
      label: 'Access chosen',
      ok: accessOk,
      hint: isNew
        ? `${environments.length > 0 ? 'Environment, resource' : 'Resource'}, then the access level.`
        : 'Resource and access level from the mapping sheet.',
    },
    {
      label: 'Justification written',
      ok: Boolean(fields.justification.trim()) && !templateLeft,
      hint: templateLeft ? 'Some [ … ] of the template are still there.' : 'Role, tasks, start and duration, reference.',
    },
    { label: 'Owner named', ok: Boolean(fields.owner.trim()) && !ownerError, hint: 'The Owner’s sign-in name.' },
  ];
  const ready = readiness.filter((x) => x.ok).length;
  const summaryGroups = isNew
    ? tickedAccess.map((a) => ({ name: a.groupName, level: a.accessLevel }))
    : fields.resource ? [{ name: fields.resource, level: fields.accessLevel || '—' }] : [];
  const length = fields.justification.trim().length;

  return (
    <div className={s.stack}>
      <PageHeader title={requestId ? 'Correct and resubmit request' : 'New onboarding request'}>
        {requestId
          ? 'Fix what the AI review returned, then resubmit. The review runs again on the new revision.'
          : 'Four short steps. The AI review checks the request as soon as you submit; only the people named below decide.'}
      </PageHeader>

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
      {partial && (
        <MessageBar intent="warning" role="alert" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{partial.created
              ? `${partial.done} request${partial.done === 1 ? '' : 's'} created; ${partial.failed.length} environment${partial.failed.length === 1 ? '' : 's'} not submitted`
              : `${partial.done} of ${partial.done + partial.failed.length} requests resubmitted`}</MessageBarTitle>
            {partial.created
              ? `Not submitted: ${partial.failed.map((x) => `${x.label} (${x.why})`).join('; ')}. Start a new request for ${partial.failed.length === 1 ? 'that environment' : 'those environments'}; the others are in the requests list.`
              : `Not resubmitted: ${partial.failed.map((x) => `${x.label} (${x.why})`).join('; ')}. Open each from the requests list to fix it.`}
            <div><Button size="small" onClick={() => navigate({ page: 'requests' })}>Go to requests</Button></div>
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={f.layout}>
        <form
          className={s.stack}
          style={{ minWidth: 0 }}
          onSubmit={(e) => {
            e.preventDefault();
            if (!invalid) void submit();
          }}
        >
          <FormSection n={1} id="who-h" title="Who is joining" done={readiness[0].ok}>
            <Field label="Tenant" required hint="Requests, groups and roles never cross tenants: pick the tenant the joiner needs access in.">
              <Select value={project.projectId} disabled={Boolean(requestId) || !onProject || tenants.length < 2}
                onChange={(_, d) => onProject?.(d.value)}>
                {tenants.map((p) => <option key={p.projectId} value={p.projectId}>{p.tenantName || p.name}{p.tenantName && p.tenantName !== p.name ? ` (${p.name})` : ''}</option>)}
              </Select>
            </Field>
            <Field label="Enterprise ID (mail)" required validationMessage={mailError}
              validationState={derived && !mailError ? 'success' : undefined}
              hint={derived && !mailError
                ? `${tap ? 'The sign-in details and notifications go' : 'Notifications go'} to this address. Sign-in name to create: ${derived}`
                : `The joiner’s enterprise mail address, e.g. jane.doe@company.com. ${tap ? 'The sign-in details (one-time pass, MFA steps) and notifications go here.' : 'Notifications go here.'}`}>
              <Input type="email" value={fields.enterpriseId} onChange={(_, d) => set('enterpriseId')(d.value)} maxLength={200} autoComplete="off" />
            </Field>
            <div className={f.two}>
              <Field label="First name" required validationMessage={firstNameError}>
                <Input value={fields.firstName} onChange={(_, d) => set('firstName')(d.value)} maxLength={100} />
              </Field>
              <Field label="Last name" required validationMessage={lastNameError}>
                <Input value={fields.lastName} onChange={(_, d) => set('lastName')(d.value)} maxLength={100} />
              </Field>
            </div>
            <Checkbox checked={fields.guest} onChange={(_, d) => setFields((x) => ({ ...x, guest: d.checked === true }))} label="Guest (external) user" />
          </FormSection>

          <FormSection n={2} id="access-h" title="What access they need" done={readiness[1].ok}>
            {environments.length > 0 && (
              <Field label="Environment" required hint={allEnvironments
                ? `One submission per environment (${environments.join(', ')}), each with its own groups and approvals. An environment with no group of the chosen level is left out.`
                : 'Groups and roles differ per environment. A missing environment (e.g. Prod with no groups yet) is returned by the AI review.'}>
                <div className={s.row}>
                  <Select value={fields.environment} onChange={(_, d) => setFields((x) => ({ ...x, environment: d.value, resource: '', accessLevel: '' }))}>
                    <option value="">Choose…</option>
                    {environments.map((e) => <option key={e} value={e}>{e}</option>)}
                    {isNew && environments.length > 1 && <option value={ALL_ENVIRONMENTS}>All environments</option>}
                  </Select>
                  {isNew && environments.length > 1 && (
                    <Button size="small" appearance={allEnvironments ? 'primary' : 'secondary'} aria-pressed={allEnvironments}
                      onClick={() => setFields((x) => ({ ...x, environment: allEnvironments ? '' : ALL_ENVIRONMENTS, resource: '', accessLevel: '' }))}>
                      {allEnvironments ? 'All environments selected' : 'Select all environments'}
                    </Button>
                  )}
                </div>
              </Field>
            )}
            {isNew && (
              <>
                <Switch checked={mode === 'checkbox'} label="Choose several resources"
                  onChange={(_, d) => {
                    const next: SelectionMode = d.checked ? 'checkbox' : 'dropdown';
                    if (next === 'dropdown') setSelected((cur) => fromDropdown(toDropdown(cur), options ?? []));
                    setMode(next);
                  }} />
                {mode === 'dropdown' ? (
                  <Field label="Resource" required validationMessage={envChosen && options !== null && options.length > 0 && selected.length === 0 ? 'Choose at least one resource.' : undefined}
                    validationState={envChosen && options !== null && options.length > 0 && selected.length === 0 ? 'none' : undefined}
                    hint="From the PMO mapping sheet of this tenant: its domains, PH, ACQ, or all of them.">
                    <Select value={toDropdown(selected)} disabled={!envChosen || !options || options.length === 0}
                      onChange={(_, d) => setSelected(fromDropdown(d.value, options ?? []))}>
                      <option value="">{envChosen ? 'Choose…' : 'Choose the environment first'}</option>
                      {(options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
                    </Select>
                  </Field>
                ) : (
                  // Checkboxes outside a Field: inside one, each checkbox would take the field's label instead of its own.
                  <fieldset role="group" aria-label="Resources" style={{ border: 'none', margin: 0, padding: 0 }}>
                    <legend><Body1><b>Resource</b> <span aria-hidden="true">*</span></Body1></legend>
                    <div className={s.row}>
                      {(options ?? []).map((o) => (
                        <Checkbox key={o} label={o} checked={selected.includes(o)}
                          onChange={(_, d) => setSelected((cur) => toggle(cur, o, d.checked === true, options ?? []))} />
                      ))}
                    </div>
                    <Caption1>{envChosen ? 'Tick one or more; All ticks every one.' : 'Choose the environment first.'}</Caption1>
                  </fieldset>
                )}
                {envChosen && options !== null && options.length === 0 && (
                  <MessageBar intent="warning" layout="multiline">
                    <MessageBarBody>{`The mapping sheet of ${tenantName} has no resources${fields.environment ? ` for ${fields.environment}` : ''} that can be requested.`}</MessageBarBody>
                  </MessageBar>
                )}
                <ErrorBar error={resolveError} />
                {resolvedList.length > 0 && (
                  <div className={f.resolved} aria-label="Access level and groups" role="region">
                    {levelOptions.map(({ resource, levels: offered }) => (
                      <Field key={resource} label={levelOptions.length === 1 ? 'Access level' : `Access level for ${resource}`} required
                        hint={levelOptions.length === 1 ? 'The levels the mapping sheet has for this resource. The Entra groups follow from the mapping sheet.' : undefined}
                        validationMessage={offered.length === 0 ? 'No group of this resource can be requested here (unresolved groups are escalated to PMO).' : undefined}>
                        <Select value={levelChoice[resource] ?? ''} disabled={offered.length === 0}
                          onChange={(_, d) => setLevelChoice((cur) => ({ ...cur, [resource]: d.value }))}>
                          <option value="">Choose…</option>
                          {offered.map((l) => <option key={l} value={l}>{l}</option>)}
                          {levelOptions.length > 1 && <option value={NOT_NEEDED}>Not needed</option>}
                        </Select>
                      </Field>
                    ))}
                    {levelsChosen && (
                      <Caption1>
                        {submissions.length === 0
                          ? 'No group of these levels in the chosen environment.'
                          : `${ticked.length} request${ticked.length === 1 ? '' : 's'} will be created${allEnvironments ? ` across ${submissions.map((x) => x.environment).join(', ')}` : ''}, each through the whole flow.`}
                      </Caption1>
                    )}
                    {unresolved.length > 0 && (
                      <MessageBar intent="warning" layout="multiline">
                        <MessageBarBody>{`${unresolved.length} group${unresolved.length === 1 ? '' : 's'} of this selection could not be resolved and ${unresolved.length === 1 ? 'is' : 'are'} never granted; PMO is told.`}</MessageBarBody>
                      </MessageBar>
                    )}
                    {tickedAccess.length > 0 && (
                      <details>
                        <summary><Caption1>Show the Entra groups (reference only)</Caption1></summary>
                        <div className={s.tableWrap}>
                          <Table aria-label="Resolved access" size="small">
                            <TableHeader>
                              <TableRow>
                                {allEnvironments && <TableHeaderCell>Environment</TableHeaderCell>}
                                <TableHeaderCell>Entra group</TableHeaderCell>
                                <TableHeaderCell>Access level</TableHeaderCell>
                                <TableHeaderCell>Status</TableHeaderCell>
                              </TableRow>
                            </TableHeader>
                            <TableBody>
                              {submissions.flatMap((x) => x.access.map((a) => (
                                <TableRow key={`${x.environment}|${a.groupObjectId || a.groupName}`}>
                                  {allEnvironments && <TableCell>{x.environment}</TableCell>}
                                  <TableCell><span className={s.wrap}>{a.groupName}</span></TableCell>
                                  <TableCell>{a.accessLevel}</TableCell>
                                  <TableCell><Badge color={STATUS[a.status].color}>{STATUS[a.status].label}</Badge></TableCell>
                                </TableRow>
                              )))}
                            </TableBody>
                          </Table>
                        </div>
                      </details>
                    )}
                    {resolvedList.some((r) => r.mappingQualityIssues.length > 0) && (
                      <Caption1>Mapping sheet notes for PMO: {resolvedList.flatMap((r) => r.mappingQualityIssues).map((n) => n.reason).join(' ')}</Caption1>
                    )}
                    <Caption1 className={s.muted}>Mapping version {resolvedList.map((r) => r.mappingVersion).filter((v, i, a) => a.indexOf(v) === i).join(', ')}. Production above Read waits for a ProdApprover after Owner and Security.</Caption1>
                  </div>
                )}
              </>
            )}
            {!isNew && sheet !== null && resources.length === 0 && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  {sheetError ? 'The mapping sheet could not be read; reload the page.'
                    : `The mapping sheet of ${tenantName} has no resources${fields.environment ? ` for ${fields.environment}` : ''} yet. PMO imports it from the group classification and the response document.`}
                </MessageBarBody>
              </MessageBar>
            )}
            {!isNew && (
            <Field label="Resource" required
              hint="From the PMO mapping sheet of this tenant. One request is one group: to ask for more resources (or All), start a new request.">
              <Select value={fields.resource} disabled={resources.length === 0 || (environments.length > 0 && !fields.environment)}
                onChange={(_, d) => setFields((x) => ({ ...x, resource: d.value, accessLevel: '' }))}>
                <option value="">{environments.length > 0 && !fields.environment ? 'Choose the environment first' : 'Choose…'}</option>
                {fields.resource && !resources.includes(fields.resource) && <option value={fields.resource}>{fields.resource} (not in the mapping sheet now)</option>}
                {resources.map((x) => <option key={x} value={x}>{x}</option>)}
              </Select>
            </Field>
            )}
            {!isNew && (
            <Field label="Access level" required hint="The levels the mapping sheet has for that resource and environment.">
              <Select value={fields.accessLevel} disabled={!fields.resource} onChange={(_, d) => set('accessLevel')(d.value)}>
                <option value="">{fields.resource ? 'Choose…' : 'Choose the resource first'}</option>
                {fields.accessLevel && !levels.includes(fields.accessLevel) && <option value={fields.accessLevel}>{fields.accessLevel} (not in the mapping sheet now)</option>}
                {levels.map((x) => <option key={x} value={x}>{x}</option>)}
              </Select>
            </Field>
            )}
          </FormSection>

          <FormSection n={3} id="why-h" title="Why they need it" done={readiness[2].ok}>
            <div className={f.template} aria-labelledby="template-h" role="region">
              <div className={s.row} style={{ justifyContent: 'space-between' }}>
                <Body1Strong as="h3" id="template-h" style={{ margin: 0 }}>Justification template</Body1Strong>
                <div className={s.row}>
                  <Button size="small" icon={<Copy16Regular />} onClick={() => void copyTemplate()}>{copied ? 'Copied' : 'Copy template'}</Button>
                  <Button size="small" appearance="primary" icon={<TextAdd20Regular />} onClick={() => setFields((x) => ({ ...x, justification: template }))}>Insert into justification</Button>
                </div>
              </div>
              <Caption1 className={s.muted}>Insert it, then replace every [ … ] with the joiner’s details. Never paste passwords or passes.</Caption1>
              <pre className={f.templateText}>{template}</pre>
            </div>
            <Field label="Justification" required validationMessage={templateLeft}
              hint="The business reason, consistent with the access level: role, tasks that need the access, start and duration, reference.">
              <Textarea value={fields.justification} onChange={(_, d) => set('justification')(d.value)} rows={6} resize="vertical" maxLength={2000} />
            </Field>
            <Caption1 className={s.muted} aria-live="polite">
              {length} / 2000 characters. Higher access needs a longer reason: the AI review checks the length against the access level.
            </Caption1>
            {privilegedRows.length > 0 && (
              <MessageBar intent={missingTasks(fields.justification) ? 'warning' : 'info'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Privileged group</MessageBarTitle>
                  {`${[...new Set(privilegedRows.map((r) => r.accessLevel))].join(', ')} is flagged Privileged on the mapping sheet. `}
                  {missingTasks(fields.justification)
                    ? 'Name the tasks that need it in the justification (e.g. build or run pipelines, ingest or transform data, deploy, support), or the AI review returns the request.'
                    : 'The justification names the tasks; approvers see the Privileged flag.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </FormSection>

          {siblings.length > 0 && (
            <section className={`${s.section} ${f.card}`} aria-labelledby="bundle-h">
              <Subtitle2 as="h2" id="bundle-h">Fix the whole submission</Subtitle2>
              <Caption1>
                {`This request was submitted together with ${siblings.length} other request${siblings.length === 1 ? '' : 's'} (one per group) that the AI review also returned. `}
                Resubmit them with the same joiner details, justification and Owner; each keeps its own resource and access level. Untick any you want to fix on its own.
              </Caption1>
              <fieldset role="group" aria-label="Other returned requests" style={{ border: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                {siblings.map((x) => (
                  <Checkbox key={x.requestId} label={`${x.resource ?? x.requestId} · ${x.accessLevel ?? '—'}`} checked={include.includes(x.requestId)}
                    onChange={(_, d) => setInclude((cur) => d.checked === true ? [...cur, x.requestId] : cur.filter((y) => y !== x.requestId))} />
                ))}
              </fieldset>
            </section>
          )}

          <FormSection n={4} id="owner-h" title="Who decides the business need" done={readiness[3].ok}>
            <Field label="Owner (sign-in name)" required hint="Only this person’s decision counts at the Owner stage. Security decides next." validationMessage={ownerError}>
              <Input type="email" value={fields.owner} onChange={(_, d) => set('owner')(d.value)} maxLength={200} autoComplete="off" />
            </Field>
          </FormSection>

          {isNew && developer && (
            <Field hint={liveSimulation
              ? `LIVE: you act for the Owner, Security and the teams, and the AI agent creates the user, group and membership and assigns the roles in Entra and Azure for real${chosenEnvironments.some(isProduction) ? ' (not for the Prod environment: that one stays a dry run)' : ''}. Use test accounts only; remove what it created with Offboard user.`
              : `${requester ? 'Developer: a simulation' : 'You have the Developer role only, so your requests are simulations:'} every stage runs with you acting for the Owner, Security and the teams. Nothing is created in Entra or Azure and its mails stay in the app.${project.liveSimulationRefused && project.liveSimulationRefused.indexOf('switched off') < 0 ? ` Not live: ${project.liveSimulationRefused}.` : ''}`}>
              <Switch label={liveSimulation ? 'Simulation (LIVE: real Entra and Azure writes)' : 'Simulation (developer dry run)'} checked={simulate} disabled={!requester} onChange={(_, d) => setSimulate(d.checked)} />
            </Field>
          )}
          <div className={f.submitBar}>
            <Button appearance="primary" size="large" type="submit" icon={<Send20Regular />} disabled={saving || invalid || stale || partial !== null}>
              {saving ? 'Submitting…' : requestId ? (include.length > 0 ? `Resubmit ${1 + include.length} requests` : 'Resubmit request') : simulate && isNew ? 'Submit simulation' : 'Submit request'}
            </Button>
            <Button size="large" onClick={() => navigate(requestId ? { page: 'request', requestId } : { page: 'requests' })}>Cancel</Button>
            <Caption1 className={s.muted}>{ready} of {readiness.length} parts ready{isNew && ticked.length > 1 ? ` · ${ticked.length} requests will be created` : ''}</Caption1>
          </div>
        </form>

        <aside className={f.aside} aria-label="Request summary and how it works">
          <section className={s.section} aria-labelledby="flow-h">
            <Subtitle2 as="h2" id="flow-h">How this works</Subtitle2>
            <ol className={f.flow}>
              {FLOW_OVERVIEW.map((step) => <li key={step} className={f.flowStep}><Body1>{step}</Body1></li>)}
            </ol>
            <dl className={f.who}>
              {whoDoesWhat(agent, tap, privileged).map(([who, what]) => (
                <div key={who} className={f.whoRow}>
                  <dt><Caption1><b>{who}</b></Caption1></dt>
                  <dd style={{ margin: 0 }}><Caption1>{what}</Caption1></dd>
                </div>
              ))}
            </dl>
          </section>
          <div className={f.sticky}>
            <RequestSummary
              joiner={`${fields.firstName} ${fields.lastName}`.trim()}
              mail={fields.enterpriseId.trim()}
              signIn={derived && !mailError ? derived : ''}
              tenant={tenantName}
              environment={fields.environment}
              groups={summaryGroups}
              owner={fields.owner.trim()}
              items={readiness}
              count={isNew ? ticked.length : 1 + include.length}
            />
          </div>
        </aside>
      </div>
    </div>
  );
}

const useFormStyles = makeStyles({
  layout: {
    display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 340px', gap: '24px', alignItems: 'start',
    '@media (max-width: 1199px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  aside: {
    display: 'flex', flexDirection: 'column', gap: '20px', alignSelf: 'stretch', minWidth: 0,
    '@media (max-width: 1199px)': { order: -1, alignSelf: 'auto' },
  },
  sticky: { position: 'sticky', top: '80px', '@media (max-width: 1199px)': { position: 'static' } },
  two: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px', '@media (max-width: 560px)': { gridTemplateColumns: '1fr' } },
  card: {
    gap: '14px',
    // Fields shrink with the window: a native select otherwise takes the width of its longest option (phones).
    '& .fui-Field': { gridTemplateColumns: 'minmax(0, 1fr)' },
    '& .fui-Select': { minWidth: 0 },
    '& .fui-Select__select': { minWidth: 0, textOverflow: 'ellipsis' },
  },
  head: { display: 'flex', alignItems: 'center', gap: '12px' },
  num: {
    width: '28px', height: '28px', borderRadius: '50%', display: 'grid', placeItems: 'center', flexShrink: 0,
    fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1,
  },
  numDone: { backgroundColor: tokens.colorStatusSuccessBackground3, color: tokens.colorNeutralForegroundOnBrand },
  resolved: {
    display: 'flex', flexDirection: 'column', gap: '8px', padding: '14px', borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  picked: { backgroundColor: tokens.colorBrandBackground2 },
  template: {
    display: 'flex', flexDirection: 'column', gap: '8px', padding: '14px', borderRadius: tokens.borderRadiusLarge,
    border: `1px dashed ${tokens.colorNeutralStroke1}`, backgroundColor: tokens.colorNeutralBackground2,
  },
  templateText: {
    margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'inherit', fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300, color: tokens.colorNeutralForeground2,
  },
  submitBar: {
    display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '12px', padding: '14px 20px', borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, boxShadow: tokens.shadow8,
    position: 'sticky', bottom: '12px', zIndex: 2,
  },
  flow: { display: 'flex', flexDirection: 'column', gap: '0', margin: 0, padding: 0, listStyle: 'none' },
  flowStep: {
    position: 'relative', padding: '4px 0 4px 18px',
    '::before': {
      content: '""', position: 'absolute', left: '4px', top: '12px', width: '7px', height: '7px', borderRadius: '50%',
      backgroundColor: tokens.colorBrandBackground,
    },
    '::after': {
      content: '""', position: 'absolute', left: '7px', top: '22px', bottom: '-6px', width: '1px', backgroundColor: tokens.colorNeutralStroke1,
    },
    ':last-child::after': { display: 'none' },
  },
  who: { display: 'flex', flexDirection: 'column', gap: '6px', margin: '6px 0 0', paddingTop: '10px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  whoRow: { display: 'grid', gridTemplateColumns: '92px 1fr', gap: '8px' },
});

/** One numbered part of the form; its number turns into a tick once that part is complete. */
function FormSection({ n, id, title, done, children }: { n: number; id: string; title: string; done: boolean; children: React.ReactNode }) {
  const s = useStyles();
  const f = useFormStyles();
  return (
    <section className={`${s.section} ${f.card}`} aria-labelledby={id}>
      <div className={f.head}>
        <span className={done ? `${f.num} ${f.numDone}` : f.num} aria-hidden="true">{done ? '✓' : n}</span>
        <Subtitle2 as="h2" id={id}>{title}</Subtitle2>
        {done && <span className={s.muted} style={{ fontSize: 12 }}>complete</span>}
      </div>
      {children}
    </section>
  );
}

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Checkbox, Field, Input, MessageBar, MessageBarBody, Subtitle2,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow,
} from '@fluentui/react-components';
import { ApiError, api } from '../lib/api';
import { dateTime } from '../lib/format';
import { ACCEPT, UPLOADS, acceptedFile, canApply, rollbackTarget, summary } from '../lib/uploads';
import type { ProjectSummary, UploadHistory, UploadKind, UploadView, UploadsOverview } from '../lib/types';
import { Empty, ErrorBar, Loading, PageTitle, useStyles } from '../components/common';

/**
 * v1.1 F4: the Data uploads page. The Data Steward uploads the PMO mapping sheet or the people inventory (.xlsx or .csv),
 * sees the validation and the diff against the current version, applies it (upsert by key; rows missing from the file
 * stay unless "Retire missing rows" is ticked) and can roll back the current version. PMO can read it.
 */
export function DataUploadsPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [overview, setOverview] = useState<UploadsOverview | null>(null);
  const [error, setError] = useState<unknown>(null);
  const canUpload = project.roles.includes('DataSteward');

  const load = useCallback(() => {
    setError(null);
    api.uploads(project.projectId).then(setOverview).catch(setError);
  }, [project.projectId]);
  useEffect(load, [load]);

  if (!overview) return <div className={s.stack}><PageTitle>Data uploads</PageTitle><ErrorBar error={error} />{!error && <Loading />}</div>;
  return (
    <div className={s.stack}>
      <PageTitle>Data uploads</PageTitle>
      <Body1>
        Upload the PMO mapping sheet or the people inventory as .xlsx (first sheet, or the sheet you name) or .csv, at most{' '}
        {Math.round(overview.maxUploadBytes / 1024).toLocaleString()} KB. The file is checked first and nothing changes until you apply it.
        Rows are merged by key; rows missing from the file are kept unless you tick &quot;Retire missing rows&quot;. Every apply is a new version that can be rolled back.
      </Body1>
      {!canUpload && <MessageBar intent="info"><MessageBarBody>Read only: uploads are made by the Data Steward.</MessageBarBody></MessageBar>}
      <ErrorBar error={error} />
      {UPLOADS.map((u) => (
        <UploadSection key={u.kind} project={project} kind={u.kind} button={u.button} title={u.title} keyText={u.key}
          history={u.kind === 'mapping' ? overview.mapping : overview.inventory} canUpload={canUpload} onChanged={load} />
      ))}
      <Caption1>External upload API: {overview.externalApiEnabled ? 'on' : 'off'} (docs/EXTERNAL_UPLOAD_API.md).</Caption1>
    </div>
  );
}

function UploadSection({ project, kind, button, title, keyText, history, canUpload, onChanged }: {
  project: ProjectSummary; kind: UploadKind; button: string; title: string; keyText: string; history: UploadHistory; canUpload: boolean; onChanged: () => void;
}) {
  const s = useStyles();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [retire, setRetire] = useState(false);
  const [sheet, setSheet] = useState('');
  const [view, setView] = useState<UploadView | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [applied, setApplied] = useState<string | null>(null);
  const [confirmRollback, setConfirmRollback] = useState(false);
  const target = rollbackTarget(history);

  const validate = async (f: File, retireMissing: boolean) => {
    setError(null); setApplied(null); setView(null);
    if (!acceptedFile(f.name)) {
      setError(new ApiError(400, 'unsupported', 'Only .xlsx and .csv files are accepted (macro-enabled or binary Excel files are refused).'));
      return;
    }
    setBusy(true);
    try {
      setView(await api.upload(project.projectId, kind, f, { mode: 'validate', retireMissing, sheet: sheet.trim() || undefined }));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    if (!file || !view) return;
    setBusy(true); setError(null);
    try {
      const r = await api.upload(project.projectId, kind, file, { mode: 'apply', retireMissing: retire, sheet: sheet.trim() || undefined, baseVersion: view.baseVersion });
      setView(r);
      if (r.upload.status === 'Applied') { setApplied(r.upload.version); onChanged(); }
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const rollback = async () => {
    if (target === null) return;
    setBusy(true); setError(null);
    try {
      const v = await api.rollbackUpload(project.projectId, kind, target);
      setApplied(`${v.version} (rollback of v${target})`); setConfirmRollback(false); setView(null); onChanged();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const result = view?.upload;
  return (
    <section className={s.section} aria-label={title}>
      <Subtitle2 as="h2">{title} — current version v{history.current}</Subtitle2>
      <Caption1>Columns: {history.columns.join(', ')}. Key: {keyText}.</Caption1>
      {canUpload && (
        <div className={s.row}>
          <input ref={input} type="file" accept={ACCEPT} hidden aria-label={`${button} file`}
            onChange={(e) => { const f = e.target.files?.[0] ?? null; e.target.value = ''; setFile(f); if (f) void validate(f, retire); }} />
          <Button appearance="primary" disabled={busy} onClick={() => input.current?.click()}>{button}</Button>
          <Field label="Sheet name (optional)" orientation="horizontal">
            <Input value={sheet} placeholder="first sheet" onChange={(_, d) => setSheet(d.value)} />
          </Field>
          <Checkbox label="Retire missing rows" checked={retire} disabled={busy}
            onChange={(_, d) => { const v = !!d.checked; setRetire(v); if (file) void validate(file, v); }} />
        </div>
      )}
      <ErrorBar error={error} />
      {busy && <Loading label="Checking" />}
      {applied && <MessageBar intent="success"><MessageBarBody>Applied: {applied}.</MessageBarBody></MessageBar>}
      {result && (
        <div className={s.stack}>
          <div className={s.row}>
            <Badge color={result.status === 'Invalid' ? 'danger' : 'success'}>{result.status}</Badge>
            <Body1>{file?.name ?? view.fileName}{result.status !== 'Invalid' && <> — {summary(view)}</>}</Body1>
          </div>
          {result.errors.length > 0 && (
            <div className={s.tableWrap}>
              <Table aria-label="Validation errors" size="small">
                <TableHeader><TableRow>
                  <TableHeaderCell>Row</TableHeaderCell><TableHeaderCell>Column</TableHeaderCell><TableHeaderCell>Problem</TableHeaderCell><TableHeaderCell>Message</TableHeaderCell>
                </TableRow></TableHeader>
                <TableBody>
                  {result.errors.map((e, i) => (
                    <TableRow key={i}>
                      <TableCell>{e.row === 0 ? 'file' : e.row}</TableCell><TableCell>{e.column || '—'}</TableCell>
                      <TableCell>{e.code}</TableCell><TableCell><span className={s.wrap}>{e.message}</span></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
          {view.diff.length > 0 && (
            <div className={s.tableWrap}>
              <Table aria-label="Changes against the current version" size="small">
                <TableHeader><TableRow>
                  <TableHeaderCell>Change</TableHeaderCell><TableHeaderCell>Row</TableHeaderCell><TableHeaderCell>What</TableHeaderCell><TableHeaderCell>Fields</TableHeaderCell>
                </TableRow></TableHeader>
                <TableBody>
                  {view.diff.map((d, i) => (
                    <TableRow key={i}>
                      <TableCell>{d.change === 'Missing' ? 'Missing from file' : d.change}</TableCell><TableCell>{d.row || '—'}</TableCell>
                      <TableCell><span className={s.wrap}>{d.label}</span></TableCell><TableCell>{d.fields.join(', ') || '—'}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
          {canUpload && result.mode === 'validate' && result.status === 'Valid' && (
            <div className={s.row}>
              <Button appearance="primary" disabled={busy || !canApply(view, history.current)} onClick={() => void apply()}>Apply as v{history.current + 1}</Button>
              {!canApply(view, history.current) && <Caption1>{view.baseVersion !== history.current ? 'The data changed since this check: choose the file again.' : 'Nothing to change.'}</Caption1>}
            </div>
          )}
        </div>
      )}
      <Subtitle2 as="h3">Versions</Subtitle2>
      {history.versions.length === 0 ? <Empty>No upload yet.</Empty> : (
        <div className={s.tableWrap}>
          <Table aria-label={`${title} versions`} size="small">
            <TableHeader><TableRow>
              <TableHeaderCell>Version</TableHeaderCell><TableHeaderCell>When / who</TableHeaderCell><TableHeaderCell>File</TableHeaderCell><TableHeaderCell>Changes</TableHeaderCell>
            </TableRow></TableHeader>
            <TableBody>
              {history.versions.map((v) => (
                <TableRow key={v.version}>
                  <TableCell>{v.version} {v.state === 'RolledBack' && <Badge color="subtle">rolled back</Badge>}{v.rollbackOf ? <Caption1 block>rollback of v{v.rollbackOf}</Caption1> : null}</TableCell>
                  <TableCell>{dateTime(v.uploadedAt)}<Caption1 block>{v.uploadedBy} · {v.channel === 'ExternalApi' ? 'external API' : 'this page'}</Caption1></TableCell>
                  <TableCell><span className={s.wrap}>{v.fileName ?? '—'}</span>{v.fileHash && <Caption1 block>sha256 {v.fileHash.slice(0, 12)}…</Caption1>}</TableCell>
                  <TableCell><span className={s.wrap}>{v.diffSummary}</span></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {canUpload && target !== null && (
        confirmRollback ? (
          <MessageBar intent="warning">
            <MessageBarBody>
              Roll back v{target}? Rows it changed get their earlier values; rows it added are retired.{' '}
              <Button size="small" appearance="primary" disabled={busy} onClick={() => void rollback()}>Roll back v{target}</Button>{' '}
              <Button size="small" disabled={busy} onClick={() => setConfirmRollback(false)}>Cancel</Button>
            </MessageBarBody>
          </MessageBar>
        ) : <div><Button disabled={busy} onClick={() => setConfirmRollback(true)}>Roll back v{target}…</Button></div>
      )}
    </section>
  );
}

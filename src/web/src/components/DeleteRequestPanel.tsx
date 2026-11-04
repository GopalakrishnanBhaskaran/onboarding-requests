import { useState } from 'react';
import { Button, Caption1, Checkbox, Field, Subtitle2, Textarea } from '@fluentui/react-components';
import { api } from '../lib/api';
import { navigate } from '../lib/router';
import { deleteReasonError } from '../lib/developer';
import type { AccessRequest } from '../lib/types';
import { ErrorBar, useStyles } from './common';

/**
 * Developer (4 Nov 2026): delete this request with its review runs, mails and issues. A real request needs a reason (kept
 * in the audit trail). Deleting never removes access already granted in Entra or Azure: Offboard user does that.
 */
export function DeleteRequestPanel({ request }: { request: AccessRequest }) {
  const s = useStyles();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [sure, setSure] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const simulated = request.simulated === true;
  const reasonError = deleteReasonError(simulated, reason);

  async function remove() {
    setBusy(true);
    setError(null);
    try {
      await api.deleteRequest(request.projectId, request.id, reason);
      navigate({ page: 'requests' });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  if (!open) {
    return <div><Button onClick={() => setOpen(true)}>{simulated ? 'Delete simulation' : 'Delete request'}</Button></div>;
  }
  return (
    <section className={s.section} aria-label="Delete request">
      <Subtitle2 as="h2">{simulated ? 'Delete this simulation' : 'Delete this request'}</Subtitle2>
      <Caption1>
        Removes the request with its review runs, mails and issues; an audit event records who deleted it, when and why. It cannot be undone.
        {!simulated && ' Access already granted in Entra or Azure stays: use Offboard user to remove it. The response document keeps its record once the request was approved.'}
      </Caption1>
      <Field label="Reason" required={!simulated} validationState={reasonError && reason ? 'error' : 'none'} validationMessage={reason ? reasonError ?? undefined : undefined}>
        <Textarea aria-label="Reason" value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} rows={2} />
      </Field>
      <Checkbox label="I understand this cannot be undone" checked={sure} onChange={(_, d) => setSure(d.checked === true)} />
      <ErrorBar error={error} />
      <div className={s.row}>
        <Button appearance="primary" disabled={busy || !sure || !!reasonError} onClick={() => void remove()}>{busy ? 'Deleting…' : 'Delete'}</Button>
        <Button onClick={() => setOpen(false)}>Cancel</Button>
      </div>
    </section>
  );
}

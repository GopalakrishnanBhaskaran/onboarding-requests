import { useState } from 'react';
import { Body1, Button, Field, Subtitle2, Textarea } from '@fluentui/react-components';
import { api } from '../lib/api';
import { APPROVE_LABEL } from '../lib/flow';
import type { AccessRequest, RequestView } from '../lib/types';
import { ErrorBar, useStyles } from './common';

/**
 * Stage 3, the only place with decision controls. Rendered only when the server says this caller may decide now:
 * the named Owner at the Owner stage, a Security member (not the Owner approver) at the Security stage.
 * The decision is tied to the request id and to the review the approver is looking at.
 */
export function ApprovalPanel({ request, stage, onDone }: { request: AccessRequest; stage: 'owner' | 'security'; onDone: (v: RequestView) => void }) {
  const s = useStyles();
  const [remarks, setRemarks] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const runId = request.flow?.scope?.reviewRunId ?? '';

  async function decide(decision: 'Approved' | 'Rejected') {
    setBusy(true);
    setError(null);
    try {
      onDone(await api.respond(request.projectId, request.id, decision, remarks.trim(), runId));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className={s.section} aria-labelledby="decision-h">
      <Subtitle2 as="h2" id="decision-h">{stage === 'owner' ? 'Your decision as the named Owner' : 'Your decision as Security'}</Subtitle2>
      <Body1>
        {stage === 'owner'
          ? 'Decide on the business need, based on the AI review summary above.'
          : 'Decide on the risk and access level, based on the AI review summary and the Owner decision above.'}
      </Body1>
      <ErrorBar error={error} />
      <Field label="Remarks" hint="Required when rejecting. Never paste passwords or passes.">
        <Textarea value={remarks} onChange={(_, d) => setRemarks(d.value)} maxLength={1000} />
      </Field>
      <div className={s.row}>
        <Button appearance="primary" disabled={busy || !runId} onClick={() => void decide('Approved')}>
          {stage === 'owner' ? APPROVE_LABEL.owner : APPROVE_LABEL.security}
        </Button>
        <Button disabled={busy || remarks.trim() === ''} onClick={() => void decide('Rejected')}>{APPROVE_LABEL.reject}</Button>
      </div>
    </section>
  );
}

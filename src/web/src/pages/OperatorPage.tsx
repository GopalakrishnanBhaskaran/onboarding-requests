import { useState } from 'react';
import { Body1, Button } from '@fluentui/react-components';
import { api } from '../lib/api';
import type { ProjectSummary } from '../lib/types';
import { ErrorBar, PageTitle, useStyles } from '../components/common';

/** Operator: mark runs stuck in Queued/Running past the threshold as Failed (Timeout). */
export function OperatorPage({ project }: { project: ProjectSummary }) {
  const s = useStyles();
  const [result, setResult] = useState<string[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  async function recover() {
    setBusy(true);
    setError(null);
    try {
      setResult((await api.recoverStuck(project.projectId)).recovered);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={s.stack}>
      <PageTitle>Operator: stuck reviews</PageTitle>
      <Body1>Marks reviews stuck in Queued or Running beyond the configured limit as Failed (Timeout). The PMO can then retry them.</Body1>
      <div><Button onClick={() => void recover()} disabled={busy}>Recover stuck reviews</Button></div>
      <ErrorBar error={error} />
      <div aria-live="polite" role="status">
        {result && <Body1>{result.length === 0 ? 'No stuck reviews found.' : `Marked as failed: ${result.join(', ')}`}</Body1>}
      </div>
    </div>
  );
}

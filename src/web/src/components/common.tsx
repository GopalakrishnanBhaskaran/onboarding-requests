import { useEffect, useRef } from 'react';
import {
  Body1, Caption1, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Title2, makeStyles, tokens,
} from '@fluentui/react-components';
import { ApiError } from '../lib/api';

export const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  row: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, alignItems: 'center' },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  tableWrap: { overflowX: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  pre: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
});

/** Page heading that receives focus on navigation, so screen readers announce the new page. */
export function PageTitle({ children }: { children: string }) {
  const ref = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, [children]);
  return (
    <h1 ref={ref} tabIndex={-1} style={{ outline: 'none', margin: 0 }}>
      <Title2>{children}</Title2>
    </h1>
  );
}

export function ErrorBar({ error }: { error: unknown }) {
  if (!error) return null;
  const e = error instanceof ApiError ? error : null;
  return (
    <MessageBar intent="error" role="alert">
      <MessageBarBody>
        <MessageBarTitle>{e?.status === 404 ? 'Not found' : e?.status === 403 ? 'Not permitted' : 'Something went wrong'}</MessageBarTitle>
        {e?.message ?? (error instanceof Error ? error.message : 'Unexpected error.')}
        {e?.correlationId && <Caption1 block>Reference: {e.correlationId}</Caption1>}
      </MessageBarBody>
    </MessageBar>
  );
}

export function Loading({ label = 'Loading' }: { label?: string }) {
  return <Spinner size="small" label={label} />;
}

export function Empty({ children }: { children: React.ReactNode }) {
  const s = useStyles();
  return <Body1 className={s.muted}>{children}</Body1>;
}

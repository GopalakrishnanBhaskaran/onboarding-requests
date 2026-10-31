import { useEffect, useRef } from 'react';
import {
  Body1, Caption1, MessageBar, MessageBarBody, MessageBarTitle, Spinner, Title2, makeStyles, tokens,
} from '@fluentui/react-components';
import { ApiError } from '../lib/api';

export const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  row: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, alignItems: 'center' },
  /** A card: every panel of the app sits on one, so the pages read as one system. */
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: '20px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    minWidth: 0,
  },
  tableWrap: { overflowX: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  pre: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  /** A cell with long unbroken text (a mail address, an id): it wraps inside its column instead of running into the next one. */
  wrap: { overflowWrap: 'anywhere' },
  /** A table whose columns keep the widths their header cells set; narrower windows scroll it sideways (tableWrap). */
  fixedTable: { tableLayout: 'fixed', minWidth: '960px' },
  /** Small upper-case label above a value or a group of controls. */
  eyebrow: {
    color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em', textTransform: 'uppercase',
  },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  /** Label/value pairs. */
  facts: { display: 'grid', gridTemplateColumns: 'minmax(110px, max-content) 1fr', gap: '6px 16px', margin: 0, '& dd': { margin: 0, minWidth: 0 } },
});

const useHeaderStyles = makeStyles({
  header: { display: 'flex', flexWrap: 'wrap', alignItems: 'flex-end', justifyContent: 'space-between', gap: tokens.spacingHorizontalL },
  text: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, minWidth: 0, maxWidth: '760px' },
  sub: { color: tokens.colorNeutralForeground2 },
  actions: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, alignItems: 'center' },
});

/** A mail address or sign-in name that may break after the @ (and anywhere else only if it must). */
export function Breakable({ text }: { text: string | null | undefined }) {
  if (!text) return <>—</>;
  const at = text.indexOf('@');
  return at < 0 ? <>{text}</> : <>{text.slice(0, at + 1)}<wbr />{text.slice(at + 1)}</>;
}

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

/** Page title, one line on what the page is for, and the page's main actions on the right. */
export function PageHeader({ title, children, actions }: { title: string; children?: React.ReactNode; actions?: React.ReactNode }) {
  const h = useHeaderStyles();
  return (
    <div className={h.header}>
      <div className={h.text}>
        <PageTitle>{title}</PageTitle>
        {children && <Body1 className={h.sub}>{children}</Body1>}
      </div>
      {actions && <div className={h.actions}>{actions}</div>}
    </div>
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

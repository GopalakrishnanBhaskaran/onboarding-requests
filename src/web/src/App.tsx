import { useEffect, useMemo, useState } from 'react';
import {
  Body1, Button, Caption1, Dropdown, FluentProvider, Link, MessageBar, MessageBarBody, Option, Title3,
  makeStyles, tokens, webDarkTheme, webLightTheme,
} from '@fluentui/react-components';
import { api } from './lib/api';
import { href, useRoute, type Route } from './lib/router';
import type { Me, ProjectSummary } from './lib/types';
import { ErrorBar, Loading } from './components/common';
import { RequestsPage } from './pages/RequestsPage';
import { RequestFormPage } from './pages/RequestFormPage';
import { RequestDetailPage } from './pages/RequestDetailPage';
import { QueuePage } from './pages/QueuePage';
import { ReferencePage } from './pages/ReferencePage';
import { OperatorPage } from './pages/OperatorPage';

export const BANNER = 'This checks request readiness. A ready review is not an access decision.';
const PROJECT_KEY = 'pmo-review.project';

const useStyles = makeStyles({
  app: { minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  header: {
    display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} 16px`, backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  grow: { flexGrow: 1 },
  nav: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalM, padding: `${tokens.spacingVerticalS} 16px` },
  main: { padding: '16px', maxWidth: '1100px', margin: '0 auto' },
  skip: {
    position: 'absolute', left: '-10000px',
    ':focus': { left: '16px', top: '8px', zIndex: 10, padding: '8px', backgroundColor: tokens.colorNeutralBackground1 },
  },
});

function useDarkMode(): boolean {
  const query = '(prefers-color-scheme: dark)';
  const [dark, setDark] = useState(() => window.matchMedia?.(query).matches ?? false);
  useEffect(() => {
    const mq = window.matchMedia?.(query);
    if (!mq) return;
    const on = (e: MediaQueryListEvent) => setDark(e.matches);
    mq.addEventListener('change', on);
    return () => mq.removeEventListener('change', on);
  }, []);
  return dark;
}

function readStoredProject(): string | null {
  try {
    return window.localStorage.getItem(PROJECT_KEY);
  } catch {
    return null;
  }
}

function storeProject(id: string) {
  try {
    window.localStorage.setItem(PROJECT_KEY, id);
  } catch {
    /* storage unavailable: selection just isn't remembered */
  }
}

export function App() {
  const dark = useDarkMode();
  return (
    <FluentProvider theme={dark ? webDarkTheme : webLightTheme}>
      <Shell />
    </FluentProvider>
  );
}

function Shell() {
  const s = useStyles();
  const route = useRoute();
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [projectId, setProjectId] = useState<string | null>(readStoredProject());

  useEffect(() => {
    api.me().then(setMe).catch(setError);
  }, []);

  const project: ProjectSummary | undefined = useMemo(
    () => me?.projects.find((p) => p.projectId === projectId) ?? me?.projects[0],
    [me, projectId],
  );

  const choose = (id: string) => {
    setProjectId(id);
    storeProject(id);
  };

  const anyRole = (role: string) => me?.projects.some((p) => p.roles.includes(role as never)) ?? false;

  return (
    <div className={s.app}>
      <a className={s.skip} href="#main">Skip to content</a>
      <header className={s.header}>
        <Title3 as="p">PMO Request Review</Title3>
        <Caption1>Pilot</Caption1>
        <div className={s.grow} />
        {me && me.projects.length > 0 && project && (
          <Dropdown
            aria-label="Project"
            value={project.name}
            selectedOptions={[project.projectId]}
            onOptionSelect={(_, d) => d.optionValue && choose(d.optionValue)}
          >
            {me.projects.map((p) => (
              <Option key={p.projectId} value={p.projectId} text={p.name}>
                {p.name} ({p.roles.join(', ')})
              </Option>
            ))}
          </Dropdown>
        )}
        {me && <Body1>{me.displayName || me.userId}</Body1>}
        <Button as="a" href="/logout" appearance="subtle">Sign out</Button>
      </header>

      {/* Permanent, non-dismissible scope statement. */}
      <MessageBar intent="info" layout="multiline" style={{ borderRadius: 0 }}>
        <MessageBarBody>{BANNER}</MessageBarBody>
      </MessageBar>

      {me && project && (
        <nav className={s.nav} aria-label="Main">
          <Link href={href({ page: 'requests' })}>My requests</Link>
          {anyRole('Reviewer') && <Link href={href({ page: 'queue' })}>Review queue</Link>}
          {(project.roles.includes('Maintainer') || project.roles.includes('PMO') || project.roles.includes('Reviewer')) && (
            <Link href={href({ page: 'reference' })}>Reference data</Link>
          )}
          {project.roles.includes('Operator') && <Link href={href({ page: 'operator' })}>Operator</Link>}
        </nav>
      )}

      <main id="main" className={s.main}>
        <ErrorBar error={error} />
        {!me && !error && <Loading label="Signing in" />}
        {me && me.projects.length === 0 && (
          <MessageBar intent="warning">
            <MessageBarBody>
              You are signed in but not registered for any pilot project. Ask the pilot owner to register this user id: <code>{me.userId}</code>
            </MessageBarBody>
          </MessageBar>
        )}
        {me && project && <Page route={route} project={project} onOpenProject={choose} />}
      </main>
    </div>
  );
}

function Page({ route, project, onOpenProject }: { route: Route; project: ProjectSummary; onOpenProject: (id: string) => void }) {
  switch (route.page) {
    case 'new':
      return <RequestFormPage key="new" project={project} />;
    case 'edit':
      return <RequestFormPage key={route.requestId} project={project} requestId={route.requestId} />;
    case 'request':
      return <RequestDetailPage key={`${project.projectId}/${route.requestId}`} project={project} requestId={route.requestId} />;
    case 'queue':
      return <QueuePage onOpen={onOpenProject} />;
    case 'reference':
      return <ReferencePage key={project.projectId} project={project} />;
    case 'operator':
      return <OperatorPage project={project} />;
    default:
      return <RequestsPage key={project.projectId} project={project} />;
  }
}

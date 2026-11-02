import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Avatar, Badge, Body1Strong, Button, Caption1, CounterBadge, Dropdown, FluentProvider, MessageBar, MessageBarBody, Option,
  makeStyles, mergeClasses, tokens, webDarkTheme, webLightTheme,
} from '@fluentui/react-components';
import {
  Add20Regular, ArrowUpload20Regular, DataBarVertical20Regular, DocumentBulletList20Regular, People20Regular, ShieldCheckmark20Regular,
  ShieldKeyhole20Filled, SignOut20Regular, Table20Regular, TaskListSquareLtr20Regular, Warning20Regular,
} from '@fluentui/react-icons';
import { api } from './lib/api';
import { href, useRoute, type Route } from './lib/router';
import type { ActionItem, AppRole, Me, ProjectSummary } from './lib/types';
import { QueueContext } from './lib/queue';
import { ErrorBar, Loading } from './components/common';
import { RequestsPage } from './pages/RequestsPage';
import { RequestFormPage } from './pages/RequestFormPage';
import { RequestDetailPage } from './pages/RequestDetailPage';
import { QueuePage } from './pages/QueuePage';
import { MappingSheetPage } from './pages/MappingSheetPage';
import { ContactsPage } from './pages/ContactsPage';
import { OperatorPage } from './pages/OperatorPage';
import { EscalationsPage } from './pages/EscalationsPage';
import { DataUploadsPage } from './pages/DataUploadsPage';
import { AGENT_BANNER, AGENT_TAP_BANNER, BANNER } from './lib/flow';

/** Roles that handle escalations (v1.1): ProdApprovers decide the Prod ones; the others read them. */
const ESCALATION_ROLES: AppRole[] = ['ProdApprover', 'Security', 'PMO', 'Operator', 'IdTeam', 'DataSteward'];

const PROJECT_KEY = 'pmo-review.project';
const NARROW = '@media (max-width: 1023px)';

const useStyles = makeStyles({
  app: { minHeight: '100vh', backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  header: {
    display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '8px 16px', padding: '10px 20px',
    backgroundColor: tokens.colorNeutralBackground1, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    position: 'sticky', top: 0, zIndex: 5,
    [NARROW]: { position: 'static', padding: '10px 16px' },
  },
  switcher: { minWidth: '220px', [NARROW]: { minWidth: 0, flex: '1 1 160px' } },
  brand: { display: 'flex', alignItems: 'center', gap: '10px', textDecoration: 'none', color: 'inherit' },
  mark: {
    width: '32px', height: '32px', borderRadius: tokens.borderRadiusLarge, display: 'grid', placeItems: 'center',
    color: tokens.colorNeutralForegroundOnBrand, backgroundColor: tokens.colorBrandBackground, boxShadow: tokens.shadow4,
  },
  brandText: { display: 'flex', flexDirection: 'column', lineHeight: 1.15 },
  brandName: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  tenant: { color: tokens.colorNeutralForeground3 },
  grow: { flexGrow: 1, [NARROW]: { display: 'none' } },
  user: { display: 'flex', alignItems: 'center', gap: '10px', minWidth: 0 },
  userText: { display: 'flex', flexDirection: 'column', minWidth: 0, lineHeight: 1.2, [NARROW]: { display: 'none' } },
  roles: { color: tokens.colorNeutralForeground3 },
  scope: {
    display: 'flex', alignItems: 'flex-start', gap: '10px', padding: '8px 20px', fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200, color: tokens.colorNeutralForeground2, backgroundColor: tokens.colorBrandBackground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '& svg': { flexShrink: 0, color: tokens.colorBrandForeground1 },
  },
  body: {
    display: 'grid', gridTemplateColumns: '236px minmax(0, 1fr)', alignItems: 'start',
    [NARROW]: { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  nav: {
    position: 'sticky', top: '64px', display: 'flex', flexDirection: 'column', gap: '2px', padding: '20px 12px',
    [NARROW]: {
      position: 'static', flexDirection: 'row', overflowX: 'auto', padding: '8px 12px', gap: '4px',
      backgroundColor: tokens.colorNeutralBackground1, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    },
  },
  navCta: { marginBottom: '12px', [NARROW]: { display: 'none' } },
  navGroup: {
    margin: '14px 12px 4px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold, letterSpacing: '0.06em', textTransform: 'uppercase',
    [NARROW]: { display: 'none' },
  },
  navItem: {
    position: 'relative', display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 12px', borderRadius: tokens.borderRadiusLarge,
    color: tokens.colorNeutralForeground2, textDecoration: 'none', fontSize: tokens.fontSizeBase300, whiteSpace: 'nowrap', flexShrink: 0,
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover, color: tokens.colorNeutralForeground1 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  navActive: {
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold,
    boxShadow: tokens.shadow2,
    '::before': {
      content: '""', position: 'absolute', left: '0', top: '8px', bottom: '8px', width: '3px', borderRadius: '2px',
      backgroundColor: tokens.colorBrandBackground,
    },
    [NARROW]: { backgroundColor: tokens.colorBrandBackground2, boxShadow: 'none', '::before': { display: 'none' } },
  },
  navCount: { marginLeft: 'auto' },
  main: { padding: '24px 28px 48px', maxWidth: '1280px', width: '100%', boxSizing: 'border-box', [NARROW]: { padding: '16px' } },
  solo: { padding: '24px 28px', maxWidth: '960px', margin: '0 auto', boxSizing: 'border-box' },
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

/** The page a route belongs to in the navigation (a request and its form sit under Requests). */
function section(route: Route): Route['page'] {
  return route.page === 'new' || route.page === 'request' || route.page === 'edit' ? 'requests' : route.page;
}

function Shell() {
  const s = useStyles();
  const route = useRoute();
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [projectId, setProjectId] = useState<string | null>(readStoredProject());
  const [queue, setQueue] = useState<ActionItem[] | null>(null);

  useEffect(() => {
    api.me().then(setMe).catch(setError);
  }, []);

  const refreshQueue = useCallback(() => {
    api.queue().then((r) => setQueue(r.items)).catch(() => undefined);
  }, []);
  // What waits on the signed-in person: read again on every page change, so the count in the navigation stays true.
  const routeKey = JSON.stringify(route);
  useEffect(() => {
    if (me && me.projects.length > 0) refreshQueue();
  }, [me, routeKey, refreshQueue]);
  const queueValue = useMemo(() => ({ items: queue, refresh: refreshQueue }), [queue, refreshQueue]);

  const project: ProjectSummary | undefined = useMemo(
    () => me?.projects.find((p) => p.projectId === projectId) ?? me?.projects[0],
    [me, projectId],
  );

  const choose = (id: string) => {
    setProjectId(id);
    storeProject(id);
  };

  const active = section(route);
  const roles = project?.roles ?? [];
  const has = (...r: AppRole[]) => r.some((x) => roles.includes(x));
  const waiting = queue?.length ?? 0;
  const item = (page: Route['page'], label: string, icon: React.ReactElement, count?: number) => (
    <a key={page} href={href({ page } as Route)} className={mergeClasses(s.navItem, active === page && s.navActive)} aria-current={active === page ? 'page' : undefined}>
      {icon}
      <span>{label}</span>
      {count ? <CounterBadge className={s.navCount} count={count} size="small" color="brand" aria-label={`${count} waiting on you`} /> : null}
    </a>
  );

  return (
    <QueueContext.Provider value={queueValue}>
      <div className={s.app}>
        <a className={s.skip} href="#main">Skip to content</a>
        <header className={s.header}>
          <a className={s.brand} href={href({ page: 'requests' })}>
            <span className={s.mark} aria-hidden="true"><ShieldKeyhole20Filled /></span>
            <span className={s.brandText}>
              <span className={s.brandName}>Onboarding requests</span>
              {project && <Caption1 className={s.tenant}>Tenant: <b>{project.tenantName || project.name}</b></Caption1>}
            </span>
          </a>
          <Badge appearance="tint" color={me?.appEnvironment === 'prod' ? 'danger' : 'informative'}>{(me?.appEnvironment ?? 'pilot').toUpperCase()}</Badge>
          <div className={s.grow} />
          {me && me.projects.length > 0 && project && (
            <Dropdown
              aria-label="Switch tenant"
              value={project.tenantName ? `${project.tenantName} · ${project.name}` : project.name}
              selectedOptions={[project.projectId]}
              onOptionSelect={(_, d) => d.optionValue && choose(d.optionValue)}
              className={s.switcher}
            >
              {me.projects.map((p) => (
                <Option key={p.projectId} value={p.projectId} text={p.tenantName ? `${p.tenantName} · ${p.name}` : p.name}>
                  {p.tenantName ? `Tenant ${p.tenantName} · ${p.name}` : p.name} ({p.roles.join(', ')})
                </Option>
              ))}
            </Dropdown>
          )}
          {me && (
            <div className={s.user}>
              <Avatar name={me.displayName || me.userId} size={32} color="colorful" aria-hidden="true" />
              <span className={s.userText}>
                <Body1Strong>{me.displayName || me.userId}</Body1Strong>
                {roles.length > 0 && <Caption1 className={s.roles}>{roles.join(' · ')}</Caption1>}
              </span>
            </div>
          )}
          <Button as="a" href="/logout" appearance="subtle" icon={<SignOut20Regular />}>Sign out</Button>
        </header>

        {/* Permanent, non-dismissible scope statement. */}
        <div className={s.scope} role="note">
          <ShieldCheckmark20Regular aria-hidden="true" />
          <span>{me?.agentProvisioning ? (me.agentTap ? AGENT_TAP_BANNER : AGENT_BANNER) : BANNER}</span>
        </div>
        {me?.mailEnabled && me.mailRedirectTo && (
          <MessageBar intent="warning" style={{ borderRadius: 0 }}>
            <MessageBarBody>Test environment: every notification mail goes to {me.mailRedirectTo} (the intended recipients are named in each mail).</MessageBarBody>
          </MessageBar>
        )}

        {me && project ? (
          <div className={s.body}>
            <nav className={s.nav} aria-label="Main">
              {has('Requester') && (
                <Button as="a" href={href({ page: 'new' })} appearance="primary" icon={<Add20Regular />} className={s.navCta}>New request</Button>
              )}
              <span className={s.navGroup}>Work</span>
              {item('requests', 'Requests', <DocumentBulletList20Regular />)}
              {item('queue', 'My actions', <TaskListSquareLtr20Regular />, waiting)}
              <span className={s.navGroup}>Reference</span>
              {item('sheet', 'Mapping sheet', <Table20Regular />)}
              {item('contacts', 'Team contacts', <People20Regular />)}
              {(has(...ESCALATION_ROLES) || has('DataSteward', 'PMO') || has('Operator', 'Security', 'PMO')) && <span className={s.navGroup}>Oversight</span>}
              {has(...ESCALATION_ROLES) && item('escalations', 'Escalations', <Warning20Regular />)}
              {has('DataSteward', 'PMO') && item('uploads', 'Data uploads', <ArrowUpload20Regular />)}
              {has('Operator', 'Security', 'PMO') && item('operator', 'Issues and reports', <DataBarVertical20Regular />)}
            </nav>
            <main id="main" className={s.main}>
              <Page route={route} project={project} projects={me.projects} onOpenProject={choose} agent={!!me.agentProvisioning} tap={!!me.agentTap} privileged={!!me.agentPrivileged} />
            </main>
          </div>
        ) : (
          <main id="main" className={s.solo}>
            <ErrorBar error={error} />
            {!me && !error && <Loading label="Signing in" />}
            {me && me.projects.length === 0 && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  You are signed in as <b>{me.displayName || me.userId}</b> but not registered for any project. Ask the pilot owner to register your
                  sign-in name{me.displayName ? <> <code>{me.displayName}</code></> : ''} (or this user id: <code>{me.userId}</code>).
                </MessageBarBody>
              </MessageBar>
            )}
          </main>
        )}
      </div>
    </QueueContext.Provider>
  );
}

function Page({ route, project, projects, onOpenProject, agent, tap, privileged }: {
  route: Route; project: ProjectSummary; projects: ProjectSummary[]; onOpenProject: (id: string) => void; agent: boolean; tap: boolean; privileged: boolean;
}) {
  switch (route.page) {
    case 'new':
      return <RequestFormPage key="new" project={project} projects={projects} onProject={onOpenProject} agent={agent} tap={tap} privileged={privileged} />;
    case 'edit':
      return <RequestFormPage key={route.requestId} project={project} requestId={route.requestId} agent={agent} tap={tap} privileged={privileged} />;
    case 'request':
      return <RequestDetailPage key={`${project.projectId}/${route.requestId}`} project={project} requestId={route.requestId} tap={agent && tap} />;
    case 'queue':
      return <QueuePage onOpen={onOpenProject} projects={projects} />;
    case 'sheet':
      return <MappingSheetPage key={project.projectId} project={project} />;
    case 'contacts':
      return <ContactsPage key={project.projectId} project={project} />;
    case 'escalations':
      return <EscalationsPage key={project.projectId} project={project} />;
    case 'uploads':
      return <DataUploadsPage key={project.projectId} project={project} />;
    case 'operator':
      return <OperatorPage project={project} />;
    default:
      return <RequestsPage key={project.projectId} project={project} projects={projects} onOpenProject={onOpenProject} />;
  }
}

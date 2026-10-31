import { useEffect } from 'react';
import {
  Badge, Body1, Body1Strong, Button, Caption1, makeStyles, mergeClasses, shorthands, tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwise20Regular, ArrowRight16Regular, CheckmarkCircle20Filled, Clock16Regular, Edit20Regular, People20Regular,
  PersonAvailable20Regular, ShieldCheckmark20Regular, Wrench20Regular,
} from '@fluentui/react-icons';
import { href } from '../lib/router';
import type { ActionItem, ActionName, ProjectSummary } from '../lib/types';
import { statusColor } from '../lib/format';
import { useActionQueue } from '../lib/queue';
import { journey, waitingLong } from '../lib/tracker';
import { JourneyDots } from '../components/Journey';
import { Breakable, Loading, PageHeader, useStyles } from '../components/common';

export const ACTION_LABEL: Record<ActionName, string> = {
  ApproveOwner: 'Owner decision',
  ApproveSecurity: 'Security decision',
  IdTeamTask: 'ID team task',
  RoleTeamTask: 'Role assignment task',
  Resubmit: 'Correct and resubmit',
};

/** What the person does on the request page, in the words of the button that takes them there. */
const OPEN_LABEL: Record<ActionName, string> = {
  ApproveOwner: 'Open to decide',
  ApproveSecurity: 'Open to decide',
  IdTeamTask: 'Open the task',
  RoleTeamTask: 'Open the task',
  Resubmit: 'Open and correct',
};

const ICON: Record<ActionName, React.ReactElement> = {
  ApproveOwner: <PersonAvailable20Regular />,
  ApproveSecurity: <ShieldCheckmark20Regular />,
  IdTeamTask: <People20Regular />,
  RoleTeamTask: <Wrench20Regular />,
  Resubmit: <Edit20Regular />,
};

const ORDER: ActionName[] = ['ApproveOwner', 'ApproveSecurity', 'IdTeamTask', 'RoleTeamTask', 'Resubmit'];

const useLocal = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: '12px' },
  card: {
    display: 'grid', gridTemplateColumns: '40px minmax(0, 1fr)', gap: '4px 14px', padding: '16px 18px',
    borderRadius: tokens.borderRadiusXLarge, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    ':hover': { boxShadow: tokens.shadow8, ...shorthands.borderColor(tokens.colorBrandStroke2) },
  },
  cardLong: { borderLeftWidth: '4px', borderLeftColor: tokens.colorStatusWarningBorder1 },
  icon: {
    gridRow: 'span 4', width: '40px', height: '40px', borderRadius: tokens.borderRadiusLarge, display: 'grid', placeItems: 'center',
    backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1,
  },
  head: { display: 'flex', flexWrap: 'wrap', alignItems: 'baseline', gap: '4px 10px' },
  id: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorNeutralForeground3 },
  meta: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px 12px', marginTop: '6px' },
  long: { color: tokens.colorStatusWarningForeground1, fontWeight: tokens.fontWeightSemibold },
  foot: { display: 'flex', justifyContent: 'flex-end', marginTop: '8px' },
  done: { alignItems: 'center', textAlign: 'center', padding: '40px 20px' },
  doneIcon: { color: tokens.colorStatusSuccessForeground1, transform: 'scale(1.8)', marginBottom: '8px' },
  groupHead: { display: 'flex', alignItems: 'center', gap: '8px', margin: '8px 0 0' },
});

/** Everything waiting on the signed-in person, across their projects (computed by the server), as an inbox. */
export function QueuePage({ onOpen, projects = [] }: { onOpen: (projectId: string) => void; projects?: ProjectSummary[] }) {
  const s = useStyles();
  const l = useLocal();
  const queue = useActionQueue();
  const items = queue.items;
  // Read fresh when the page opens (the navigation count reads it too).
  useEffect(() => queue.refresh(), [queue.refresh]);
  const tenant = (id: string) => {
    const p = projects.find((x) => x.projectId === id);
    return p ? p.tenantName || p.name : id;
  };
  const severalTenants = new Set((items ?? []).map((i) => i.status.projectId)).size > 1;

  return (
    <div className={s.stack}>
      <PageHeader title="My actions" actions={<Button icon={<ArrowClockwise20Regular />} onClick={queue.refresh}>Refresh</Button>}>
        Decisions and tasks waiting on you, in every tenant you work in. The longest wait is first in each group.
      </PageHeader>
      {items === null ? (
        <Loading />
      ) : items.length === 0 ? (
        <section className={mergeClasses(s.section, l.done)}>
          <CheckmarkCircle20Filled className={l.doneIcon} aria-hidden="true" />
          <Body1Strong>Nothing is waiting on you.</Body1Strong>
          <Body1 className={s.muted}>New decisions and tasks show up here, and as a count in the navigation.</Body1>
        </section>
      ) : (
        ORDER.filter((a) => items.some((i) => i.action === a)).map((action) => {
          const group = items.filter((i) => i.action === action).sort((a, b) => b.status.timeInStageSeconds - a.status.timeInStageSeconds);
          return (
            <section key={action} aria-labelledby={`q-${action}`} className={s.stack} style={{ gap: 10 }}>
              <div className={l.groupHead}>
                <Body1Strong as="h2" id={`q-${action}`} style={{ margin: 0 }}>{ACTION_LABEL[action]}</Body1Strong>
                <Badge appearance="tint" color="brand">{group.length}</Badge>
              </div>
              <div className={l.grid}>
                {group.map((i) => <Card key={`${i.status.requestId}-${i.action}`} item={i} tenant={severalTenants ? tenant(i.status.projectId) : null} onOpen={onOpen} />)}
              </div>
            </section>
          );
        })
      )}
    </div>
  );
}

function Card({ item, tenant, onOpen }: { item: ActionItem; tenant: string | null; onOpen: (projectId: string) => void }) {
  const s = useStyles();
  const l = useLocal();
  const st = item.status;
  const long = waitingLong(st);
  return (
    <article className={mergeClasses(l.card, long && l.cardLong)} aria-label={`${ACTION_LABEL[item.action]} ${st.requestId.slice(0, 8)}`}>
      <span className={l.icon} aria-hidden="true">{ICON[item.action]}</span>
      <div className={l.head}>
        <Body1Strong className={s.wrap}><Breakable text={st.domainId} /></Body1Strong>
        <Caption1 className={l.id}>{st.requestId.slice(0, 8)}</Caption1>
      </div>
      <Body1 className={s.wrap}>{st.resource ? `${st.accessLevel} on ${st.resource}` : 'v1 request'}{tenant ? ` · ${tenant}` : ''}</Body1>
      <div className={l.meta}>
        <Badge appearance="filled" color={statusColor[st.status]}>{st.currentStage}</Badge>
        <Caption1 className={long ? l.long : s.muted}><Clock16Regular aria-hidden="true" style={{ verticalAlign: 'text-bottom' }} /> {st.timeInStage} waiting</Caption1>
        <Caption1 className={s.muted}>from <Breakable text={st.requester} /></Caption1>
      </div>
      <div>
        <JourneyDots steps={journey(st.status, st.done)} />
        <div className={l.foot}>
          <Button as="a" appearance="primary" size="small" href={href({ page: 'request', requestId: st.requestId })} onClick={() => onOpen(st.projectId)}
            icon={<ArrowRight16Regular />} iconPosition="after">
            {OPEN_LABEL[item.action]}
          </Button>
        </div>
      </div>
    </article>
  );
}

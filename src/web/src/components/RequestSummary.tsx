import { Badge, Body1, Body1Strong, Caption1, ProgressBar, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { CheckmarkCircle16Filled, Circle16Regular } from '@fluentui/react-icons';
import { Breakable, useStyles as useCommon } from './common';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '6px', margin: 0, padding: 0, listStyle: 'none' },
  item: { display: 'flex', alignItems: 'flex-start', gap: '8px', '& svg': { flexShrink: 0, marginTop: '2px' } },
  ok: { color: tokens.colorStatusSuccessForeground1 },
  todo: { color: tokens.colorNeutralForeground3 },
  groups: { display: 'flex', flexDirection: 'column', gap: '4px', margin: 0, padding: 0, listStyle: 'none' },
  group: {
    display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '8px', padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2,
  },
  ready: { color: tokens.colorStatusSuccessForeground1, fontWeight: tokens.fontWeightSemibold },
});

export interface ReadinessItem {
  label: string;
  ok: boolean;
  /** What is still missing, in a few words. */
  hint?: string;
}

/** A live summary of the request being written, and what it still needs before it can go to the AI review. */
export function RequestSummary({ joiner, mail, signIn, tenant, environment, groups, owner, items, count }: {
  joiner: string;
  mail: string;
  signIn: string;
  tenant: string;
  environment: string;
  groups: { name: string; level: string }[];
  owner: string;
  items: ReadinessItem[];
  /** How many requests submitting creates (one per group). */
  count: number;
}) {
  const s = useStyles();
  const c = useCommon();
  const done = items.filter((i) => i.ok).length;
  const all = done === items.length;
  return (
    <section className={c.section} aria-labelledby="summary-card-h">
      <Body1Strong as="h2" id="summary-card-h" style={{ margin: 0 }}>Your request</Body1Strong>
      <dl className={c.facts}>
        <dt><Caption1>Joiner</Caption1></dt><dd><Body1 className={c.wrap}>{joiner || '—'}</Body1>{mail && <Caption1 block className={mergeClasses(c.muted, c.wrap)}><Breakable text={mail} /></Caption1>}</dd>
        {signIn && <><dt><Caption1>Sign-in name</Caption1></dt><dd><Caption1 className={c.wrap}><Breakable text={signIn} /></Caption1></dd></>}
        <dt><Caption1>Where</Caption1></dt><dd><Body1>{tenant}{environment ? ` · ${environment}` : ''}</Body1></dd>
        <dt><Caption1>Decided by</Caption1></dt><dd><Body1 className={c.wrap}>{owner ? <Breakable text={owner} /> : '—'}</Body1></dd>
      </dl>
      {groups.length > 0 && (
        <>
          <Caption1 className={c.eyebrow}>Access ({groups.length})</Caption1>
          <ul className={s.groups}>
            {groups.map((g) => (
              <li key={g.name} className={s.group}>
                <Caption1 className={c.wrap}>{g.name}</Caption1>
                <Badge appearance="tint" color="brand" size="small">{g.level}</Badge>
              </li>
            ))}
          </ul>
        </>
      )}
      <div className={c.row} style={{ justifyContent: 'space-between', marginTop: 4 }}>
        <Caption1 className={c.eyebrow}>Ready to submit</Caption1>
        <Caption1 className={all ? s.ready : c.muted}>{done} of {items.length}</Caption1>
      </div>
      <ProgressBar value={done / Math.max(1, items.length)} color={all ? 'success' : 'brand'} thickness="large" aria-hidden="true" />
      <ul className={s.list} aria-label="What the request still needs">
        {items.map((i) => (
          <li key={i.label} className={s.item}>
            {i.ok ? <CheckmarkCircle16Filled className={s.ok} aria-hidden="true" /> : <Circle16Regular className={s.todo} aria-hidden="true" />}
            <span>
              <Body1>{i.label}</Body1><span className={c.muted}>{i.ok ? ' — done' : ''}</span>
              {!i.ok && i.hint && <Caption1 block className={c.muted}>{i.hint}</Caption1>}
            </span>
          </li>
        ))}
      </ul>
      {count > 1 && <Caption1 className={c.muted}>Submitting creates {count} requests, one per group; each goes through the whole flow.</Caption1>}
    </section>
  );
}

import { useState } from 'react';
import {
  Badge, Body1, Button, Caption1, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components';
import { CheckmarkCircle20Filled, ChevronDown16Regular } from '@fluentui/react-icons';
import type { CheckResult, CheckStatus } from '../lib/types';
import { checkColor, checkLabel } from '../lib/format';
import { CHECK_ORDER, digest } from '../lib/tracker';
import { useStyles as useCommon } from './common';

const useStyles = makeStyles({
  bar: { display: 'flex', height: '10px', borderRadius: '5px', overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3 },
  Pass: { backgroundColor: tokens.colorStatusSuccessBackground3 },
  Fail: { backgroundColor: tokens.colorStatusDangerBackground3 },
  Flagged: { backgroundColor: tokens.colorStatusWarningBackground3 },
  Pending: { backgroundColor: tokens.colorBrandBackground },
  NotApplicable: { backgroundColor: tokens.colorNeutralStrokeAccessible },
  legend: { display: 'flex', flexWrap: 'wrap', gap: '4px 16px' },
  key: { display: 'inline-flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground2 },
  swatch: { width: '10px', height: '10px', borderRadius: '3px' },
  list: { display: 'flex', flexDirection: 'column', margin: 0, padding: 0, listStyle: 'none', gap: '2px' },
  item: {
    display: 'grid', gridTemplateColumns: '88px minmax(0, 1fr)', gap: '4px 12px', alignItems: 'start', padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2,
  },
  allPass: { display: 'flex', alignItems: 'center', gap: '8px', color: tokens.colorStatusSuccessForeground1 },
});

const NOUN: Record<CheckStatus, string> = { Pass: 'passed', Fail: 'failed', Flagged: 'risk flags', Pending: 'pending', NotApplicable: 'not checked' };

/** Full table of checks (group, code, result, detail). */
export function ChecksTable({ checks, label }: { checks: CheckResult[]; label: string }) {
  const s = useCommon();
  return (
    <div className={s.tableWrap}>
      <Table aria-label={label} size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell style={{ width: '18%' }}>Group</TableHeaderCell>
            <TableHeaderCell style={{ width: '22%' }}>Check</TableHeaderCell>
            <TableHeaderCell style={{ width: '14%' }}>Result</TableHeaderCell>
            <TableHeaderCell>Detail</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {checks.map((c) => (
            <TableRow key={c.code}>
              <TableCell><Caption1>{c.group}</Caption1></TableCell>
              <TableCell><Caption1 className={s.wrap}>{c.code}</Caption1></TableCell>
              <TableCell><Badge color={checkColor[c.status]}>{checkLabel[c.status]}</Badge></TableCell>
              <TableCell><span className={s.pre}>{c.detail}</span></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

/**
 * The checks as a reader needs them: one bar for the whole result, then only what did not pass (failed first), and
 * the full list one click away. Nothing is hidden from the record: every check stays in the table.
 */
export function CheckDigest({ checks, label }: { checks: CheckResult[]; label: string }) {
  const s = useStyles();
  const c = useCommon();
  const [all, setAll] = useState(false);
  const d = digest(checks);
  if (d.total === 0) return null;
  return (
    <div className={c.stack} style={{ gap: 12 }}>
      <div className={s.bar} role="img" aria-label={`${label}: ${CHECK_ORDER.filter((k) => d.counts[k]).map((k) => `${d.counts[k]} ${NOUN[k]}`).join(', ')}`}>
        {CHECK_ORDER.filter((k) => d.counts[k] > 0).map((k) => (
          <span key={k} className={s[k]} style={{ width: `${(100 * d.counts[k]) / d.total}%` }} />
        ))}
      </div>
      <div className={s.legend}>
        {CHECK_ORDER.filter((k) => d.counts[k] > 0).map((k) => (
          <Caption1 key={k} className={s.key}><span className={mergeClasses(s.swatch, s[k])} />{d.counts[k]} {NOUN[k]}</Caption1>
        ))}
      </div>
      {d.attention.length === 0 ? (
        <Body1 className={s.allPass}><CheckmarkCircle20Filled />All {d.total} checks passed.</Body1>
      ) : (
        <ul className={s.list} aria-label={`${label}: what did not pass`}>
          {d.attention.map((x) => (
            <li key={x.code} className={s.item}>
              <span><Badge color={checkColor[x.status]}>{checkLabel[x.status]}</Badge></span>
              <span>
                <Body1 className={c.pre}>{x.detail}</Body1>
                <Caption1 block className={c.muted}>{x.group} · {x.code}</Caption1>
              </span>
            </li>
          ))}
        </ul>
      )}
      <div>
        <Button appearance="subtle" size="small" icon={<ChevronDown16Regular style={{ transform: all ? 'rotate(180deg)' : undefined }} />}
          aria-expanded={all} onClick={() => setAll((v) => !v)}>
          {all ? 'Hide the full list' : `Show all ${d.total} checks`}
        </Button>
      </div>
      {all && <ChecksTable checks={checks} label={label} />}
    </div>
  );
}

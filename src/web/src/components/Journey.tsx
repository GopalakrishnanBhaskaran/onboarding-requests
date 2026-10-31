import { Caption1, Tooltip, makeStyles, mergeClasses, shorthands, tokens } from '@fluentui/react-components';
import { ArrowUndo16Filled, Checkmark16Filled, Dismiss16Filled, ErrorCircle16Filled } from '@fluentui/react-icons';
import { duration, journeyText, type JourneyStep, type StepState } from '../lib/tracker';

const useStyles = makeStyles({
  steps: {
    display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', margin: 0, padding: 0, listStyle: 'none',
    overflowX: 'auto', minWidth: 0,
  },
  step: { display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', gap: '6px', minWidth: '64px' },
  track: { display: 'flex', alignItems: 'center', width: '100%' },
  line: { flexGrow: 1, height: '3px', borderRadius: '2px', backgroundColor: tokens.colorNeutralStroke2 },
  lineDone: { backgroundColor: tokens.colorStatusSuccessBackground3 },
  lineHidden: { visibility: 'hidden' },
  marker: {
    flexShrink: 0, width: '30px', height: '30px', borderRadius: '50%', display: 'grid', placeItems: 'center', boxSizing: 'border-box',
    fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold,
    border: `2px solid ${tokens.colorNeutralStroke1}`, color: tokens.colorNeutralForeground3, backgroundColor: tokens.colorNeutralBackground1,
  },
  done: { border: 'none', backgroundColor: tokens.colorStatusSuccessBackground3, color: tokens.colorNeutralForegroundOnBrand },
  current: {
    ...shorthands.borderColor(tokens.colorBrandStroke1), backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1,
    animationName: {
      '0%': { boxShadow: `0 0 0 0 ${tokens.colorBrandStroke2}` },
      '70%': { boxShadow: '0 0 0 8px transparent' },
      '100%': { boxShadow: '0 0 0 0 transparent' },
    },
    animationDuration: '2s', animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
  },
  returned: { ...shorthands.borderColor(tokens.colorStatusWarningBorder1), backgroundColor: tokens.colorStatusWarningBackground1, color: tokens.colorStatusWarningForeground1 },
  stopped: { ...shorthands.borderColor(tokens.colorStatusDangerBorder1), backgroundColor: tokens.colorStatusDangerBackground1, color: tokens.colorStatusDangerForeground1 },
  label: { fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200, color: tokens.colorNeutralForeground2 },
  labelNow: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1 },
  time: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  dots: { display: 'flex', gap: '3px', width: '100%', maxWidth: '150px', marginTop: '6px' },
  dot: { flexGrow: 1, height: '5px', borderRadius: '3px', backgroundColor: tokens.colorNeutralStroke2 },
  dotDone: { backgroundColor: tokens.colorStatusSuccessBackground3 },
  dotNow: { backgroundColor: tokens.colorBrandBackground },
  dotReturned: { backgroundColor: tokens.colorStatusWarningBackground3 },
  dotStopped: { backgroundColor: tokens.colorStatusDangerBackground3 },
});

const WORD: Record<StepState, string> = {
  done: 'done', current: 'in progress', upcoming: 'to come', returned: 'returned to the requester', stopped: 'stopped here', failed: 'needs a developer',
};

function Marker({ step, index }: { step: JourneyStep; index: number }) {
  const s = useStyles();
  const cls = mergeClasses(
    s.marker,
    step.state === 'done' && s.done,
    step.state === 'current' && s.current,
    step.state === 'returned' && s.returned,
    (step.state === 'stopped' || step.state === 'failed') && s.stopped,
  );
  const icon = step.state === 'done' ? <Checkmark16Filled />
    : step.state === 'returned' ? <ArrowUndo16Filled />
      : step.state === 'stopped' ? <Dismiss16Filled />
        : step.state === 'failed' ? <ErrorCircle16Filled />
          : index + 1;
  return <span className={cls} aria-hidden="true">{icon}</span>;
}

/** The 7 steps of the flow, where this request stands, and the time each step took so far. */
export function JourneyStepper({ steps, seconds }: { steps: JourneyStep[]; seconds?: (number | null)[] }) {
  const s = useStyles();
  return (
    <ol className={s.steps} aria-label={`Flow progress. ${journeyText(steps)}`}>
      {steps.map((step, i) => {
        const now = step.state !== 'done' && step.state !== 'upcoming';
        const t = seconds?.[i];
        return (
          <li key={step.key} className={s.step} aria-current={now ? 'step' : undefined}>
            <span className={s.track}>
              <span className={mergeClasses(s.line, i === 0 && s.lineHidden, i > 0 && steps[i - 1].state === 'done' && s.lineDone)} />
              <Tooltip content={`${step.detail} — ${WORD[step.state]}`} relationship="description" withArrow>
                <span tabIndex={0} style={{ borderRadius: '50%' }}><Marker step={step} index={i} /></span>
              </Tooltip>
              <span className={mergeClasses(s.line, i === steps.length - 1 && s.lineHidden, step.state === 'done' && s.lineDone)} />
            </span>
            <span className={mergeClasses(s.label, now && s.labelNow)}>{step.label}</span>
            {t != null && (step.state !== 'upcoming') && <Caption1 className={s.time}>{duration(t)}</Caption1>}
          </li>
        );
      })}
    </ol>
  );
}

/** Seven small bars: the same journey at a glance, for a list row. */
export function JourneyDots({ steps }: { steps: JourneyStep[] }) {
  const s = useStyles();
  return (
    <span className={s.dots} role="img" aria-label={journeyText(steps)} title={journeyText(steps)}>
      {steps.map((step) => (
        <span key={step.key} className={mergeClasses(
          s.dot,
          step.state === 'done' && s.dotDone,
          step.state === 'current' && s.dotNow,
          step.state === 'returned' && s.dotReturned,
          (step.state === 'stopped' || step.state === 'failed') && s.dotStopped,
        )} />
      ))}
    </span>
  );
}

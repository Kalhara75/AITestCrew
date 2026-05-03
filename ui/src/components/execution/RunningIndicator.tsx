/**
 * Canonical spinning indicator for active execution states (Running / Queued / Claimed).
 * Replaces the ad-hoc CSS ring divs in TriggerRunButton and TriggerObjectiveRunButton.
 *
 * Size prop controls the pixel diameter:
 *   sm  = 12px  (compact — inside a table cell or next to a label)
 *   md  = 16px  (default — button-level)
 *   lg  = 20px  (prominent — e.g. banner)
 */

const SIZE_MAP = {
  sm: 12,
  md: 16,
  lg: 20,
} as const;

/** Base colour = border base; top = accent (spinning arc colour). */
const STATE_COLORS: Record<'queued' | 'running', { base: string; top: string }> = {
  queued:  { base: '#fde68a', top: '#b45309' },
  running: { base: '#bfdbfe', top: '#2563eb' },
};

type IndicatorState = 'queued' | 'running';

interface Props {
  state?: IndicatorState;
  size?: keyof typeof SIZE_MAP;
}

export function RunningIndicator({ state = 'running', size = 'md' }: Props) {
  const px = SIZE_MAP[size];
  const { base, top } = STATE_COLORS[state];
  const border = Math.max(1.5, px / 6);
  return (
    <>
      <div style={{
        width: px,
        height: px,
        border: `${border}px solid ${base}`,
        borderTop: `${border}px solid ${top}`,
        borderRadius: '50%',
        animation: 'exec-spin 0.8s linear infinite',
        flexShrink: 0,
      }} />
      <style>{`@keyframes exec-spin { to { transform: rotate(360deg); } }`}</style>
    </>
  );
}

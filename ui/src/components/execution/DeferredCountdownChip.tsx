import { useEffect, useState } from 'react';

/**
 * Single canonical chip that shows a live countdown to a deferred
 * verification due time. Used by StepList, QueueBanner, and any future
 * surface so the format is identical everywhere.
 *
 * Format rule:
 *   > 2 minutes remaining → "in ~Nm"  (rounded to nearest minute)
 *   <= 2 minutes remaining → "in Xm Ys"  (precise)
 *   overdue / awaiting claim → "awaiting claim"
 */

interface Props {
  /** The target due time (UTC). */
  target: Date;
  /** Optional class-like size override. Default: 'sm'. */
  size?: 'sm' | 'md';
}

export function DeferredCountdownChip({ target, size = 'sm' }: Props) {
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 1000);
    return () => clearInterval(id);
  }, []);
  // Force re-render on each tick
  void tick;

  const diffMs = target.getTime() - Date.now();

  const fontSize = size === 'md' ? 12 : 11;

  if (diffMs <= 0) {
    return (
      <span style={{
        background: '#fef9c3', color: '#854d0e',
        padding: '2px 8px', borderRadius: 10,
        fontSize, fontWeight: 600,
      }}>
        awaiting claim
      </span>
    );
  }

  const totalSeconds = Math.floor(diffMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;

  const label = minutes > 2
    ? `in ~${minutes}m`
    : minutes > 0
      ? `in ${minutes}m ${seconds}s`
      : `in ${seconds}s`;

  return (
    <span style={{
      background: '#cffafe', color: '#0e7490',
      padding: '2px 8px', borderRadius: 10,
      fontSize, fontWeight: 600,
      fontFamily: 'ui-monospace, Consolas, monospace',
    }}>
      {label}
    </span>
  );
}

import type { CSSProperties } from 'react';

/**
 * Canonical stats component for run/execution summaries.
 * Used by both RunHistoryTable (compact) and ExecutionDetailPage (full grid).
 *
 * size="sm"  → single row of pills, suitable for a table cell
 * size="lg"  → grid of stat boxes with large numbers
 */

interface StatBarProps {
  passed: number;
  failed: number;
  total: number;
  errors?: number;
  duration?: string;
  startedAt?: string;
  size?: 'sm' | 'lg';
}

export function StatsBar({
  passed, failed, total, errors, duration, startedAt, size = 'sm',
}: StatBarProps) {
  if (size === 'lg') {
    return (
      <div style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))',
        gap: 16,
        paddingTop: 20,
        borderTop: '1px solid #f1f5f9',
      }}>
        <StatBox label="Total Objectives" value={total} />
        <StatBox label="Passed" value={passed} color="#16a34a" />
        <StatBox label="Failed" value={failed} color={failed > 0 ? '#dc2626' : undefined} />
        {errors !== undefined && errors > 0 && (
          <StatBox label="Errors" value={errors} color="#d97706" />
        )}
        {duration !== undefined && (
          <StatBox label="Duration" value={duration} mono />
        )}
        {startedAt !== undefined && (
          <StatBox label="Started" value={new Date(startedAt).toLocaleString()} />
        )}
      </div>
    );
  }

  // size='sm': compact pill row
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 13 }}>
      <span style={{ color: '#16a34a', fontWeight: 600 }}>{passed}</span>
      <span style={{ color: '#94a3b8' }}>/ {total}</span>
      {failed > 0 && (
        <span style={{ color: '#dc2626', fontSize: 12, marginLeft: 2 }}>({failed} failed)</span>
      )}
    </span>
  );
}

function StatBox({
  label, value, color, mono,
}: {
  label: string;
  value: string | number;
  color?: string;
  mono?: boolean;
}) {
  const isString = typeof value === 'string';
  const style: CSSProperties = {
    fontSize: isString ? 13 : 18,
    fontWeight: 700,
    color: color ?? '#1e293b',
    ...(mono || isString
      ? { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13, fontWeight: 600 }
      : {}),
  };
  return (
    <div style={{ background: '#f8fafc', padding: '12px 16px', borderRadius: 8 }}>
      <div style={{
        fontSize: 11, color: '#94a3b8', marginBottom: 4,
        textTransform: 'uppercase', letterSpacing: 0.5, fontWeight: 600,
      }}>
        {label}
      </div>
      <div style={style}>{value}</div>
    </div>
  );
}

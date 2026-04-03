import type { CSSProperties } from 'react';

const colors: Record<string, { bg: string; fg: string; border: string }> = {
  Passed:  { bg: '#dcfce7', fg: '#166534', border: '#bbf7d0' },
  Failed:  { bg: '#fee2e2', fg: '#991b1b', border: '#fecaca' },
  Error:   { bg: '#fef3c7', fg: '#92400e', border: '#fde68a' },
  Skipped: { bg: '#f1f5f9', fg: '#475569', border: '#e2e8f0' },
  Running: { bg: '#dbeafe', fg: '#1e40af', border: '#bfdbfe' },
};

export function StatusBadge({ status, size = 'sm' }: { status: string | null; size?: 'sm' | 'md' }) {
  if (!status) return <span style={{ ...baseStyle, background: '#f1f5f9', color: '#94a3b8', border: '1px solid #e2e8f0' }}>No runs</span>;
  const c = colors[status] || colors.Skipped;
  const style: CSSProperties = {
    ...baseStyle,
    backgroundColor: c.bg,
    color: c.fg,
    border: `1px solid ${c.border}`,
    ...(size === 'md' ? { padding: '4px 14px', fontSize: 13 } : {}),
  };
  return <span style={style}>{status}</span>;
}

const baseStyle: CSSProperties = {
  display: 'inline-block',
  padding: '2px 10px',
  borderRadius: 12,
  fontSize: 12,
  fontWeight: 600,
  letterSpacing: 0.3,
  whiteSpace: 'nowrap',
  lineHeight: '20px',
};

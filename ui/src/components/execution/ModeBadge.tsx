import type { CSSProperties } from 'react';

/**
 * Canonical badge for run modes (Reuse / Rebaseline / VerifyOnly / Normal).
 * Replaces the duplicated inline-style palettes in RunHistoryTable and
 * ExecutionDetailPage.
 */

const MODE_PALETTE: Record<string, { bg: string; fg: string }> = {
  Reuse:      { bg: '#f0f9ff', fg: '#0369a1' },
  Rebaseline: { bg: '#fff7ed', fg: '#c2410c' },
  VerifyOnly: { bg: '#f0fdf4', fg: '#166534' },
};

const DEFAULT_PALETTE = { bg: '#f8fafc', fg: '#475569' };

const baseStyle: CSSProperties = {
  display: 'inline-block',
  fontSize: 12,
  fontWeight: 500,
  padding: '2px 8px',
  borderRadius: 4,
  whiteSpace: 'nowrap',
};

export function ModeBadge({ mode }: { mode: string }) {
  const p = MODE_PALETTE[mode] ?? DEFAULT_PALETTE;
  return (
    <span style={{ ...baseStyle, background: p.bg, color: p.fg }}>
      {mode}
    </span>
  );
}

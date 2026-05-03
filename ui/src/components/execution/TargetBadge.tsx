import type { CSSProperties } from 'react';

/**
 * Canonical badge for UI target types (Desktop / Blazor / MVC / API / DB / etc.).
 * Replaces the duplicate `targetBadge` / `targetBadgeStyle` helper functions
 * scattered across PostStepsPanel.tsx and AseXmlDeliveryTestCaseTable.tsx.
 */

const PALETTE_RULES: Array<{ test: (t: string) => boolean; bg: string; fg: string; border: string }> = [
  { test: t => t.includes('Desktop'),              bg: '#ecfdf5', fg: '#047857', border: '#a7f3d0' },
  { test: t => t.includes('Blazor') || t.includes('Web_Blazor'), bg: '#eff6ff', fg: '#1d4ed8', border: '#bfdbfe' },
  { test: t => t.includes('MVC'),                  bg: '#fef3c7', fg: '#92400e', border: '#fde68a' },
  { test: t => t.includes('Db_'),                  bg: '#fae8ff', fg: '#86198f', border: '#f5d0fe' },
  { test: t => t.includes('API') || t.includes('REST'), bg: '#fef2f2', fg: '#991b1b', border: '#fecaca' },
];

const FALLBACK = { bg: '#f1f5f9', fg: '#334155', border: '#cbd5e1' };

const baseStyle: CSSProperties = {
  fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
};

export function TargetBadge({ target }: { target: string }) {
  const p = PALETTE_RULES.find(r => r.test(target)) ?? FALLBACK;
  return (
    <span style={{ ...baseStyle, background: p.bg, color: p.fg, border: `1px solid ${p.border}` }}>
      {target}
    </span>
  );
}

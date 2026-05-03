// Chat-local design tokens. NOT app-wide — values mirror the existing slate
// palette already used across panels (AgentsPanel, QueueBanner, etc.) so the
// chat surface visually aligns without disturbing other components.

export const tokens = {
  color: {
    bg:           '#ffffff',
    surface:      '#f8fafc',
    surfaceAlt:   '#f1f5f9',
    border:       '#e2e8f0',
    borderStrong: '#cbd5e1',
    text:         '#0f172a',
    textMuted:    '#475569',
    textSubtle:   '#64748b',
    textFaint:    '#94a3b8',
    primary:      '#0f172a',
    primaryFg:    '#ffffff',
    accent:       '#1d4ed8',
    accentBg:     '#eff6ff',
    accentBorder: '#bfdbfe',
    success:      '#065f46',
    successBg:    '#ecfdf5',
    successBorder:'#a7f3d0',
    warning:      '#92400e',
    warningBg:    '#fffbeb',
    warningBorder:'#fde68a',
    danger:       '#991b1b',
    dangerBg:     '#fef2f2',
    dangerBorder: '#fecaca',
    runTint:      '#10b981',
    createTint:   '#3b82f6',
    recordTint:   '#ef4444',
    postTint:     '#8b5cf6',
    dataTint:     '#0ea5e9',
    navTint:      '#1d4ed8',
  },
  space: { 0: 0, 1: 2, 2: 4, 3: 6, 4: 8, 5: 10, 6: 12, 7: 16, 8: 20, 9: 24, 10: 32 },
  radius: { sm: 4, md: 6, lg: 8, xl: 12, pill: 999 },
  shadow: {
    sm: '0 1px 2px rgba(15,23,42,0.06)',
    md: '0 2px 8px rgba(15,23,42,0.06)',
    lg: '0 6px 20px rgba(15,23,42,0.10)',
  },
  font: {
    sans: 'inherit',
    mono: 'ui-monospace,Menlo,monospace',
    size:   { xs: 11, sm: 12, md: 13, lg: 14, xl: 16 },
    weight: { regular: 400, medium: 500, semi: 600, bold: 700 },
    lineHeight: { tight: 1.35, body: 1.5, loose: 1.6 },
  },
  motion: {
    fast: 120,
    base: 180,
    slow: 260,
    ease: 'cubic-bezier(0.4, 0, 0.2, 1)',
  },
  layout: {
    drawerMin:         360,
    drawerMax:         900,
    drawerDefault:     420,
    drawerWide:        720,
    composerMinPx:     80,
    composerMaxPx:     360,
    composerDefaultPx: 96,
    headerHeight:      56,
  },
} as const;

export type Tokens = typeof tokens;

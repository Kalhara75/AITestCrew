import type { CSSProperties } from 'react';
import { tokens } from './tokens';

export const iconBtnStyle: CSSProperties = {
  background: 'transparent',
  border: `1px solid ${tokens.color.border}`,
  color: tokens.color.textMuted,
  fontSize: tokens.font.size.xs,
  padding: '3px 8px',
  borderRadius: tokens.radius.sm,
  cursor: 'pointer',
  display: 'inline-flex',
  alignItems: 'center',
  gap: 4,
  lineHeight: 1,
};

export const ghostBtnStyle: CSSProperties = {
  background: 'transparent',
  border: 'none',
  color: tokens.color.textSubtle,
  fontSize: tokens.font.size.sm,
  padding: '4px 6px',
  borderRadius: tokens.radius.sm,
  cursor: 'pointer',
  display: 'inline-flex',
  alignItems: 'center',
  gap: 4,
};

export const primaryBtnStyle: CSSProperties = {
  background: tokens.color.primary,
  color: tokens.color.primaryFg,
  border: 'none',
  padding: '6px 14px',
  borderRadius: tokens.radius.md,
  fontSize: tokens.font.size.md,
  fontWeight: tokens.font.weight.medium,
  cursor: 'pointer',
};

export const linkBtnStyle: CSSProperties = {
  background: 'transparent',
  border: 'none',
  color: tokens.color.success,
  padding: 0,
  fontSize: tokens.font.size.md,
  fontWeight: tokens.font.weight.semi,
  cursor: 'pointer',
  textDecoration: 'underline',
};

export const errorLineStyle: CSSProperties = {
  marginTop: 6,
  fontSize: tokens.font.size.sm,
  color: tokens.color.danger,
};

export const successCardStyle: CSSProperties = {
  background: tokens.color.successBg,
  border: `1px solid ${tokens.color.successBorder}`,
  borderRadius: tokens.radius.lg,
  padding: 10,
  color: tokens.color.success,
  fontSize: tokens.font.size.md,
};

export const errorCardStyle: CSSProperties = {
  background: tokens.color.dangerBg,
  border: `1px solid ${tokens.color.dangerBorder}`,
  borderRadius: tokens.radius.lg,
  padding: 10,
  color: tokens.color.danger,
  fontSize: tokens.font.size.md,
};

export const confirmHeaderStyle: CSSProperties = {
  fontSize: tokens.font.size.xs,
  fontWeight: tokens.font.weight.bold,
  color: tokens.color.textMuted,
  textTransform: 'uppercase',
  letterSpacing: 0.5,
  marginBottom: 6,
  display: 'flex',
  alignItems: 'center',
  gap: 6,
};

export const bubbleUserStyle: CSSProperties = {
  background: tokens.color.primary,
  color: tokens.color.primaryFg,
  padding: '8px 12px',
  borderRadius: tokens.radius.lg,
  fontSize: tokens.font.size.md,
  lineHeight: tokens.font.lineHeight.body,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

export const bubbleAssistantStyle: CSSProperties = {
  background: tokens.color.surfaceAlt,
  color: tokens.color.text,
  padding: '8px 12px',
  borderRadius: tokens.radius.lg,
  fontSize: tokens.font.size.md,
  lineHeight: tokens.font.lineHeight.body,
  wordBreak: 'break-word',
};

export const tableThStyle: CSSProperties = {
  textAlign: 'left',
  padding: '4px 6px',
  color: tokens.color.textMuted,
  fontWeight: tokens.font.weight.semi,
  borderBottom: `1px solid ${tokens.color.border}`,
};

export const tableTdStyle: CSSProperties = {
  padding: '4px 6px',
  color: tokens.color.text,
  borderBottom: `1px solid ${tokens.color.surfaceAlt}`,
  verticalAlign: 'top',
};

export const focusRingStyle: CSSProperties = {
  outline: `2px solid ${tokens.color.accent}`,
  outlineOffset: 2,
};

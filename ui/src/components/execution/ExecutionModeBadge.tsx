import type { CSSProperties } from 'react';

/**
 * Canonical badge for inline vs deferred execution mode.
 * Replaces the duplicate `executionModeBadgeStyle` functions in
 * PostStepsPanel.tsx and AseXmlDeliveryTestCaseTable.tsx.
 */

const DEFERRED_STYLE: CSSProperties = {
  fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
  background: '#ecfeff', color: '#0e7490', border: '1px solid #a5f3fc',
};

const INLINE_STYLE: CSSProperties = {
  fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
  background: '#f1f5f9', color: '#475569', border: '1px solid #cbd5e1',
};

interface Props {
  deferred: boolean;
  /** Optional tooltip to explain the mode. */
  title?: string;
}

export function ExecutionModeBadge({ deferred, title }: Props) {
  return (
    <span style={deferred ? DEFERRED_STYLE : INLINE_STYLE} title={title}>
      {deferred ? 'Deferred' : 'Inline'}
    </span>
  );
}

import type { MouseEvent } from 'react';
import { tokens } from './tokens';

const SUGGESTIONS = [
  'list modules',
  'which environments are configured?',
  'show connected agents',
  'open the MFN delivery test set',
];

interface Props {
  onPick: (text: string, autoSend?: boolean) => void;
}

export function EmptyState({ onPick }: Props) {
  function handle(e: MouseEvent<HTMLButtonElement>, text: string) {
    onPick(text, e.shiftKey);
  }

  return (
    <div style={{ color: tokens.color.textSubtle, fontSize: tokens.font.size.md, lineHeight: tokens.font.lineHeight.body }}>
      <p style={{ margin: '0 0 12px 0' }}>
        Ask me about your test suite. I can list and navigate; I can also confirm runs, recordings, and post-step authoring on your behalf.
      </p>
      <p style={{ margin: '0 0 8px 0', fontSize: tokens.font.size.xs, color: tokens.color.textFaint }}>
        Try one of these — Shift-click to send immediately:
      </p>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
        {SUGGESTIONS.map(s => (
          <button
            key={s}
            onClick={(e) => handle(e, s)}
            style={{
              background: tokens.color.surfaceAlt,
              border: `1px solid ${tokens.color.border}`,
              color: tokens.color.text,
              padding: '4px 10px',
              borderRadius: tokens.radius.pill,
              fontSize: tokens.font.size.sm,
              cursor: 'pointer',
              fontFamily: 'inherit',
            }}
          >
            {s}
          </button>
        ))}
      </div>
    </div>
  );
}

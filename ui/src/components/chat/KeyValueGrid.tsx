import { useState } from 'react';
import { tokens } from './tokens';
import { CopyIcon, CheckIcon } from './icons';

export interface KeyValueRow {
  key: string;
  value: string | undefined;
}

interface KeyValueGridProps {
  rows: KeyValueRow[] | [string, string | undefined][];
}

export function KeyValueGrid({ rows }: KeyValueGridProps) {
  const normalised: KeyValueRow[] = rows.map(r =>
    Array.isArray(r) ? { key: r[0], value: r[1] } : r);
  const filled = normalised.filter(r => r.value != null && r.value !== '');
  if (filled.length === 0) return null;

  return (
    <div style={{
      display: 'grid',
      gridTemplateColumns: 'auto 1fr auto',
      gap: '3px 10px',
      fontSize: tokens.font.size.sm,
      color: tokens.color.text,
      margin: '4px 0 2px',
      alignItems: 'baseline',
    }}>
      {filled.map(({ key, value }) => (
        <Row key={key} k={key} v={value!} />
      ))}
    </div>
  );
}

function Row({ k, v }: { k: string; v: string }) {
  const [hovered, setHovered] = useState(false);
  const [copied, setCopied] = useState(false);

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(v);
      setCopied(true);
      setTimeout(() => setCopied(false), 1200);
    } catch {
      // ignore
    }
  };

  return (
    <div
      style={{ display: 'contents' }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <div style={{ color: tokens.color.textSubtle, whiteSpace: 'nowrap' }}>{k}</div>
      <div style={{
        fontFamily: tokens.font.mono,
        fontSize: tokens.font.size.sm,
        overflowWrap: 'anywhere',
        whiteSpace: 'pre-wrap',
      }}>
        {v}
      </div>
      <button
        type="button"
        onClick={onCopy}
        aria-label={copied ? `Copied ${k}` : `Copy ${k}`}
        title={copied ? 'Copied' : 'Copy value'}
        style={{
          background: 'transparent',
          border: 'none',
          padding: 2,
          borderRadius: tokens.radius.sm,
          color: copied ? tokens.color.success : tokens.color.textFaint,
          cursor: 'pointer',
          opacity: hovered || copied ? 1 : 0,
          transition: 'opacity 120ms ease',
          display: 'inline-flex',
          alignItems: 'center',
        }}
      >
        {copied ? <CheckIcon size={12} /> : <CopyIcon size={12} />}
      </button>
    </div>
  );
}

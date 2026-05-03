import { useState } from 'react';
import type { ReactNode } from 'react';
import { tokens } from './tokens';
import { CopyIcon, CheckIcon } from './icons';

interface CodeBlockProps {
  language?: string;
  // Either pass raw code as `code` (for our own SQL renders), or pass
  // markdown-rendered children (for react-markdown overrides).
  code?: string;
  children?: ReactNode;
  // The text actually copied to the clipboard. If omitted, falls back to
  // `code` or the stringified text content of children.
  copyText?: string;
}

export function CodeBlock({ language, code, children, copyText }: CodeBlockProps) {
  const [copied, setCopied] = useState(false);

  const onCopy = async () => {
    const text = copyText ?? code ?? extractText(children);
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be blocked; ignore.
    }
  };

  return (
    <div style={{
      border: `1px solid ${tokens.color.border}`,
      borderRadius: tokens.radius.md,
      background: tokens.color.surface,
      overflow: 'hidden',
      margin: '6px 0',
    }}>
      <div style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        padding: '4px 8px',
        background: tokens.color.surfaceAlt,
        borderBottom: `1px solid ${tokens.color.border}`,
        fontSize: tokens.font.size.xs,
        color: tokens.color.textSubtle,
      }}>
        <span style={{
          fontFamily: tokens.font.mono,
          textTransform: 'lowercase',
        }}>
          {language || 'text'}
        </span>
        <button
          type="button"
          onClick={onCopy}
          aria-label={copied ? 'Copied' : 'Copy code'}
          style={{
            background: 'transparent',
            border: 'none',
            color: copied ? tokens.color.success : tokens.color.textSubtle,
            cursor: 'pointer',
            display: 'inline-flex',
            alignItems: 'center',
            gap: 4,
            fontSize: tokens.font.size.xs,
            padding: '2px 4px',
            borderRadius: tokens.radius.sm,
          }}
        >
          {copied ? <CheckIcon /> : <CopyIcon />}
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre style={{
        margin: 0,
        padding: 10,
        fontFamily: tokens.font.mono,
        fontSize: tokens.font.size.sm,
        color: tokens.color.text,
        overflowX: 'auto',
        lineHeight: 1.5,
      }}>
        {code != null
          ? <code className={language ? `language-${language} hljs` : 'hljs'}>{code}</code>
          : children}
      </pre>
    </div>
  );
}

function extractText(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return '';
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(extractText).join('');
  if (typeof node === 'object' && 'props' in (node as object)) {
    const props = (node as { props: { children?: ReactNode } }).props;
    return extractText(props.children);
  }
  return '';
}

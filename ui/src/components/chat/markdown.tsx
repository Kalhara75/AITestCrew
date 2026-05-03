import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeHighlight from 'rehype-highlight';
import 'highlight.js/styles/github.css';
import type { ComponentProps, ReactNode } from 'react';
import { CodeBlock } from './CodeBlock';
import { tokens } from './tokens';
import { tableThStyle, tableTdStyle } from './styles';

interface MarkdownProps {
  children: string;
}

export function Markdown({ children }: MarkdownProps) {
  return (
    <div style={{ fontSize: tokens.font.size.md, lineHeight: tokens.font.lineHeight.body }}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeHighlight]}
        components={{
          p: ({ children }) => <p style={{ margin: '4px 0' }}>{children}</p>,
          a: ({ children, href }) => (
            <a href={href} target="_blank" rel="noopener noreferrer"
               style={{ color: tokens.color.accent }}>{children}</a>
          ),
          ul: ({ children }) => <ul style={{ margin: '4px 0', paddingLeft: 18 }}>{children}</ul>,
          ol: ({ children }) => <ol style={{ margin: '4px 0', paddingLeft: 18 }}>{children}</ol>,
          li: ({ children }) => <li style={{ margin: '2px 0' }}>{children}</li>,
          h1: ({ children }) => <h3 style={hStyle}>{children}</h3>,
          h2: ({ children }) => <h3 style={hStyle}>{children}</h3>,
          h3: ({ children }) => <h3 style={hStyle}>{children}</h3>,
          h4: ({ children }) => <h4 style={hStyle}>{children}</h4>,
          h5: ({ children }) => <h4 style={hStyle}>{children}</h4>,
          h6: ({ children }) => <h4 style={hStyle}>{children}</h4>,
          blockquote: ({ children }) => (
            <blockquote style={{
              margin: '4px 0',
              padding: '4px 10px',
              borderLeft: `3px solid ${tokens.color.borderStrong}`,
              color: tokens.color.textMuted,
            }}>{children}</blockquote>
          ),
          code: (props) => <Code {...props} />,
          pre: ({ children }) => <>{children}</>,
          table: ({ children }) => (
            <div style={{ overflowX: 'auto', margin: '6px 0' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: tokens.font.size.sm }}>
                {children}
              </table>
            </div>
          ),
          th: ({ children }) => <th style={tableThStyle}>{children}</th>,
          td: ({ children }) => <td style={tableTdStyle}>{children}</td>,
          hr: () => <hr style={{ border: 'none', borderTop: `1px solid ${tokens.color.border}`, margin: '8px 0' }} />,
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}

const hStyle = { margin: '8px 0 4px', fontSize: tokens.font.size.lg, fontWeight: tokens.font.weight.semi };

type CodeProps = ComponentProps<'code'> & { node?: unknown };

function Code({ className, children, ...rest }: CodeProps) {
  const isBlock = typeof className === 'string' && className.includes('language-');
  if (!isBlock) {
    return (
      <code
        {...rest}
        className={className}
        style={{
          fontFamily: tokens.font.mono,
          fontSize: tokens.font.size.sm,
          background: tokens.color.surfaceAlt,
          padding: '1px 4px',
          borderRadius: tokens.radius.sm,
        }}
      >
        {children}
      </code>
    );
  }
  const language = className?.replace(/.*language-([^\s]+).*/, '$1');
  return (
    <CodeBlock language={language} copyText={extract(children)}>
      <code className={className}>{children}</code>
    </CodeBlock>
  );
}

function extract(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return '';
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(extract).join('');
  if (typeof node === 'object' && 'props' in (node as object)) {
    const props = (node as { props: { children?: ReactNode } }).props;
    return extract(props.children);
  }
  return '';
}

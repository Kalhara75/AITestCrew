import { tokens } from './tokens';
import { tableThStyle, tableTdStyle } from './styles';

export function DataCard({ title, data }: { title?: string; data: unknown }) {
  return (
    <div style={{
      background: tokens.color.bg,
      border: `1px solid ${tokens.color.border}`,
      borderLeft: `4px solid ${tokens.color.dataTint}`,
      borderRadius: tokens.radius.lg,
      padding: 10,
      minWidth: 0,
    }}>
      {title && (
        <div style={{
          fontSize: tokens.font.size.xs,
          fontWeight: tokens.font.weight.bold,
          color: tokens.color.textMuted,
          textTransform: 'uppercase',
          letterSpacing: 0.5,
          marginBottom: 8,
        }}>
          {title}
        </div>
      )}
      <DataView data={data} />
    </div>
  );
}

export function DataView({ data }: { data: unknown }) {
  if (data == null)
    return <div style={muted}>(no data)</div>;

  if (Array.isArray(data) && data.length === 0)
    return <div style={muted}>(empty)</div>;

  if (Array.isArray(data) && data.every(v => typeof v === 'string' || typeof v === 'number')) {
    return (
      <ul style={{ margin: 0, paddingLeft: 18, fontSize: tokens.font.size.md, color: tokens.color.text }}>
        {data.map((v, i) => <li key={i}>{String(v)}</li>)}
      </ul>
    );
  }

  if (Array.isArray(data) && data.every(v => typeof v === 'object' && v !== null)) {
    const keys = Array.from(
      new Set(data.flatMap(obj => Object.keys(obj as Record<string, unknown>))),
    ).slice(0, 6);
    return (
      <div style={{ overflowX: 'auto', minWidth: 0 }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: tokens.font.size.sm }}>
          <thead>
            <tr>{keys.map(k => <th key={k} style={tableThStyle}>{k}</th>)}</tr>
          </thead>
          <tbody>
            {data.map((row, i) => (
              <tr key={i}>
                {keys.map(k => (
                  <td key={k} style={tableTdStyle}>
                    {formatCell((row as Record<string, unknown>)[k])}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  return (
    <pre style={{
      margin: 0,
      fontFamily: tokens.font.mono,
      fontSize: tokens.font.size.sm,
      color: tokens.color.text,
      whiteSpace: 'pre-wrap',
      wordBreak: 'break-word',
      overflowX: 'auto',
    }}>
      {JSON.stringify(data, null, 2)}
    </pre>
  );
}

const muted = { fontSize: tokens.font.size.sm, color: tokens.color.textFaint } as const;

function formatCell(v: unknown): string {
  if (v == null) return '';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

import { Link } from 'react-router-dom';
import type { TestSetListItem } from '../types';
import { StatusBadge } from './StatusBadge';

export function TestSetCard({ ts }: { ts: TestSetListItem }) {
  return (
    <Link to={`/testsets/${ts.id}`} style={{ textDecoration: 'none', color: 'inherit' }}>
      <div style={{
        background: '#fff',
        borderRadius: 10,
        padding: 24,
        boxShadow: '0 1px 3px rgba(0,0,0,0.06)',
        border: '1px solid #e2e8f0',
        cursor: 'pointer',
        transition: 'box-shadow 0.15s, border-color 0.15s',
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
      }}
        onMouseEnter={e => {
          e.currentTarget.style.boxShadow = '0 4px 16px rgba(0,0,0,0.1)';
          e.currentTarget.style.borderColor = '#cbd5e1';
        }}
        onMouseLeave={e => {
          e.currentTarget.style.boxShadow = '0 1px 3px rgba(0,0,0,0.06)';
          e.currentTarget.style.borderColor = '#e2e8f0';
        }}
      >
        {/* Status badge row */}
        <div style={{ marginBottom: 12 }}>
          <StatusBadge status={ts.lastRunStatus} />
        </div>

        {/* Title */}
        <h3 style={{
          margin: '0 0 16px',
          fontSize: 15,
          fontWeight: 600,
          color: '#0f172a',
          lineHeight: 1.5,
          flex: 1,
          wordBreak: 'break-word',
        }}>
          {ts.objective}
        </h3>

        {/* Stats row */}
        <div style={{
          display: 'flex',
          gap: 8,
          fontSize: 13,
          color: '#64748b',
          marginBottom: 12,
        }}>
          <span style={statPill}>{ts.taskCount} task{ts.taskCount !== 1 ? 's' : ''}</span>
          <span style={statPill}>{ts.testCaseCount} case{ts.testCaseCount !== 1 ? 's' : ''}</span>
          <span style={statPill}>{ts.runCount} run{ts.runCount !== 1 ? 's' : ''}</span>
        </div>

        {/* Footer */}
        <div style={{
          fontSize: 12,
          color: '#94a3b8',
          borderTop: '1px solid #f1f5f9',
          paddingTop: 12,
        }}>
          {ts.lastRunAt
            ? `Last run: ${new Date(ts.lastRunAt).toLocaleString()}`
            : 'Never run'}
        </div>
      </div>
    </Link>
  );
}

const statPill: React.CSSProperties = {
  background: '#f8fafc',
  padding: '2px 10px',
  borderRadius: 6,
  fontSize: 12,
  fontWeight: 500,
  border: '1px solid #f1f5f9',
};

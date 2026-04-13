import { Link } from 'react-router-dom';
import type { TestSetListItem } from '../types';
import { StatusBadge } from './StatusBadge';

interface Props {
  ts: TestSetListItem;
  moduleId: string;
  isRunning?: boolean;
}

export function TestSetCard({ ts, moduleId, isRunning }: Props) {
  const linkTo = `/modules/${moduleId}/testsets/${ts.id}`;
  const displayTitle = ts.name || ts.objective || ts.id;

  return (
    <Link to={linkTo} style={{ textDecoration: 'none', color: 'inherit', display: 'flex', flexDirection: 'column' }}>
      <div style={{
        background: '#fff',
        borderRadius: 10,
        padding: 24,
        boxShadow: '0 1px 3px rgba(0,0,0,0.06)',
        border: `1px solid ${isRunning ? '#bfdbfe' : '#e2e8f0'}`,
        borderLeft: isRunning ? '3px solid #2563eb' : undefined,
        cursor: 'pointer',
        transition: 'box-shadow 0.15s, border-color 0.15s',
        flex: 1,
        display: 'flex',
        flexDirection: 'column',
      }}
        onMouseEnter={e => {
          e.currentTarget.style.boxShadow = '0 4px 16px rgba(0,0,0,0.1)';
          if (!isRunning) e.currentTarget.style.borderColor = '#cbd5e1';
        }}
        onMouseLeave={e => {
          e.currentTarget.style.boxShadow = '0 1px 3px rgba(0,0,0,0.06)';
          if (!isRunning) e.currentTarget.style.borderColor = '#e2e8f0';
        }}
      >
        {/* Status badge row */}
        <div style={{ marginBottom: 12 }}>
          <StatusBadge status={isRunning ? 'Running' : ts.lastRunStatus} />
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
          {displayTitle}
        </h3>

        {/* Stats row */}
        <div style={{
          display: 'flex',
          gap: 8,
          fontSize: 13,
          color: '#64748b',
          marginBottom: 12,
          flexWrap: 'wrap',
        }}>
          <span style={statPill}>{ts.objectiveCount} objective{ts.objectiveCount !== 1 ? 's' : ''}</span>
          <span style={statPill}>{ts.runCount} run{ts.runCount !== 1 ? 's' : ''}</span>
        </div>

        {/* Footer */}
        <div style={{
          fontSize: 12,
          color: '#94a3b8',
          borderTop: '1px solid #f1f5f9',
          paddingTop: 12,
        }}>
          {ts.lastRunAt && ts.lastRunAt !== '0001-01-01T00:00:00'
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

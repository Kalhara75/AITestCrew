import { Link } from 'react-router-dom';
import type { RunSummary } from '../types';
import { StatusBadge } from './execution/StatusBadge';
import { ModeBadge } from './execution/ModeBadge';
import { StatsBar } from './execution/StatsBar';

export function RunHistoryTable({ runs, testSetId, moduleId }: { runs: RunSummary[]; testSetId: string; moduleId?: string }) {
  if (runs.length === 0) {
    return <p style={{ color: '#94a3b8', fontSize: 14 }}>No execution history yet.</p>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Run</th>
            <th style={thStyle}>Mode</th>
            <th style={thStyle}>Status</th>
            <th style={thStyle}>Steps</th>
            <th style={thStyle}>Duration</th>
            <th style={thStyle}>Date</th>
          </tr>
        </thead>
        <tbody>
          {runs.map(r => (
            <tr key={r.runId} style={{ borderBottom: '1px solid #f1f5f9' }}
              onMouseEnter={e => (e.currentTarget.style.background = '#f8fafc')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
            >
              <td style={tdStyle}>
                <Link to={moduleId ? `/modules/${moduleId}/testsets/${testSetId}/runs/${r.runId}` : `/testsets/${testSetId}/runs/${r.runId}`}
                  style={{ color: '#2563eb', textDecoration: 'none', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 }}>
                  {r.runId}
                </Link>
              </td>
              <td style={tdStyle}><ModeBadge mode={r.mode} /></td>
              <td style={tdStyle}><StatusBadge status={r.status} /></td>
              <td style={tdStyle}>
                <StatsBar passed={r.passedObjectives} failed={r.failedObjectives} total={r.totalObjectives} size="sm" />
              </td>
              <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 }}>{r.totalDuration}</td>
              <td style={{ ...tdStyle, fontSize: 13, color: '#64748b' }}>
                {new Date(r.startedAt).toLocaleString()}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { padding: '10px 14px', color: '#64748b', fontWeight: 600, fontSize: 12, textTransform: 'uppercase', letterSpacing: 0.5 };
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b' };

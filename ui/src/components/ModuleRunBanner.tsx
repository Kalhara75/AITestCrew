import { Link } from 'react-router-dom';
import type { ModuleRunStatus } from '../types';

interface Props {
  moduleRun: ModuleRunStatus;
  moduleId: string;
}

export function ModuleRunBanner({ moduleRun, moduleId }: Props) {
  const { completedCount, totalCount, testSets } = moduleRun;

  return (
    <div style={{
      background: '#eff6ff',
      border: '1px solid #bfdbfe',
      borderRadius: 10,
      padding: 20,
      marginBottom: 20,
    }}>
      {/* Header */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 14 }}>
        <div style={spinnerStyle} />
        <span style={{ fontSize: 14, fontWeight: 600, color: '#1e40af' }}>
          Running test sets: {completedCount} of {totalCount} completed
        </span>
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>

      {/* Segmented progress bar */}
      <div style={{ display: 'flex', gap: 3, marginBottom: 14, borderRadius: 6, overflow: 'hidden' }}>
        {testSets.map(ts => (
          <div
            key={ts.testSetId}
            style={{
              flex: 1,
              height: 8,
              background: segmentColor(ts.status),
              transition: 'background 0.3s',
              ...(ts.status === 'Running' ? {
                backgroundImage: 'linear-gradient(90deg, #60a5fa 0%, #2563eb 50%, #60a5fa 100%)',
                backgroundSize: '200% 100%',
                animation: 'shimmer 1.5s ease-in-out infinite',
              } : {}),
            }}
          />
        ))}
        <style>{`@keyframes shimmer { 0%, 100% { background-position: 200% 0; } 50% { background-position: -200% 0; } }`}</style>
      </div>

      {/* Test set status list */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
        {testSets.map(ts => (
          <Link
            key={ts.testSetId}
            to={`/modules/${moduleId}/testsets/${ts.testSetId}`}
            style={{
              textDecoration: 'none',
              fontSize: 12,
              fontWeight: 500,
              padding: '3px 10px',
              borderRadius: 6,
              border: `1px solid ${statusBorder(ts.status)}`,
              background: statusBg(ts.status),
              color: statusFg(ts.status),
              display: 'flex',
              alignItems: 'center',
              gap: 5,
            }}
          >
            {ts.status === 'Running' && <span style={{ ...miniSpinnerStyle }} />}
            {ts.status === 'Completed' && <span>&#10003;</span>}
            {ts.status === 'Failed' && <span>&#10007;</span>}
            {ts.testSetName}
          </Link>
        ))}
      </div>
    </div>
  );
}

function segmentColor(status: string): string {
  switch (status) {
    case 'Completed': return '#22c55e';
    case 'Failed': return '#ef4444';
    case 'Running': return '#3b82f6';
    default: return '#e2e8f0';
  }
}

function statusBg(status: string): string {
  switch (status) {
    case 'Completed': return '#dcfce7';
    case 'Failed': return '#fee2e2';
    case 'Running': return '#dbeafe';
    default: return '#f8fafc';
  }
}

function statusFg(status: string): string {
  switch (status) {
    case 'Completed': return '#166534';
    case 'Failed': return '#991b1b';
    case 'Running': return '#1e40af';
    default: return '#64748b';
  }
}

function statusBorder(status: string): string {
  switch (status) {
    case 'Completed': return '#bbf7d0';
    case 'Failed': return '#fecaca';
    case 'Running': return '#bfdbfe';
    default: return '#e2e8f0';
  }
}

const spinnerStyle: React.CSSProperties = {
  width: 16,
  height: 16,
  border: '2.5px solid #bfdbfe',
  borderTop: '2.5px solid #2563eb',
  borderRadius: '50%',
  animation: 'spin 0.8s linear infinite',
  flexShrink: 0,
};

const miniSpinnerStyle: React.CSSProperties = {
  display: 'inline-block',
  width: 10,
  height: 10,
  border: '1.5px solid #bfdbfe',
  borderTop: '1.5px solid #2563eb',
  borderRadius: '50%',
  animation: 'spin 0.8s linear infinite',
};

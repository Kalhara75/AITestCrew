import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';

interface Props {
  testSetId: string;
  moduleId?: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
}

export function TriggerRunButton({ testSetId, moduleId, apiStackKey, apiModule }: Props) {
  const navigate = useNavigate();
  const { individualRun, individualRunStatus, setIndividualRun } = useActiveRun();
  const [error, setError] = useState<string | null>(null);

  // This button is "active" if the global individual run targets this test set
  const isActive = individualRun?.testSetId === testSetId;

  // Navigate on completion
  if (isActive && individualRunStatus) {
    if (individualRunStatus.status === 'Completed' && individualRunStatus.testSetId) {
      const basePath = moduleId
        ? `/modules/${moduleId}/testsets/${individualRunStatus.testSetId}`
        : `/testsets/${individualRunStatus.testSetId}`;
      // Defer navigation to avoid state update during render
      setTimeout(() => navigate(`${basePath}/runs/${individualRunStatus.runId}`), 0);
    } else if (individualRunStatus.status === 'Failed') {
      setTimeout(() => {
        setIndividualRun(null);
        setError(individualRunStatus.error || 'Run failed');
      }, 0);
    }
  }

  const handleRun = async () => {
    setError(null);
    try {
      const res = await triggerRun({
        mode: 'Reuse',
        testSetId: testSetId,
        moduleId,
        apiStackKey: apiStackKey ?? undefined,
        apiModule: apiModule ?? undefined,
      });
      setIndividualRun({ runId: res.runId, testSetId, moduleId });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  if (isActive) {
    const s = individualRunStatus?.status;
    const label = s === 'Queued' ? 'Queued — waiting for agent...'
                : s === 'Claimed' ? 'Agent claimed the job...'
                : 'Running tests...';
    return (
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: '10px 20px',
        background: s === 'Queued' || s === 'Claimed' ? '#fffbeb' : '#eff6ff',
        borderRadius: 8,
        border: `1px solid ${s === 'Queued' || s === 'Claimed' ? '#fde68a' : '#bfdbfe'}`,
      }}>
        <div style={{
          width: 16, height: 16,
          border: `2.5px solid ${s === 'Queued' || s === 'Claimed' ? '#fde68a' : '#bfdbfe'}`,
          borderTop: `2.5px solid ${s === 'Queued' || s === 'Claimed' ? '#b45309' : '#2563eb'}`,
          borderRadius: '50%',
          animation: 'spin 0.8s linear infinite',
        }} />
        <span style={{ fontSize: 13, color: s === 'Queued' || s === 'Claimed' ? '#78350f' : '#1e40af', fontWeight: 500 }}>
          {label}
        </span>
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>
    );
  }

  return (
    <div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button onClick={handleRun} style={btnStyle('#2563eb', '#1d4ed8')}>
          Re-run Tests
        </button>
      </div>
      {error && (
        <p style={{
          color: '#dc2626',
          fontSize: 12,
          marginTop: 8,
          padding: '6px 12px',
          background: '#fef2f2',
          borderRadius: 6,
          border: '1px solid #fecaca',
        }}>{error}</p>
      )}
    </div>
  );
}

const btnStyle = (bg: string, _hover: string): React.CSSProperties => ({
  background: bg,
  color: '#fff',
  border: 'none',
  padding: '8px 18px',
  borderRadius: 8,
  fontSize: 13,
  fontWeight: 600,
  cursor: 'pointer',
  transition: 'background 0.15s',
});

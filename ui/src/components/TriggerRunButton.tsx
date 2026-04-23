import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';

interface Props {
  testSetId: string;
  moduleId?: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
  environmentKey?: string | null;
}

export function TriggerRunButton({ testSetId, moduleId, apiStackKey, apiModule, environmentKey }: Props) {
  const navigate = useNavigate();
  const { individualRun, individualRunStatus, setIndividualRun } = useActiveRun();
  const [error, setError] = useState<string | null>(null);

  // This button is "active" if the global individual run targets this test set
  const isActive = individualRun?.testSetId === testSetId;

  // Navigate on completion. Both 'Completed' and 'Passed' mean terminal success
  // (server returns 'Completed' from RunTracker, 'Passed' when collapsed from
  // execution history). Don't navigate while the run is AwaitingVerification —
  // the deferred verification is still in flight.
  if (isActive && individualRunStatus) {
    const s = individualRunStatus.status;
    if ((s === 'Completed' || s === 'Passed') && individualRunStatus.testSetId) {
      const basePath = moduleId
        ? `/modules/${moduleId}/testsets/${individualRunStatus.testSetId}`
        : `/testsets/${individualRunStatus.testSetId}`;
      // Defer navigation to avoid state update during render
      setTimeout(() => navigate(`${basePath}/runs/${individualRunStatus.runId}`), 0);
    } else if (s === 'Failed' || s === 'Error') {
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
        environmentKey: environmentKey ?? undefined,
      });
      setIndividualRun({ runId: res.runId, testSetId, moduleId });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  if (isActive) {
    const s = individualRunStatus?.status;
    const isQueued = s === 'Queued' || s === 'Claimed';
    const isAwaiting = s === 'AwaitingVerification';
    const label = s === 'Queued' ? 'Queued — waiting for agent'
                : s === 'Claimed' ? 'Agent claimed the job'
                : isAwaiting ? 'Scheduled — awaiting deferred verification'
                : 'Running tests...';
    const palette = isAwaiting
      ? { bg: '#ecfeff', border: '#a5f3fc', fg: '#0e7490' }
      : isQueued
      ? { bg: '#fffbeb', border: '#fde68a', fg: '#78350f' }
      : { bg: '#eff6ff', border: '#bfdbfe', fg: '#1e40af' };
    return (
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '10px 18px',
        background: palette.bg,
        borderRadius: 8,
        border: `1px solid ${palette.border}`,
      }}>
        {/* Awaiting is a scheduled / parked state — no spinner, just an icon.
            Only active execution (Queued/Claimed/Running) gets the spinner. */}
        {isAwaiting ? (
          <span style={{ fontSize: 16, lineHeight: 1 }}>{'\u23F3'}</span>
        ) : (
          <div style={{
            width: 16, height: 16,
            border: `2.5px solid ${palette.border}`,
            borderTop: `2.5px solid ${palette.fg}`,
            borderRadius: '50%',
            animation: 'spin 0.8s linear infinite',
          }} />
        )}
        <span style={{ fontSize: 13, color: palette.fg, fontWeight: 500 }}>
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

import { useState } from 'react';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';

interface Props {
  testSetId: string;
  objectiveId: string;
  moduleId?: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
  disabled?: boolean;
}

export function TriggerObjectiveRunButton({ testSetId, objectiveId, moduleId, apiStackKey, apiModule, disabled }: Props) {
  const { individualRun, setIndividualRun } = useActiveRun();
  const [error, setError] = useState<string | null>(null);

  // This button is "active" if the global individual run targets this specific objective
  const isActive = individualRun?.testSetId === testSetId && individualRun?.objectiveId === objectiveId;
  // Any individual run is in progress (disable other run buttons)
  const anyRunning = !!individualRun;

  const handleRun = async (e: React.MouseEvent) => {
    e.stopPropagation();
    setError(null);

    try {
      const res = await triggerRun({
        mode: 'Reuse',
        testSetId,
        moduleId,
        objectiveId,
        apiStackKey: apiStackKey ?? undefined,
        apiModule: apiModule ?? undefined,
      });
      setIndividualRun({ runId: res.runId, testSetId, moduleId, objectiveId });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  if (isActive) {
    return (
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
        <div style={{
          width: 12, height: 12,
          border: '2px solid #bfdbfe',
          borderTop: '2px solid #2563eb',
          borderRadius: '50%',
          animation: 'spin 0.8s linear infinite',
        }} />
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>
    );
  }

  return (
    <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      <button
        onClick={handleRun}
        disabled={disabled || anyRunning}
        style={{
          background: 'none',
          color: disabled || anyRunning ? '#94a3b8' : '#2563eb',
          border: `1px solid ${disabled || anyRunning ? '#e2e8f0' : '#bfdbfe'}`,
          padding: '1px 8px',
          borderRadius: 4,
          fontSize: 11,
          fontWeight: 600,
          cursor: disabled || anyRunning ? 'not-allowed' : 'pointer',
          lineHeight: '18px',
        }}
        title="Run this test case"
      >
        Run
      </button>
      {error && (
        <span style={{ color: '#dc2626', fontSize: 10 }} title={error}>!</span>
      )}
    </div>
  );
}

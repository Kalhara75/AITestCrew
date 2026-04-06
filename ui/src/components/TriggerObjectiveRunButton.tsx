import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { triggerRun, fetchRunStatus } from '../api/runs';

interface Props {
  testSetId: string;
  objectiveId: string;
  moduleId?: string;
  disabled?: boolean;
}

export function TriggerObjectiveRunButton({ testSetId, objectiveId, moduleId, disabled }: Props) {
  const queryClient = useQueryClient();
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleRun = async (e: React.MouseEvent) => {
    e.stopPropagation();
    setError(null);
    setRunning(true);

    try {
      const res = await triggerRun({
        mode: 'Reuse',
        testSetId,
        moduleId,
        objectiveId,
      });

      // Poll until complete
      const poll = async () => {
        const status = await fetchRunStatus(res.runId);
        if (status.status === 'Completed') {
          setRunning(false);
          queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, testSetId] });
          queryClient.invalidateQueries({ queryKey: ['runs', moduleId, testSetId] });
        } else if (status.status === 'Failed') {
          setRunning(false);
          setError(status.error || 'Run failed');
        } else {
          setTimeout(poll, 3000);
        }
      };
      setTimeout(poll, 2000);
    } catch (err) {
      setRunning(false);
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  if (running) {
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
        disabled={disabled}
        style={{
          background: 'none',
          color: disabled ? '#94a3b8' : '#2563eb',
          border: `1px solid ${disabled ? '#e2e8f0' : '#bfdbfe'}`,
          padding: '1px 8px',
          borderRadius: 4,
          fontSize: 11,
          fontWeight: 600,
          cursor: disabled ? 'not-allowed' : 'pointer',
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

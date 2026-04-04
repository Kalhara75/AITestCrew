import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { triggerRun, fetchRunStatus } from '../api/runs';

interface Props {
  testSetId: string;
  objective: string;
  moduleId?: string;
}

export function TriggerRunButton({ testSetId, objective, moduleId }: Props) {
  const navigate = useNavigate();
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Poll run status while a run is active
  useQuery({
    queryKey: ['runStatus', activeRunId],
    queryFn: () => fetchRunStatus(activeRunId!),
    enabled: !!activeRunId,
    refetchInterval: 3000,
    select(data) {
      if (data.status === 'Completed' && data.testSetId) {
        setActiveRunId(null);
        const basePath = moduleId
          ? `/modules/${moduleId}/testsets/${data.testSetId}`
          : `/testsets/${data.testSetId}`;
        navigate(`${basePath}/runs/${data.runId}`);
      } else if (data.status === 'Failed') {
        setActiveRunId(null);
        setError(data.error || 'Run failed');
      }
      return data;
    },
  });

  const handleRun = async (mode: string) => {
    setError(null);
    try {
      const res = await triggerRun({
        mode,
        testSetId: testSetId,
        objective: mode !== 'Reuse' ? objective : undefined,
        moduleId,
      });
      setActiveRunId(res.runId);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  if (activeRunId) {
    return (
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: '10px 20px',
        background: '#eff6ff',
        borderRadius: 8,
        border: '1px solid #bfdbfe',
      }}>
        <div style={{
          width: 16, height: 16,
          border: '2.5px solid #bfdbfe',
          borderTop: '2.5px solid #2563eb',
          borderRadius: '50%',
          animation: 'spin 0.8s linear infinite',
        }} />
        <span style={{ fontSize: 13, color: '#1e40af', fontWeight: 500 }}>Running tests...</span>
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>
    );
  }

  return (
    <div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button onClick={() => handleRun('Reuse')} style={btnStyle('#2563eb', '#1d4ed8')}>
          Re-run Tests
        </button>
        <button onClick={() => handleRun('Rebaseline')} style={btnStyle('#d97706', '#b45309')}>
          Rebaseline
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

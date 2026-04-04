import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { triggerRun, fetchRunStatus } from '../api/runs';
import type { TestSetListItem } from '../types';

interface Props {
  open: boolean;
  moduleId: string;
  testSets: TestSetListItem[];
  onClose: () => void;
}

export function RunObjectiveDialog({ open, moduleId, testSets, onClose }: Props) {
  const navigate = useNavigate();
  const [objective, setObjective] = useState('');
  const [selectedTestSetId, setSelectedTestSetId] = useState(testSets[0]?.id ?? '');
  const [error, setError] = useState<string | null>(null);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);

  // Poll run status while active
  useQuery({
    queryKey: ['runStatus', activeRunId],
    queryFn: () => fetchRunStatus(activeRunId!),
    enabled: !!activeRunId,
    refetchInterval: 3000,
    select(data) {
      if (data.status === 'Completed' && data.testSetId) {
        setActiveRunId(null);
        onClose();
        navigate(`/modules/${moduleId}/testsets/${data.testSetId}/runs/${data.runId}`);
      } else if (data.status === 'Failed') {
        setActiveRunId(null);
        setError(data.error || 'Run failed');
      }
      return data;
    },
  });

  if (!open) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!objective.trim() || !selectedTestSetId) return;
    setError(null);
    try {
      const res = await triggerRun({
        mode: 'Normal',
        objective: objective.trim(),
        moduleId,
        testSetId: selectedTestSetId,
      });
      setActiveRunId(res.runId);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  return (
    <div style={overlayStyle} onClick={activeRunId ? undefined : onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 20px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          Run Test Objective
        </h2>

        {activeRunId ? (
          <div style={{
            display: 'flex', alignItems: 'center', gap: 12, padding: '20px 0',
            justifyContent: 'center',
          }}>
            <div style={{
              width: 20, height: 20,
              border: '2.5px solid #bfdbfe',
              borderTop: '2.5px solid #2563eb',
              borderRadius: '50%',
              animation: 'spin 0.8s linear infinite',
            }} />
            <span style={{ fontSize: 14, color: '#1e40af', fontWeight: 500 }}>
              Running tests... This may take a minute.
            </span>
            <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
          </div>
        ) : (
          <form onSubmit={handleSubmit}>
            <label style={labelStyle}>Target Test Set</label>
            <select
              style={{ ...inputStyle, cursor: 'pointer' }}
              value={selectedTestSetId}
              onChange={e => setSelectedTestSetId(e.target.value)}
            >
              {testSets.map(ts => (
                <option key={ts.id} value={ts.id}>{ts.name || ts.id}</option>
              ))}
            </select>

            <label style={{ ...labelStyle, marginTop: 16 }}>Test Objective</label>
            <textarea
              style={{ ...inputStyle, minHeight: 80, resize: 'vertical' }}
              value={objective}
              onChange={e => setObjective(e.target.value)}
              placeholder="e.g. Test the GET /api/ReferenceDataManagement/ControlledLoadDecodes endpoint"
              autoFocus
            />

            {error && <p style={errorStyle}>{error}</p>}

            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
              <button type="button" onClick={onClose} style={cancelBtnStyle}>Cancel</button>
              <button
                type="submit"
                disabled={!objective.trim() || !selectedTestSetId}
                style={submitBtnStyle}
              >
                Run
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex',
  alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28, width: 500, maxWidth: '90vw',
  boxShadow: '0 20px 60px rgba(0,0,0,0.15)',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 13, fontWeight: 600, color: '#475569', marginBottom: 6,
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '8px 12px', fontSize: 14, border: '1px solid #e2e8f0',
  borderRadius: 8, outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
};
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 13, marginTop: 12, padding: '6px 12px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};
const cancelBtnStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const submitBtnStyle: React.CSSProperties = {
  background: '#16a34a', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

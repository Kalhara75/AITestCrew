import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';
import type { TestSetListItem } from '../types';

interface Props {
  open: boolean;
  moduleId: string;
  testSets: TestSetListItem[];
  onClose: () => void;
}

export function RunObjectiveDialog({ open, moduleId, testSets, onClose }: Props) {
  const navigate = useNavigate();
  const { individualRun, individualRunStatus, setIndividualRun } = useActiveRun();
  const [objective, setObjective] = useState('');
  const [objectiveName, setObjectiveName] = useState('');
  const [selectedTestSetId, setSelectedTestSetId] = useState(testSets[0]?.id ?? '');
  const [error, setError] = useState<string | null>(null);
  // Track whether this dialog instance started the run
  const [startedRunId, setStartedRunId] = useState<string | null>(null);

  const isActive = !!startedRunId && individualRun?.runId === startedRunId;

  // Navigate on completion for runs started by this dialog
  useEffect(() => {
    if (!isActive || !individualRunStatus) return;
    if (individualRunStatus.status === 'Completed' && individualRunStatus.testSetId) {
      setStartedRunId(null);
      onClose();
      navigate(`/modules/${moduleId}/testsets/${individualRunStatus.testSetId}/runs/${individualRunStatus.runId}`);
    } else if (individualRunStatus.status === 'Failed') {
      setStartedRunId(null);
      setIndividualRun(null);
      setError(individualRunStatus.error || 'Run failed');
    }
  }, [individualRunStatus?.status, isActive]);

  if (!open) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!objective.trim() || !selectedTestSetId) return;
    setError(null);
    try {
      const res = await triggerRun({
        mode: 'Normal',
        objective: objective.trim(),
        objectiveName: objectiveName.trim() || undefined,
        moduleId,
        testSetId: selectedTestSetId,
      });
      setStartedRunId(res.runId);
      setIndividualRun({ runId: res.runId, testSetId: selectedTestSetId, moduleId });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  return (
    <div style={overlayStyle} onClick={isActive ? undefined : onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 20px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          Run Test Objective
        </h2>

        {isActive ? (
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

            <label style={{ ...labelStyle, marginTop: 16 }}>Short Name <span style={{ fontWeight: 400, color: '#94a3b8' }}>(optional)</span></label>
            <input
              type="text"
              style={inputStyle}
              value={objectiveName}
              onChange={e => setObjectiveName(e.target.value)}
              placeholder="e.g. Ctrl Loads GET"
              maxLength={80}
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

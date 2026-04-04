import { useState, useEffect } from 'react';
import { fetchModules, fetchModuleTestSets, moveObjective } from '../api/modules';
import type { Module, TestSetListItem } from '../types';

interface Props {
  open: boolean;
  objective: string;
  sourceModuleId: string;
  sourceTestSetId: string;
  onClose: () => void;
  onMoved: () => void;
}

export function MoveObjectiveDialog({
  open, objective, sourceModuleId, sourceTestSetId,
  onClose, onMoved,
}: Props) {
  const [modules, setModules] = useState<Module[]>([]);
  const [testSets, setTestSets] = useState<TestSetListItem[]>([]);
  const [selectedModuleId, setSelectedModuleId] = useState('');
  const [selectedTestSetId, setSelectedTestSetId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setSelectedModuleId('');
    setSelectedTestSetId('');
    setTestSets([]);
    setError(null);
    fetchModules().then(setModules).catch(() => setModules([]));
  }, [open]);

  useEffect(() => {
    if (!selectedModuleId) { setTestSets([]); setSelectedTestSetId(''); return; }
    setSelectedTestSetId('');
    fetchModuleTestSets(selectedModuleId).then(ts => {
      // Exclude the source test set if same module
      const filtered = selectedModuleId === sourceModuleId
        ? ts.filter(t => t.id !== sourceTestSetId)
        : ts;
      setTestSets(filtered);
    }).catch(() => setTestSets([]));
  }, [selectedModuleId, sourceModuleId, sourceTestSetId]);

  if (!open) return null;

  const handleMove = async () => {
    if (!selectedModuleId || !selectedTestSetId) return;
    setError(null);
    setLoading(true);
    try {
      await moveObjective(sourceModuleId, sourceTestSetId, {
        objective,
        destinationModuleId: selectedModuleId,
        destinationTestSetId: selectedTestSetId,
      });
      onMoved();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to move objective');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 16px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          Move Objective
        </h2>
        <div style={{ marginBottom: 20, padding: 12, background: '#f8fafc', borderRadius: 8, border: '1px solid #e2e8f0' }}>
          <div style={{ fontSize: 12, color: '#94a3b8', marginBottom: 4, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5 }}>
            Objective
          </div>
          <div style={{ fontSize: 13, color: '#0f172a', lineHeight: 1.5 }}>{objective}</div>
        </div>

        <label style={labelStyle}>Destination Module</label>
        <select
          style={selectStyle}
          value={selectedModuleId}
          onChange={e => setSelectedModuleId(e.target.value)}
        >
          <option value="">Select a module...</option>
          {modules.map(m => (
            <option key={m.id} value={m.id}>{m.name}</option>
          ))}
        </select>

        <label style={{ ...labelStyle, marginTop: 16 }}>Destination Test Set</label>
        <select
          style={selectStyle}
          value={selectedTestSetId}
          onChange={e => setSelectedTestSetId(e.target.value)}
          disabled={!selectedModuleId || testSets.length === 0}
        >
          <option value="">
            {!selectedModuleId ? 'Select a module first...' : testSets.length === 0 ? 'No test sets available' : 'Select a test set...'}
          </option>
          {testSets.map(ts => (
            <option key={ts.id} value={ts.id}>{ts.name}</option>
          ))}
        </select>

        {error && <p style={errorStyle}>{error}</p>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
          <button type="button" onClick={onClose} style={cancelBtnStyle} disabled={loading}>
            Cancel
          </button>
          <button
            type="button"
            onClick={handleMove}
            disabled={loading || !selectedModuleId || !selectedTestSetId}
            style={submitBtnStyle}
          >
            {loading ? 'Moving...' : 'Move'}
          </button>
        </div>
      </div>
    </div>
  );
}

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex',
  alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28, width: 460, maxWidth: '90vw',
  boxShadow: '0 20px 60px rgba(0,0,0,0.15)',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 13, fontWeight: 600, color: '#475569', marginBottom: 6,
};
const selectStyle: React.CSSProperties = {
  width: '100%', padding: '8px 12px', fontSize: 14, border: '1px solid #e2e8f0',
  borderRadius: 8, outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
  background: '#fff',
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
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

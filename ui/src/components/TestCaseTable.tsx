import { useState } from 'react';
import { updateObjective, deleteObjective } from '../api/modules';
import { EditTestCaseDialog } from './EditTestCaseDialog';
import type { TestObjective } from '../types';

const methodColor: Record<string, { fg: string; bg: string }> = {
  GET:    { fg: '#166534', bg: '#dcfce7' },
  POST:   { fg: '#1e40af', bg: '#dbeafe' },
  PUT:    { fg: '#92400e', bg: '#fef3c7' },
  PATCH:  { fg: '#6b21a8', bg: '#f3e8ff' },
  DELETE: { fg: '#991b1b', bg: '#fee2e2' },
};

interface Props {
  objectives: TestObjective[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

export function TestCaseTable({ objectives, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const [editing, setEditing] = useState<{
    objective: TestObjective;
    stepIndex: number;
  } | null>(null);
  const [deletingKey, setDeletingKey] = useState<string | null>(null);
  const [confirmDeleteKey, setConfirmDeleteKey] = useState<string | null>(null);

  const allCases = objectives
    .filter(o => o.apiSteps.length > 0)
    .flatMap(o => o.apiSteps.map((step, idx) => ({
      ...step,
      objectiveId: o.id,
      objectiveName: o.name,
      stepIndex: idx,
      objective: o,
      key: `${o.id}-${idx}`,
    })));

  const editable = !!moduleId && !!testSetId;

  const handleInlineDelete = async (tc: typeof allCases[number]) => {
    if (!moduleId || !testSetId) return;
    const key = tc.key;
    setDeletingKey(key);
    try {
      if (tc.objective.apiSteps.length <= 1) {
        await deleteObjective(moduleId, testSetId, tc.objective.id);
      } else {
        const updatedSteps = tc.objective.apiSteps.filter((_, i) => i !== tc.stepIndex);
        await updateObjective(moduleId, testSetId, tc.objective.id, { ...tc.objective, apiSteps: updatedSteps });
      }
      setConfirmDeleteKey(null);
      onTestCaseUpdated?.();
    } catch {
      // Error handling — just reset state
      setDeletingKey(null);
      setConfirmDeleteKey(null);
    }
  };

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Method</th>
            <th style={thStyle}>Endpoint</th>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Expected</th>
            {editable && <th style={{ ...thStyle, width: 70 }}></th>}
          </tr>
        </thead>
        <tbody>
          {allCases.map((tc) => (
            <tr key={tc.key} style={{ borderBottom: '1px solid #f1f5f9', cursor: editable ? 'pointer' : undefined }}
              onMouseEnter={e => (e.currentTarget.style.background = '#f8fafc')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
              onClick={editable ? () => setEditing({ objective: tc.objective, stepIndex: tc.stepIndex }) : undefined}
            >
              <td style={tdStyle}>
                {(() => {
                  const m = tc.method.toUpperCase();
                  const c = methodColor[m] || { fg: '#64748b', bg: '#f1f5f9' };
                  return (
                    <span style={{
                      fontWeight: 700, fontSize: 11, padding: '3px 8px', borderRadius: 4,
                      color: c.fg, background: c.bg,
                      fontFamily: 'ui-monospace, Consolas, monospace',
                    }}>
                      {m}
                    </span>
                  );
                })()}
              </td>
              <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13, color: '#334155' }}>{tc.endpoint}</td>
              <td style={{ ...tdStyle, color: '#475569' }}>{tc.objectiveName}</td>
              <td style={tdStyle}>
                <span style={{
                  fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13, fontWeight: 600,
                  color: tc.expectedStatus >= 200 && tc.expectedStatus < 300 ? '#166534' :
                         tc.expectedStatus >= 400 ? '#991b1b' : '#475569',
                }}>
                  {tc.expectedStatus}
                </span>
              </td>
              {editable && (
                <td style={{ ...tdStyle, whiteSpace: 'nowrap' }}>
                  <span style={{ fontSize: 12, color: '#94a3b8', marginRight: 8 }} title="Edit">&#9998;</span>
                  {confirmDeleteKey === tc.key ? (
                    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }} onClick={e => e.stopPropagation()}>
                      <button
                        onClick={(e) => { e.stopPropagation(); handleInlineDelete(tc); }}
                        disabled={deletingKey === tc.key}
                        style={inlineDeleteConfirmStyle}
                      >
                        {deletingKey === tc.key ? '...' : 'Yes'}
                      </button>
                      <button
                        onClick={(e) => { e.stopPropagation(); setConfirmDeleteKey(null); }}
                        style={inlineCancelStyle}
                      >
                        No
                      </button>
                    </span>
                  ) : (
                    <span
                      onClick={(e) => { e.stopPropagation(); setConfirmDeleteKey(tc.key); }}
                      style={{ fontSize: 13, color: '#dc2626', cursor: 'pointer', opacity: 0.6 }}
                      title="Delete step"
                    >
                      &#128465;
                    </span>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>

      {editing && moduleId && testSetId && (
        <EditTestCaseDialog
          open
          objective={editing.objective}
          stepIndex={editing.stepIndex}
          moduleId={moduleId}
          testSetId={testSetId}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); onTestCaseUpdated?.(); }}
          onDeleted={() => { setEditing(null); onTestCaseUpdated?.(); }}
        />
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { padding: '10px 14px', color: '#64748b', fontWeight: 600, fontSize: 12, textTransform: 'uppercase', letterSpacing: 0.5 };
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b' };
const inlineDeleteConfirmStyle: React.CSSProperties = {
  background: '#dc2626', color: '#fff', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};
const inlineCancelStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};

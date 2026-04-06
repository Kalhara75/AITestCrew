import { useState } from 'react';
import { EditWebUiTestCaseDialog } from './EditWebUiTestCaseDialog';
import type { TestObjective } from '../types';

interface Props {
  objectives: TestObjective[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

export function WebUiTestCaseTable({ objectives, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const [editing, setEditing] = useState<{
    objective: TestObjective;
    stepIndex: number;
  } | null>(null);

  const allCases = objectives
    .filter(o => o.webUiSteps.length > 0)
    .flatMap(o => o.webUiSteps.map((step, idx) => ({
      ...step,
      objectiveId: o.id,
      objectiveName: o.name,
      stepIndex: idx,
      objective: o,
    })));

  const editable = !!moduleId && !!testSetId;

  if (allCases.length === 0) {
    return <p style={{ color: '#94a3b8', fontSize: 14, padding: '8px 0' }}>No web UI test cases.</p>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Start URL</th>
            <th style={thStyle}>Steps</th>
            <th style={thStyle}>Screenshot</th>
            {editable && <th style={{ ...thStyle, width: 40 }}></th>}
          </tr>
        </thead>
        <tbody>
          {allCases.map((tc) => (
            <tr
              key={`${tc.objectiveId}-${tc.stepIndex}`}
              style={{ borderBottom: '1px solid #f1f5f9', cursor: editable ? 'pointer' : undefined }}
              onMouseEnter={e => (e.currentTarget.style.background = '#f8fafc')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
              onClick={editable ? () => setEditing({ objective: tc.objective, stepIndex: tc.stepIndex }) : undefined}
            >
              <td style={tdStyle}>{tc.objectiveName}</td>
              <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13, color: '#334155' }}>
                {tc.startUrl || '/'}
              </td>
              <td style={tdStyle}>
                <span style={{
                  display: 'inline-flex', alignItems: 'center', gap: 4,
                  fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13,
                  color: '#1e40af',
                }}>
                  {tc.steps.length} steps
                </span>
              </td>
              <td style={tdStyle}>
                <span style={{ fontSize: 13, color: tc.takeScreenshotOnFailure ? '#166534' : '#94a3b8' }}>
                  {tc.takeScreenshotOnFailure ? '✓ on fail' : '—'}
                </span>
              </td>
              {editable && (
                <td style={tdStyle}>
                  <span style={{ fontSize: 12, color: '#94a3b8' }} title="Edit test case">
                    &#9998;
                  </span>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>

      {editing && moduleId && testSetId && (
        <EditWebUiTestCaseDialog
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

const thStyle: React.CSSProperties = {
  padding: '10px 14px', color: '#64748b', fontWeight: 600,
  fontSize: 12, textTransform: 'uppercase', letterSpacing: 0.5,
};
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b' };

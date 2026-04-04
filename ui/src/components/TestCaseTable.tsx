import { useState } from 'react';
import { EditTestCaseDialog } from './EditTestCaseDialog';
import type { TaskEntry, ApiTestCase } from '../types';

const methodColor: Record<string, { fg: string; bg: string }> = {
  GET:    { fg: '#166534', bg: '#dcfce7' },
  POST:   { fg: '#1e40af', bg: '#dbeafe' },
  PUT:    { fg: '#92400e', bg: '#fef3c7' },
  PATCH:  { fg: '#6b21a8', bg: '#f3e8ff' },
  DELETE: { fg: '#991b1b', bg: '#fee2e2' },
};

interface Props {
  tasks: TaskEntry[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

export function TestCaseTable({ tasks, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const [editing, setEditing] = useState<{
    tc: ApiTestCase; taskId: string; caseIndex: number;
  } | null>(null);

  const allCases = tasks.flatMap(t =>
    t.testCases.map((tc, idx) => ({
      ...tc,
      taskId: t.taskId,
      taskDescription: t.taskDescription,
      caseIndex: idx,
    }))
  );

  const editable = !!moduleId && !!testSetId;

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Method</th>
            <th style={thStyle}>Endpoint</th>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Expected</th>
            {editable && <th style={{ ...thStyle, width: 40 }}></th>}
          </tr>
        </thead>
        <tbody>
          {allCases.map((tc, i) => (
            <tr key={i} style={{ borderBottom: '1px solid #f1f5f9', cursor: editable ? 'pointer' : undefined }}
              onMouseEnter={e => (e.currentTarget.style.background = '#f8fafc')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
              onClick={editable ? () => setEditing({ tc, taskId: tc.taskId, caseIndex: tc.caseIndex }) : undefined}
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
              <td style={{ ...tdStyle, color: '#475569' }}>{tc.name}</td>
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
        <EditTestCaseDialog
          open
          testCase={editing.tc}
          taskId={editing.taskId}
          caseIndex={editing.caseIndex}
          moduleId={moduleId}
          testSetId={testSetId}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); onTestCaseUpdated?.(); }}
        />
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { padding: '10px 14px', color: '#64748b', fontWeight: 600, fontSize: 12, textTransform: 'uppercase', letterSpacing: 0.5 };
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b' };

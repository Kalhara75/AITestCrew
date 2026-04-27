import { useState } from 'react';
import { updateObjective, deleteObjective } from '../api/modules';
import { EditDesktopUiTestCaseDialog } from './EditDesktopUiTestCaseDialog';
import { PostStepsPanel } from './PostStepsPanel';
import type { TestObjective, DesktopUiStep } from '../types';

interface Props {
  objectives: TestObjective[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

/** Best short label for a desktop element selector */
function selectorLabel(s: DesktopUiStep): string {
  if (s.automationId) return s.automationId;
  if (s.name) return s.name;
  if (s.className && s.controlType) return `${s.className}:${s.controlType}`;
  if (s.treePath) return s.treePath;
  if (s.menuPath) return s.menuPath;
  if (s.windowTitle) return s.windowTitle;
  return '(none)';
}

export function DesktopUiTestCaseTable({ objectives, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const [editing, setEditing] = useState<{
    objective: TestObjective;
    stepIndex: number;
  } | null>(null);
  const [deletingKey, setDeletingKey] = useState<string | null>(null);
  const [confirmDeleteKey, setConfirmDeleteKey] = useState<string | null>(null);

  const allCases = objectives
    .filter(o => o.desktopUiSteps.length > 0)
    .flatMap(o => o.desktopUiSteps.map((step, idx) => ({
      ...step,
      objectiveId: o.id,
      objectiveName: o.name,
      stepIndex: idx,
      objective: o,
      key: `${o.id}-desktop-${idx}`,
    })));

  const editable = !!moduleId && !!testSetId;

  const handleInlineDelete = async (tc: typeof allCases[number]) => {
    if (!moduleId || !testSetId) return;
    const key = tc.key;
    setDeletingKey(key);
    try {
      if (tc.objective.desktopUiSteps.length <= 1) {
        await deleteObjective(moduleId, testSetId, tc.objective.id);
      } else {
        const updatedSteps = tc.objective.desktopUiSteps.filter((_, i) => i !== tc.stepIndex);
        await updateObjective(moduleId, testSetId, tc.objective.id, { ...tc.objective, desktopUiSteps: updatedSteps });
      }
      setConfirmDeleteKey(null);
      onTestCaseUpdated?.();
    } catch {
      setDeletingKey(null);
      setConfirmDeleteKey(null);
    }
  };

  if (allCases.length === 0) {
    return <p style={{ color: '#94a3b8', fontSize: 14, padding: '8px 0' }}>No desktop UI test cases.</p>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Steps</th>
            <th style={thStyle}>Preview</th>
            <th style={thStyle}>Screenshot</th>
            {editable && <th style={{ ...thStyle, width: 70 }}></th>}
          </tr>
        </thead>
        <tbody>
          {allCases.map((tc) => (
            <tr
              key={tc.key}
              style={{ borderBottom: '1px solid #f1f5f9', cursor: editable ? 'pointer' : undefined }}
              onMouseEnter={e => (e.currentTarget.style.background = '#f8fafc')}
              onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
              onClick={editable ? () => setEditing({ objective: tc.objective, stepIndex: tc.stepIndex }) : undefined}
            >
              <td style={tdStyle}>{tc.objectiveName}</td>
              <td style={tdStyle}>
                <span style={{
                  display: 'inline-flex', alignItems: 'center', gap: 4,
                  fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13,
                  color: '#1e40af',
                }}>
                  {tc.steps.length} steps
                </span>
              </td>
              <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, color: '#64748b' }}>
                {tc.steps.slice(0, 3).map((s, i) => (
                  <span key={i} title={`${s.action}: ${selectorLabel(s)}`}>
                    {s.action}
                    {i < Math.min(tc.steps.length, 3) - 1 ? ' \u2192 ' : ''}
                  </span>
                ))}
                {tc.steps.length > 3 && <span> +{tc.steps.length - 3}</span>}
              </td>
              <td style={tdStyle}>
                <span style={{ fontSize: 13, color: tc.takeScreenshotOnFailure ? '#166534' : '#94a3b8' }}>
                  {tc.takeScreenshotOnFailure ? '\u2713 on fail' : '\u2014'}
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

      {allCases.some(tc => (tc.postSteps?.length ?? 0) > 0) && (
        <div style={{ marginTop: 16 }}>
          {allCases.map(tc => (tc.postSteps?.length ?? 0) > 0 && (
            <PostStepsPanel
              key={`${tc.key}-poststeps`}
              parentKind="DesktopUi"
              parentIndex={tc.stepIndex}
              objectiveId={tc.objectiveId}
              caseName={tc.objectiveName}
              postSteps={tc.postSteps ?? []}
              moduleId={moduleId}
              testSetId={testSetId}
              onChanged={onTestCaseUpdated}
            />
          ))}
        </div>
      )}

      {editing && moduleId && testSetId && (() => {
        const obj = editing.objective;
        const idx = editing.stepIndex;
        const isLastStep = obj.desktopUiSteps.length <= 1;
        return (
          <EditDesktopUiTestCaseDialog
            open
            definition={obj.desktopUiSteps[idx]}
            caseName={obj.name}
            deleteConfirmMessage={isLastStep
              ? 'Last step -- entire test case will be deleted.'
              : 'Delete this step?'}
            onClose={() => setEditing(null)}
            onSave={async ({ name, definition }) => {
              const updatedSteps = [...obj.desktopUiSteps];
              updatedSteps[idx] = definition;
              await updateObjective(moduleId, testSetId, obj.id, { ...obj, name, desktopUiSteps: updatedSteps });
              setEditing(null);
              onTestCaseUpdated?.();
            }}
            onDelete={async () => {
              if (isLastStep) {
                await deleteObjective(moduleId, testSetId, obj.id);
              } else {
                const updatedSteps = obj.desktopUiSteps.filter((_, i) => i !== idx);
                await updateObjective(moduleId, testSetId, obj.id, { ...obj, desktopUiSteps: updatedSteps });
              }
              setEditing(null);
              onTestCaseUpdated?.();
            }}
          />
        );
      })()}
    </div>
  );
}

const thStyle: React.CSSProperties = {
  padding: '10px 14px', color: '#64748b', fontWeight: 600,
  fontSize: 12, textTransform: 'uppercase', letterSpacing: 0.5,
};
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b' };
const inlineDeleteConfirmStyle: React.CSSProperties = {
  background: '#dc2626', color: '#fff', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};
const inlineCancelStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};

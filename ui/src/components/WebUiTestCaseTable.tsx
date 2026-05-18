import { useState } from 'react';
import { updateObjective, deleteObjective } from '../api/modules';
import { startRecording } from '../api/recordings';
import { EditWebUiTestCaseDialog } from './EditWebUiTestCaseDialog';
import { PostStepsPanel } from './PostStepsPanel';
import { StatusBadge } from './execution/StatusBadge';
import type { TestObjective, ObjectiveStatus } from '../types';

interface Props {
  objectives: TestObjective[];
  objectiveStatuses?: Record<string, ObjectiveStatus>;
  moduleId?: string;
  testSetId?: string;
  environmentKey?: string | null;
  onTestCaseUpdated?: () => void;
}

export function WebUiTestCaseTable({ objectives, objectiveStatuses, moduleId, testSetId, environmentKey, onTestCaseUpdated }: Props) {
  const [editing, setEditing] = useState<{
    objective: TestObjective;
    stepIndex: number;
  } | null>(null);
  const [deletingKey, setDeletingKey] = useState<string | null>(null);
  const [confirmDeleteKey, setConfirmDeleteKey] = useState<string | null>(null);
  const [recordingPlaceholderId, setRecordingPlaceholderId] = useState<string | null>(null);

  // Imported placeholders: source = 'ImportedFromXray' (not yet +Recorded) with empty steps.
  const importedPlaceholders = objectives.filter(o =>
    o.source === 'ImportedFromXray' &&
    (o.webUiSteps.length === 0 || (o.webUiSteps.length === 1 && o.webUiSteps[0].steps.length === 0))
  );

  const allCases = objectives
    .filter(o => o.webUiSteps.length > 0)
    .flatMap(o => o.webUiSteps.map((step, idx) => ({
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
      if (tc.objective.webUiSteps.length <= 1) {
        await deleteObjective(moduleId, testSetId, tc.objective.id);
      } else {
        const updatedSteps = tc.objective.webUiSteps.filter((_, i) => i !== tc.stepIndex);
        await updateObjective(moduleId, testSetId, tc.objective.id, { ...tc.objective, webUiSteps: updatedSteps });
      }
      setConfirmDeleteKey(null);
      onTestCaseUpdated?.();
    } catch {
      setDeletingKey(null);
      setConfirmDeleteKey(null);
    }
  };

  if (allCases.length === 0 && importedPlaceholders.length === 0) {
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
            <th style={{ ...thStyle, width: 90 }}>Last Result</th>
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
              <td style={tdStyle}>
                <StatusBadge status={objectiveStatuses?.[tc.objectiveId]?.status ?? null} size="sm" />
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
              parentKind="WebUi"
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

      {editing && moduleId && testSetId && (
        <EditWebUiTestCaseDialog
          open
          definition={editing.objective.webUiSteps[editing.stepIndex]}
          caseName={editing.objective.name}
          onClose={() => setEditing(null)}
          onSave={async ({ name, definition }) => {
            const updatedSteps = [...editing.objective.webUiSteps];
            updatedSteps[editing.stepIndex] = definition;
            await updateObjective(moduleId, testSetId, editing.objective.id,
              { ...editing.objective, name, webUiSteps: updatedSteps });
            setEditing(null);
            onTestCaseUpdated?.();
          }}
          onDelete={async () => {
            if (editing.objective.webUiSteps.length <= 1) {
              await deleteObjective(moduleId, testSetId, editing.objective.id);
            } else {
              const updatedSteps = editing.objective.webUiSteps.filter((_, i) => i !== editing.stepIndex);
              await updateObjective(moduleId, testSetId, editing.objective.id,
                { ...editing.objective, webUiSteps: updatedSteps });
            }
            setEditing(null);
            onTestCaseUpdated?.();
          }}
          deleteConfirmMessage={
            editing.objective.webUiSteps.length <= 1
              ? 'Last step — entire test case will be deleted.'
              : 'Delete this step?'
          }
        />
      )}
      {importedPlaceholders.length > 0 && editable && (
        <div style={{ marginTop: 16, borderTop: '1px solid #f1f5f9', paddingTop: 12 }}>
          <p style={{ fontSize: 12, color: '#64748b', marginBottom: 8, fontWeight: 600 }}>
            Imported placeholders — ready to record
          </p>
          {importedPlaceholders.map(obj => (
            <div
              key={obj.id}
              style={{
                display: 'flex', alignItems: 'center', gap: 12,
                padding: '8px 14px', borderRadius: 6,
                background: '#fefce8', border: '1px solid #fde68a',
                marginBottom: 6,
              }}
            >
              <span style={{ flex: 1, fontSize: 14, color: '#92400e', fontWeight: 500 }}>{obj.name}</span>
              <span style={{
                fontSize: 10, fontWeight: 600,
                padding: '1px 6px', borderRadius: 4,
                background: '#fef3c7', color: '#92400e',
                border: '1px solid #fde68a',
              }}>Imported — 0 steps</span>
              <button
                disabled={recordingPlaceholderId === obj.id}
                onClick={async () => {
                  if (!moduleId || !testSetId) return;
                  const tgt = (obj.targetType === 'UI_Web_Blazor' || obj.targetType === 'UI_Web_MVC')
                    ? obj.targetType as 'UI_Web_Blazor' | 'UI_Web_MVC'
                    : 'UI_Web_Blazor';
                  setRecordingPlaceholderId(obj.id);
                  try {
                    await startRecording({
                      kind: 'Record',
                      target: tgt,
                      moduleId,
                      testSetId,
                      caseName: obj.name,
                      ...(environmentKey ? { environmentKey } : {}),
                    });
                    onTestCaseUpdated?.();
                  } finally {
                    setRecordingPlaceholderId(null);
                  }
                }}
                style={{
                  padding: '4px 12px', fontSize: 12, fontWeight: 600,
                  background: recordingPlaceholderId === obj.id ? '#d1d5db' : '#2563eb',
                  color: '#fff', border: 'none', borderRadius: 4, cursor: 'pointer',
                  whiteSpace: 'nowrap',
                }}
              >
                {recordingPlaceholderId === obj.id ? 'Starting...' : 'Record this'}
              </button>
            </div>
          ))}
        </div>
      )}

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

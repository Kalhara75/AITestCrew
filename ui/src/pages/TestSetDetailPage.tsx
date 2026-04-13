import { useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchTestSet, fetchRuns } from '../api/testSets';
import { fetchModuleTestSet, fetchModuleRuns, fetchModule, deleteTestSet, deleteObjective } from '../api/modules';
import { TestCaseTable } from '../components/TestCaseTable';
import { WebUiTestCaseTable } from '../components/WebUiTestCaseTable';
import { DesktopUiTestCaseTable } from '../components/DesktopUiTestCaseTable';
import { RunHistoryTable } from '../components/RunHistoryTable';
import { TriggerRunButton } from '../components/TriggerRunButton';
import { StatusBadge } from '../components/StatusBadge';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { MoveObjectiveDialog } from '../components/MoveObjectiveDialog';
import { TriggerObjectiveRunButton } from '../components/TriggerObjectiveRunButton';
import { AiPatchPanel } from '../components/AiPatchPanel';
import { SetupStepsPanel } from '../components/SetupStepsPanel';
import type { TestObjective, RunSummary, ObjectiveStatus } from '../types';

export function TestSetDetailPage() {
  const { id, moduleId } = useParams<{ id: string; moduleId?: string }>();
  const isModuleScoped = !!moduleId;
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [moveObjective, setMoveObjective] = useState<string | null>(null);
  const [selectedObjectiveId, setSelectedObjectiveId] = useState<string | null>(null);
  const [deleteObjectiveId, setDeleteObjectiveId] = useState<string | null>(null);
  const [deletingObjective, setDeletingObjective] = useState(false);

  const { data: module } = useQuery({
    queryKey: ['module', moduleId],
    queryFn: () => fetchModule(moduleId!),
    enabled: isModuleScoped,
  });

  const { data: testSet, isLoading, error } = useQuery({
    queryKey: ['testSet', moduleId, id],
    queryFn: () => isModuleScoped
      ? fetchModuleTestSet(moduleId!, id!)
      : fetchTestSet(id!),
    enabled: !!id,
  });

  const { data: runs } = useQuery({
    queryKey: ['runs', moduleId, id],
    queryFn: () => isModuleScoped
      ? fetchModuleRuns(moduleId!, id!)
      : fetchRuns(id!),
    enabled: !!id,
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading test set...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;
  if (!testSet) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Test set not found.</p>;

  const totalObjectives = testSet.testObjectives.length;
  const displayTitle = testSet.name || testSet.objective || testSet.id;
  const selectedObjective = testSet.testObjectives.find(o => o.id === selectedObjectiveId) ?? null;

  const handleDelete = async () => {
    if (!isModuleScoped) return;
    setDeleting(true);
    try {
      await deleteTestSet(moduleId!, id!);
      navigate(`/modules/${moduleId}`);
    } catch {
      setDeleting(false);
      setShowDeleteConfirm(false);
    }
  };

  const handleObjectiveMoved = () => {
    queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, id] });
  };

  const handleTestCaseUpdated = () => {
    queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, id] });
  };

  const handleDeleteObjective = async () => {
    if (!deleteObjectiveId || !isModuleScoped) return;
    setDeletingObjective(true);
    try {
      await deleteObjective(moduleId!, id!, deleteObjectiveId);
      queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, id] });
      queryClient.invalidateQueries({ queryKey: ['runs', moduleId, id] });
      if (selectedObjectiveId === deleteObjectiveId) setSelectedObjectiveId(null);
    } finally {
      setDeletingObjective(false);
      setDeleteObjectiveId(null);
    }
  };

  return (
    <div>
      {/* Breadcrumb */}
      <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
        {isModuleScoped ? (
          <>
            <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Modules</Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <Link to={`/modules/${moduleId}`} style={{ color: '#2563eb', textDecoration: 'none' }}>
              {module?.name || moduleId}
            </Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <span style={{ color: '#64748b' }}>{displayTitle}</span>
          </>
        ) : (
          <>
            <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Dashboard</Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <span style={{ color: '#64748b' }}>{testSet.id}</span>
          </>
        )}
      </div>

      {/* Header card */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 24, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 280 }}>
            <div style={{ marginBottom: 12 }}>
              <StatusBadge status={testSet.lastRunStatus} size="md" />
            </div>
            <h1 style={{ margin: '0 0 16px', fontSize: 22, fontWeight: 700, color: '#0f172a', lineHeight: 1.4 }}>
              {displayTitle}
            </h1>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <StatPill label="Test Cases" value={totalObjectives} />
              <StatPill label="Runs" value={testSet.runCount} />
              <StatPill label="Created" value={new Date(testSet.createdAt).toLocaleDateString()} />
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', flexDirection: 'column' }}>
            <TriggerRunButton testSetId={testSet.id} moduleId={moduleId} apiStackKey={testSet.apiStackKey} apiModule={testSet.apiModule} />
            {isModuleScoped && (
              <button onClick={() => setShowDeleteConfirm(true)} style={deleteBtnStyle}>
                Delete Test Set
              </button>
            )}
          </div>
        </div>
      </div>

      {/* Setup Steps (e.g. login) — shown for module-scoped test sets with web UI objectives */}
      {isModuleScoped && (
        <SetupStepsPanel
          setupStartUrl={testSet.setupStartUrl ?? ''}
          setupSteps={testSet.setupSteps ?? []}
          moduleId={moduleId!}
          testSetId={testSet.id}
          onUpdated={() => queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, id] })}
        />
      )}

      {/* Test Cases list — clean table */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <SectionHeader title="Test Cases" count={totalObjectives} />
        {totalObjectives === 0 ? (
          <p style={{ color: '#94a3b8', fontSize: 14 }}>No test cases in this test set yet.</p>
        ) : (
          <ObjectiveListTable
            objectives={testSet.testObjectives}
            runs={runs || []}
            objectiveStatuses={testSet.objectiveStatuses}
            testSetId={testSet.id}
            moduleId={moduleId}
            apiStackKey={testSet.apiStackKey}
            apiModule={testSet.apiModule}
            selectedId={selectedObjectiveId}
            onSelect={(objId) => setSelectedObjectiveId(objId === selectedObjectiveId ? null : objId)}
            onMove={isModuleScoped ? (obj) => setMoveObjective(obj) : undefined}
            onDelete={isModuleScoped ? (objId) => setDeleteObjectiveId(objId) : undefined}
          />
        )}
      </div>

      {/* Selected objective detail panel */}
      {selectedObjective && (
        <div style={cardStyle({ marginBottom: 24, borderColor: '#bfdbfe', borderWidth: 2 })}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
            <div>
              <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>
                {selectedObjective.name}
              </h2>
              <p style={{ margin: '4px 0 0', fontSize: 12, color: '#94a3b8' }}>
                {selectedObjective.agentName} &middot; {selectedObjective.targetType} &middot; {selectedObjective.stepCount} step{selectedObjective.stepCount !== 1 ? 's' : ''}
              </p>
            </div>
            <button
              onClick={() => setSelectedObjectiveId(null)}
              style={{ background: 'none', border: 'none', fontSize: 20, color: '#94a3b8', cursor: 'pointer', padding: '4px 8px' }}
              title="Close"
            >&times;</button>
          </div>

          {/* Steps */}
          <SectionHeader title="Steps" count={selectedObjective.stepCount} />
          {isModuleScoped && selectedObjective.apiSteps.length > 0 && (
            <AiPatchPanel
              moduleId={moduleId!}
              testSetId={id!}
              objectives={[selectedObjective]}
              onApplied={handleTestCaseUpdated}
            />
          )}
          {selectedObjective.apiSteps.length > 0 && (
            <TestCaseTable
              objectives={[selectedObjective]}
              moduleId={moduleId}
              testSetId={id}
              onTestCaseUpdated={handleTestCaseUpdated}
            />
          )}
          {selectedObjective.webUiSteps.length > 0 && (
            <WebUiTestCaseTable
              objectives={[selectedObjective]}
              moduleId={moduleId}
              testSetId={id}
              onTestCaseUpdated={handleTestCaseUpdated}
            />
          )}
          {selectedObjective.desktopUiSteps?.length > 0 && (
            <DesktopUiTestCaseTable
              objectives={[selectedObjective]}
              moduleId={moduleId}
              testSetId={id}
              onTestCaseUpdated={handleTestCaseUpdated}
            />
          )}

          {/* Run history for this objective */}
          <div style={{ marginTop: 24 }}>
            <SectionHeader title="Execution History" count={runs?.length || 0} />
            <RunHistoryTable runs={runs || []} testSetId={testSet.id} moduleId={moduleId} />
          </div>
        </div>
      )}

      {/* Delete confirmation dialog */}
      <ConfirmDialog
        open={showDeleteConfirm}
        title="Delete Test Set"
        message={`This will permanently delete "${displayTitle}" and all ${runs?.length ?? 0} execution run(s). This action cannot be undone.`}
        confirmLabel="Delete"
        confirmDestructive
        loading={deleting}
        onConfirm={handleDelete}
        onCancel={() => setShowDeleteConfirm(false)}
      />

      {/* Delete objective confirmation dialog */}
      <ConfirmDialog
        open={!!deleteObjectiveId}
        title="Delete Test Case"
        message={`This will permanently delete this test case and remove its results from all execution runs. This action cannot be undone.`}
        confirmLabel="Delete"
        confirmDestructive
        loading={deletingObjective}
        onConfirm={handleDeleteObjective}
        onCancel={() => setDeleteObjectiveId(null)}
      />

      {/* Move objective dialog */}
      {isModuleScoped && moveObjective !== null && (
        <MoveObjectiveDialog
          open
          objective={moveObjective}
          objectiveDisplayName={testSet.objectiveNames?.[moveObjective]}
          sourceModuleId={moduleId!}
          sourceTestSetId={id!}
          onClose={() => setMoveObjective(null)}
          onMoved={handleObjectiveMoved}
        />
      )}
    </div>
  );
}

/* ─── Test Case List Table ─── */

function ObjectiveListTable({
  objectives,
  runs,
  objectiveStatuses,
  testSetId,
  moduleId,
  apiStackKey,
  apiModule,
  selectedId,
  onSelect,
  onMove,
  onDelete,
}: {
  objectives: TestObjective[];
  runs: RunSummary[];
  objectiveStatuses?: Record<string, ObjectiveStatus>;
  testSetId: string;
  moduleId?: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onMove?: (parentObjective: string) => void;
  onDelete?: (objectiveId: string) => void;
}) {
  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0' }}>
            <th style={thStyle}>TEST CASE</th>
            <th style={{ ...thStyle, width: 80 }}>STEPS</th>
            <th style={{ ...thStyle, width: 90 }}>TYPE</th>
            <th style={{ ...thStyle, width: 100 }}>STATUS</th>
            <th style={{ ...thStyle, width: 180 }}>LAST RUN</th>
            <th style={{ ...thStyle, width: 60 }}></th>
            {onMove && <th style={{ ...thStyle, width: 60 }}></th>}
            {onDelete && <th style={{ ...thStyle, width: 60 }}></th>}
          </tr>
        </thead>
        <tbody>
          {objectives.map(obj => {
            const isSelected = obj.id === selectedId;
            const objStatus = objectiveStatuses?.[obj.id];
            return (
              <tr
                key={obj.id}
                onClick={() => onSelect(obj.id)}
                style={{
                  borderBottom: '1px solid #f1f5f9',
                  cursor: 'pointer',
                  background: isSelected ? '#eff6ff' : 'transparent',
                  transition: 'background 0.15s',
                }}
                onMouseEnter={(e) => { if (!isSelected) e.currentTarget.style.background = '#f8fafc'; }}
                onMouseLeave={(e) => { if (!isSelected) e.currentTarget.style.background = 'transparent'; }}
              >
                <td style={{ ...tdStyle, fontWeight: 500, color: '#0f172a' }}>
                  {obj.name}
                  {(obj.source ?? 'Generated') === 'Recorded' && (
                    <span style={{
                      fontSize: 10, fontWeight: 600, marginLeft: 8,
                      padding: '1px 6px', borderRadius: 4,
                      background: '#fef3c7', color: '#92400e',
                      border: '1px solid #fde68a',
                    }}>Recorded</span>
                  )}
                </td>
                <td style={{ ...tdStyle, textAlign: 'center', color: '#64748b' }}>
                  {obj.stepCount}
                </td>
                <td style={tdStyle}>
                  <span style={typeBadgeStyle(obj.targetType)}>
                    {obj.apiSteps.length > 0 ? 'API' : obj.desktopUiSteps?.length > 0 ? 'Desktop UI' : 'Web UI'}
                  </span>
                </td>
                <td style={tdStyle}>
                  <StatusBadge status={objStatus?.status ?? null} size="sm" />
                </td>
                <td style={{ ...tdStyle, color: '#64748b', fontSize: 12 }}>
                  {objStatus?.completedAt ? new Date(objStatus.completedAt).toLocaleString() : '—'}
                </td>
                <td style={tdStyle}>
                  <TriggerObjectiveRunButton
                    testSetId={testSetId}
                    objectiveId={obj.id}
                    parentObjective={obj.parentObjective}
                    source={obj.source ?? 'Generated'}
                    moduleId={moduleId}
                    apiStackKey={apiStackKey}
                    apiModule={apiModule}
                  />
                </td>
                {onMove && (
                  <td style={tdStyle}>
                    <button
                      onClick={(e) => { e.stopPropagation(); onMove(obj.parentObjective); }}
                      style={moveBtnStyle}
                      title="Move to another test set"
                    >
                      Move
                    </button>
                  </td>
                )}
                {onDelete && (
                  <td style={tdStyle}>
                    <button
                      onClick={(e) => { e.stopPropagation(); onDelete(obj.id); }}
                      style={deleteRowBtnStyle}
                      title="Delete test case"
                    >
                      Delete
                    </button>
                  </td>
                )}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/* ─── Shared components ─── */

function SectionHeader({ title, count }: { title: string; count: number }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 20 }}>
      <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>{title}</h2>
      <span style={{
        fontSize: 12, fontWeight: 600, color: '#64748b',
        background: '#f1f5f9', padding: '2px 10px', borderRadius: 12,
      }}>{count}</span>
    </div>
  );
}

function StatPill({ label, value }: { label: string; value: string | number }) {
  return (
    <span style={{
      fontSize: 13, color: '#475569', background: '#f8fafc',
      padding: '4px 12px', borderRadius: 6, border: '1px solid #f1f5f9',
    }}>
      <span style={{ color: '#94a3b8', marginRight: 4 }}>{label}:</span>
      <span style={{ fontWeight: 600 }}>{value}</span>
    </span>
  );
}

function typeBadgeStyle(targetType: string): React.CSSProperties {
  const isApi = targetType.startsWith('API');
  return {
    fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
    background: isApi ? '#eff6ff' : '#f0fdf4',
    color: isApi ? '#2563eb' : '#16a34a',
    border: `1px solid ${isApi ? '#bfdbfe' : '#bbf7d0'}`,
  };
}

const thStyle: React.CSSProperties = {
  textAlign: 'left', padding: '8px 12px', fontSize: 11, fontWeight: 600,
  color: '#64748b', textTransform: 'uppercase', letterSpacing: 0.5,
};

const tdStyle: React.CSSProperties = {
  padding: '10px 12px', verticalAlign: 'middle',
};

const deleteBtnStyle: React.CSSProperties = {
  background: '#fef2f2', color: '#dc2626', border: '1px solid #fecaca',
  padding: '8px 18px', borderRadius: 8, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', width: '100%',
};

const deleteRowBtnStyle: React.CSSProperties = {
  background: '#fef2f2', color: '#dc2626', border: '1px solid #fecaca',
  padding: '1px 8px', borderRadius: 4, fontSize: 11, fontWeight: 600,
  cursor: 'pointer', lineHeight: '18px', flexShrink: 0,
};

const moveBtnStyle: React.CSSProperties = {
  background: 'none', color: '#2563eb', border: '1px solid #bfdbfe',
  padding: '1px 8px', borderRadius: 4, fontSize: 11, fontWeight: 600,
  cursor: 'pointer', lineHeight: '18px', flexShrink: 0,
};

function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return {
    background: '#fff', borderRadius: 10,
    border: '1px solid #e2e8f0', padding: 24, ...extra,
  };
}

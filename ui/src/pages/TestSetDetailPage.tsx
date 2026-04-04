import { useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchTestSet, fetchRuns } from '../api/testSets';
import { fetchModuleTestSet, fetchModuleRuns, fetchModule, deleteTestSet } from '../api/modules';
import { TestCaseTable } from '../components/TestCaseTable';
import { RunHistoryTable } from '../components/RunHistoryTable';
import { TriggerRunButton } from '../components/TriggerRunButton';
import { StatusBadge } from '../components/StatusBadge';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { MoveObjectiveDialog } from '../components/MoveObjectiveDialog';

export function TestSetDetailPage() {
  const { id, moduleId } = useParams<{ id: string; moduleId?: string }>();
  const isModuleScoped = !!moduleId;
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [moveObjective, setMoveObjective] = useState<string | null>(null);

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

  const totalCases = testSet.tasks.reduce((sum, t) => sum + t.testCases.length, 0);
  const displayTitle = testSet.name || testSet.objective || testSet.id;
  const objectives = testSet.objectives?.length > 0 ? testSet.objectives : (testSet.objective ? [testSet.objective] : []);

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

            {/* Objectives list */}
            {objectives.length > 0 && (
              <div style={{ marginBottom: 16 }}>
                <div style={{ fontSize: 12, color: '#94a3b8', marginBottom: 6, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5 }}>
                  Objectives ({objectives.length})
                </div>
                <ul style={{ margin: 0, paddingLeft: 18, listStyle: 'disc' }}>
                  {objectives.map((obj, i) => {
                    const displayName = testSet.objectiveNames?.[obj];
                    return (
                      <li key={i} style={{ fontSize: 13, color: '#475569', lineHeight: 1.8, display: 'list-item' }}>
                        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }} title={obj}>
                          {displayName || obj}
                          {isModuleScoped && (
                            <button
                              onClick={() => setMoveObjective(obj)}
                              style={moveBtnStyle}
                              title="Move to another test set"
                            >
                              Move
                            </button>
                          )}
                        </span>
                      </li>
                    );
                  })}
                </ul>
              </div>
            )}

            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <StatPill label="Tasks" value={testSet.tasks.length} />
              <StatPill label="Test Cases" value={totalCases} />
              <StatPill label="Runs" value={testSet.runCount} />
              <StatPill label="Created" value={new Date(testSet.createdAt).toLocaleDateString()} />
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start', flexDirection: 'column' }}>
            <TriggerRunButton testSetId={testSet.id} objective={testSet.objective} moduleId={moduleId} />
            {isModuleScoped && (
              <button
                onClick={() => setShowDeleteConfirm(true)}
                style={deleteBtnStyle}
              >
                Delete Test Set
              </button>
            )}
          </div>
        </div>
      </div>

      {/* Test Cases */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <SectionHeader title="Test Cases" count={totalCases} />
        {totalCases === 0 ? (
          <p style={{ color: '#94a3b8', fontSize: 14 }}>No test cases in this test set.</p>
        ) : (
          <TestCaseTable tasks={testSet.tasks} />
        )}
      </div>

      {/* Execution History */}
      <div style={cardStyle({})}>
        <SectionHeader title="Execution History" count={runs?.length || 0} />
        <RunHistoryTable runs={runs || []} testSetId={testSet.id} moduleId={moduleId} />
      </div>

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

function SectionHeader({ title, count }: { title: string; count: number }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 20 }}>
      <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>{title}</h2>
      <span style={{
        fontSize: 12,
        fontWeight: 600,
        color: '#64748b',
        background: '#f1f5f9',
        padding: '2px 10px',
        borderRadius: 12,
      }}>{count}</span>
    </div>
  );
}

function StatPill({ label, value }: { label: string; value: string | number }) {
  return (
    <span style={{
      fontSize: 13,
      color: '#475569',
      background: '#f8fafc',
      padding: '4px 12px',
      borderRadius: 6,
      border: '1px solid #f1f5f9',
    }}>
      <span style={{ color: '#94a3b8', marginRight: 4 }}>{label}:</span>
      <span style={{ fontWeight: 600 }}>{value}</span>
    </span>
  );
}

const deleteBtnStyle: React.CSSProperties = {
  background: '#fef2f2', color: '#dc2626', border: '1px solid #fecaca',
  padding: '8px 18px', borderRadius: 8, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', width: '100%',
};

const moveBtnStyle: React.CSSProperties = {
  background: 'none', color: '#2563eb', border: '1px solid #bfdbfe',
  padding: '1px 8px', borderRadius: 4, fontSize: 11, fontWeight: 600,
  cursor: 'pointer', lineHeight: '18px', flexShrink: 0,
};

function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return {
    background: '#fff',
    borderRadius: 10,
    border: '1px solid #e2e8f0',
    padding: 24,
    ...extra,
  };
}

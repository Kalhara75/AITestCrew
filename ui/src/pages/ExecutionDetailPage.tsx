import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchRun } from '../api/testSets';
import { fetchModuleRun, fetchModule } from '../api/modules';
import { StatusBadge } from '../components/execution/StatusBadge';
import { ModeBadge } from '../components/execution/ModeBadge';
import { StatsBar } from '../components/execution/StatsBar';
import { StepList } from '../components/StepList';

export function ExecutionDetailPage() {
  const { id, runId, moduleId } = useParams<{ id: string; runId: string; moduleId?: string }>();
  const isModuleScoped = !!moduleId;

  const { data: module } = useQuery({
    queryKey: ['module', moduleId],
    queryFn: () => fetchModule(moduleId!),
    enabled: isModuleScoped,
  });

  const { data: run, isLoading, error } = useQuery({
    queryKey: ['run', moduleId, id, runId],
    queryFn: () => isModuleScoped
      ? fetchModuleRun(moduleId!, id!, runId!)
      : fetchRun(id!, runId!),
    enabled: !!id && !!runId,
    // While the run is still moving (executing or awaiting a deferred verification),
    // auto-refetch so the dashboard reflects status/step changes without a manual
    // reload. Terminal states stop the poll to avoid needless churn.
    refetchInterval: (query) => {
      const s = query.state.data?.status;
      const terminal = s === 'Passed' || s === 'Failed' || s === 'Error'
        || s === 'Skipped' || s === 'Cancelled';
      return terminal ? false : 3000;
    },
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading execution details...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;
  if (!run) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Run not found.</p>;

  const testSetPath = isModuleScoped
    ? `/modules/${moduleId}/testsets/${id}`
    : `/testsets/${id}`;

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
            <Link to={testSetPath} style={{ color: '#2563eb', textDecoration: 'none' }}>{id}</Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <span style={{ color: '#64748b' }}>{run.runId}</span>
          </>
        ) : (
          <>
            <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Dashboard</Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <Link to={testSetPath} style={{ color: '#2563eb', textDecoration: 'none' }}>{id}</Link>
            <span style={{ margin: '0 8px' }}>/</span>
            <span style={{ color: '#64748b' }}>{run.runId}</span>
          </>
        )}
      </div>

      {/* Header card */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <div style={{ marginBottom: 12, display: 'flex', alignItems: 'center', gap: 10 }}>
          <StatusBadge status={run.status} size="md" />
          <ModeBadge mode={run.mode} />
        </div>
        <h1 style={{ margin: '0 0 8px', fontSize: 22, fontWeight: 700, color: '#0f172a', lineHeight: 1.4 }}>
          {run.objective}
        </h1>
        <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
          Run ID: <code style={{ fontSize: 12, color: '#64748b' }}>{run.runId}</code>
        </div>

        {/* Stats grid — uses shared StatsBar component */}
        <StatsBar
          passed={run.passedObjectives}
          failed={run.failedObjectives}
          total={run.totalObjectives}
          errors={run.errorObjectives}
          duration={run.totalDuration}
          startedAt={run.startedAt}
          size="lg"
        />
      </div>

      {/* Summary */}
      {run.summary && (
        <div style={cardStyle({ marginBottom: 24 })}>
          <h2 style={{ margin: '0 0 12px', fontSize: 16, fontWeight: 600, color: '#0f172a' }}>Summary</h2>
          <p style={{ margin: 0, fontSize: 14, color: '#475569', lineHeight: 1.7 }}>{run.summary}</p>
        </div>
      )}

      {/* Objective Results */}
      <div style={{ marginBottom: 24 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
          <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>Objective Results</h2>
          <span style={{
            fontSize: 12,
            fontWeight: 600,
            color: '#64748b',
            background: '#f1f5f9',
            padding: '2px 10px',
            borderRadius: 12,
          }}>{run.objectiveResults.length}</span>
        </div>
        {run.objectiveResults.length === 0 ? (
          <p style={{ color: '#94a3b8', fontSize: 14 }}>No objective results recorded.</p>
        ) : (
          <StepList objectiveResults={run.objectiveResults} />
        )}
      </div>
    </div>
  );
}


function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return {
    background: '#fff',
    borderRadius: 10,
    border: '1px solid #e2e8f0',
    padding: 24,
    ...extra,
  };
}

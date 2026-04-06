import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchRun } from '../api/testSets';
import { fetchModuleRun, fetchModule } from '../api/modules';
import { StatusBadge } from '../components/StatusBadge';
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
          <span style={{
            fontSize: 12,
            fontWeight: 500,
            padding: '2px 8px',
            borderRadius: 4,
            background: run.mode === 'Reuse' ? '#f0f9ff' : run.mode === 'Rebaseline' ? '#fff7ed' : '#f8fafc',
            color: run.mode === 'Reuse' ? '#0369a1' : run.mode === 'Rebaseline' ? '#c2410c' : '#475569',
          }}>
            {run.mode}
          </span>
        </div>
        <h1 style={{ margin: '0 0 8px', fontSize: 22, fontWeight: 700, color: '#0f172a', lineHeight: 1.4 }}>
          {run.objective}
        </h1>
        <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
          Run ID: <code style={{ fontSize: 12, color: '#64748b' }}>{run.runId}</code>
        </div>

        {/* Stats grid */}
        <div style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))',
          gap: 16,
          paddingTop: 20,
          borderTop: '1px solid #f1f5f9',
        }}>
          <StatBox label="Total Objectives" value={run.totalObjectives} />
          <StatBox label="Passed" value={run.passedObjectives} color="#16a34a" />
          <StatBox label="Failed" value={run.failedObjectives} color={run.failedObjectives > 0 ? '#dc2626' : undefined} />
          {run.errorObjectives > 0 && <StatBox label="Errors" value={run.errorObjectives} color="#d97706" />}
          <StatBox label="Duration" value={run.totalDuration} mono />
          <StatBox label="Started" value={new Date(run.startedAt).toLocaleString()} />
        </div>
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

function StatBox({ label, value, color, mono }: { label: string; value: string | number; color?: string; mono?: boolean }) {
  return (
    <div style={{ background: '#f8fafc', padding: '12px 16px', borderRadius: 8 }}>
      <div style={{ fontSize: 11, color: '#94a3b8', marginBottom: 4, textTransform: 'uppercase', letterSpacing: 0.5, fontWeight: 600 }}>{label}</div>
      <div style={{
        fontSize: 18,
        fontWeight: 700,
        color: color || '#1e293b',
        fontFamily: mono || typeof value === 'string' ? 'ui-monospace, Consolas, monospace' : 'inherit',
        ...(typeof value === 'string' ? { fontSize: 13, fontWeight: 600 } : {}),
      }}>
        {value}
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

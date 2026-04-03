import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchTestSet, fetchRuns } from '../api/testSets';
import { TestCaseTable } from '../components/TestCaseTable';
import { RunHistoryTable } from '../components/RunHistoryTable';
import { TriggerRunButton } from '../components/TriggerRunButton';
import { StatusBadge } from '../components/StatusBadge';

export function TestSetDetailPage() {
  const { id } = useParams<{ id: string }>();

  const { data: testSet, isLoading, error } = useQuery({
    queryKey: ['testSet', id],
    queryFn: () => fetchTestSet(id!),
    enabled: !!id,
  });

  const { data: runs } = useQuery({
    queryKey: ['runs', id],
    queryFn: () => fetchRuns(id!),
    enabled: !!id,
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading test set...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;
  if (!testSet) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Test set not found.</p>;

  const totalCases = testSet.tasks.reduce((sum, t) => sum + t.testCases.length, 0);

  return (
    <div>
      {/* Breadcrumb */}
      <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
        <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Dashboard</Link>
        <span style={{ margin: '0 8px' }}>/</span>
        <span style={{ color: '#64748b' }}>{testSet.id}</span>
      </div>

      {/* Header card */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 24, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 280 }}>
            <div style={{ marginBottom: 12 }}>
              <StatusBadge status={testSet.lastRunStatus} size="md" />
            </div>
            <h1 style={{ margin: '0 0 16px', fontSize: 22, fontWeight: 700, color: '#0f172a', lineHeight: 1.4 }}>
              {testSet.objective}
            </h1>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <StatPill label="Tasks" value={testSet.tasks.length} />
              <StatPill label="Test Cases" value={totalCases} />
              <StatPill label="Runs" value={testSet.runCount} />
              <StatPill label="Created" value={new Date(testSet.createdAt).toLocaleDateString()} />
            </div>
          </div>
          <TriggerRunButton testSetId={testSet.id} objective={testSet.objective} />
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
        <RunHistoryTable runs={runs || []} testSetId={testSet.id} />
      </div>
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

function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return {
    background: '#fff',
    borderRadius: 10,
    border: '1px solid #e2e8f0',
    padding: 24,
    ...extra,
  };
}

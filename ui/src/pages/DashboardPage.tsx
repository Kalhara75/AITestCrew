import { useQuery } from '@tanstack/react-query';
import { fetchTestSets } from '../api/testSets';
import { TestSetCard } from '../components/TestSetCard';

export function DashboardPage() {
  const { data: testSets, isLoading, error } = useQuery({
    queryKey: ['testSets'],
    queryFn: fetchTestSets,
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading test sets...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 }}>
        <h1 style={{ margin: 0, fontSize: 24, fontWeight: 700, color: '#0f172a' }}>Test Sets</h1>
        <span style={{
          fontSize: 13,
          color: '#64748b',
          background: '#f1f5f9',
          padding: '4px 14px',
          borderRadius: 20,
          fontWeight: 500,
        }}>
          {testSets?.length || 0} saved
        </span>
      </div>

      {!testSets || testSets.length === 0 ? (
        <div style={{
          background: '#fff',
          borderRadius: 10,
          padding: 48,
          textAlign: 'center',
          border: '1px solid #e2e8f0',
        }}>
          <div style={{ fontSize: 40, marginBottom: 16 }}>{ '\u{1F9EA}' }</div>
          <p style={{ color: '#475569', fontSize: 16, margin: '0 0 8px', fontWeight: 500 }}>
            No test sets found
          </p>
          <p style={{ color: '#94a3b8', fontSize: 14, margin: '0 0 20px' }}>
            Run a test from the CLI to create one:
          </p>
          <code style={{
            display: 'inline-block',
            padding: '12px 20px',
            background: '#0f172a',
            borderRadius: 8,
            fontSize: 13,
            color: '#e2e8f0',
            fontFamily: 'ui-monospace, Consolas, monospace',
          }}>
            dotnet run --project src/AiTestCrew.Runner -- "your test objective"
          </code>
        </div>
      ) : (
        <div style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
          gap: 20,
        }}>
          {testSets.map(ts => <TestSetCard key={ts.id} ts={ts} />)}
        </div>
      )}
    </div>
  );
}

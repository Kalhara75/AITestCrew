import { useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchModule, fetchModuleTestSets } from '../api/modules';
import { TestSetCard } from '../components/TestSetCard';
import { CreateTestSetDialog } from '../components/CreateTestSetDialog';
import { RunObjectiveDialog } from '../components/RunObjectiveDialog';

export function ModuleDetailPage() {
  const { moduleId } = useParams<{ moduleId: string }>();
  const [showCreateTestSet, setShowCreateTestSet] = useState(false);
  const [showRunObjective, setShowRunObjective] = useState(false);

  const { data: module, isLoading: loadingModule, error: moduleError } = useQuery({
    queryKey: ['module', moduleId],
    queryFn: () => fetchModule(moduleId!),
    enabled: !!moduleId,
  });

  const { data: testSets, isLoading: loadingTestSets, refetch } = useQuery({
    queryKey: ['moduleTestSets', moduleId],
    queryFn: () => fetchModuleTestSets(moduleId!),
    enabled: !!moduleId,
  });

  if (loadingModule || loadingTestSets) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading module...</p>;
  if (moduleError) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(moduleError as Error).message}</p>;
  if (!module) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Module not found.</p>;

  return (
    <div>
      {/* Breadcrumb */}
      <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
        <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Modules</Link>
        <span style={{ margin: '0 8px' }}>/</span>
        <span style={{ color: '#64748b' }}>{module.name}</span>
      </div>

      {/* Header */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 16, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 280 }}>
            <h1 style={{ margin: '0 0 8px', fontSize: 22, fontWeight: 700, color: '#0f172a' }}>
              {module.name}
            </h1>
            {module.description && (
              <p style={{ margin: '0 0 16px', fontSize: 14, color: '#64748b', lineHeight: 1.5 }}>
                {module.description}
              </p>
            )}
            <div style={{ display: 'flex', gap: 8 }}>
              <StatPill label="Test Sets" value={module.testSetCount} />
              <StatPill label="Test Cases" value={module.totalTestCases} />
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => setShowCreateTestSet(true)} style={btnStyle('#2563eb')}>
              + Test Set
            </button>
            <button
              onClick={() => setShowRunObjective(true)}
              disabled={!testSets || testSets.length === 0}
              style={{
                ...btnStyle('#16a34a'),
                opacity: !testSets || testSets.length === 0 ? 0.5 : 1,
                cursor: !testSets || testSets.length === 0 ? 'not-allowed' : 'pointer',
              }}
            >
              Run Objective
            </button>
          </div>
        </div>
      </div>

      {/* Test Sets Grid */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>Test Sets</h2>
        <span style={{
          fontSize: 12, fontWeight: 600, color: '#64748b', background: '#f1f5f9',
          padding: '2px 10px', borderRadius: 12,
        }}>{testSets?.length || 0}</span>
      </div>

      {!testSets || testSets.length === 0 ? (
        <div style={{
          background: '#fff', borderRadius: 10, padding: 48, textAlign: 'center',
          border: '1px solid #e2e8f0',
        }}>
          <div style={{ fontSize: 40, marginBottom: 16 }}>{'\u{1F9EA}'}</div>
          <p style={{ color: '#475569', fontSize: 16, margin: '0 0 8px', fontWeight: 500 }}>
            No test sets in this module
          </p>
          <p style={{ color: '#94a3b8', fontSize: 14, margin: '0 0 20px' }}>
            Create a test set, then run objectives against it.
          </p>
          <button onClick={() => setShowCreateTestSet(true)} style={btnStyle('#2563eb')}>
            + Create Test Set
          </button>
        </div>
      ) : (
        <div style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
          gap: 20,
        }}>
          {testSets.map(ts => <TestSetCard key={ts.id} ts={ts} moduleId={moduleId!} />)}
        </div>
      )}

      <CreateTestSetDialog
        open={showCreateTestSet}
        moduleId={moduleId!}
        onClose={() => setShowCreateTestSet(false)}
        onCreated={() => refetch()}
      />

      {testSets && testSets.length > 0 && (
        <RunObjectiveDialog
          open={showRunObjective}
          moduleId={moduleId!}
          testSets={testSets}
          onClose={() => setShowRunObjective(false)}
        />
      )}
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

function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return { background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0', padding: 24, ...extra };
}

const btnStyle = (bg: string): React.CSSProperties => ({
  background: bg, color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
});

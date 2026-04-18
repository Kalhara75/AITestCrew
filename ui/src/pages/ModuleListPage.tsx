import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchModules } from '../api/modules';
import { CreateModuleDialog } from '../components/CreateModuleDialog';
import { AgentsPanel } from '../components/AgentsPanel';
import { QueueBanner } from '../components/QueueBanner';
import { useActiveRun } from '../contexts/ActiveRunContext';

export function ModuleListPage() {
  const [showCreate, setShowCreate] = useState(false);
  const { moduleRun, isModuleRunning } = useActiveRun();

  const { data: modules, isLoading, error, refetch } = useQuery({
    queryKey: ['modules'],
    queryFn: fetchModules,
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading modules...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;

  return (
    <div>
      <QueueBanner />
      <AgentsPanel />
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 }}>
        <h1 style={{ margin: 0, fontSize: 24, fontWeight: 700, color: '#0f172a' }}>Modules</h1>
        <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
          <span style={{
            fontSize: 13, color: '#64748b', background: '#f1f5f9',
            padding: '4px 14px', borderRadius: 20, fontWeight: 500,
          }}>
            {modules?.length || 0} module{(modules?.length || 0) !== 1 ? 's' : ''}
          </span>
          <button onClick={() => setShowCreate(true)} style={createBtnStyle}>
            + Create Module
          </button>
        </div>
      </div>

      {!modules || modules.length === 0 ? (
        <div style={{
          background: '#fff', borderRadius: 10, padding: 48, textAlign: 'center',
          border: '1px solid #e2e8f0',
        }}>
          <div style={{ fontSize: 40, marginBottom: 16 }}>{'\u{1F4E6}'}</div>
          <p style={{ color: '#475569', fontSize: 16, margin: '0 0 8px', fontWeight: 500 }}>
            No modules yet
          </p>
          <p style={{ color: '#94a3b8', fontSize: 14, margin: '0 0 20px' }}>
            Create a module to organise your test sets.
          </p>
          <button onClick={() => setShowCreate(true)} style={createBtnStyle}>
            + Create Module
          </button>
        </div>
      ) : (
        <div style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
          gap: 20,
        }}>
          {modules.map(m => {
            const running = isModuleRunning(m.id);
            return (
              <Link key={m.id} to={`/modules/${m.id}`} style={{ textDecoration: 'none', color: 'inherit', display: 'flex', flexDirection: 'column' }}>
                <div style={{
                  ...cardStyle,
                  ...(running ? { borderLeft: '3px solid #2563eb', borderColor: '#bfdbfe' } : {}),
                  overflow: 'hidden',
                  position: 'relative' as const,
                }}
                  onMouseEnter={e => {
                    e.currentTarget.style.boxShadow = '0 4px 16px rgba(0,0,0,0.1)';
                    if (!running) e.currentTarget.style.borderColor = '#cbd5e1';
                  }}
                  onMouseLeave={e => {
                    e.currentTarget.style.boxShadow = '0 1px 3px rgba(0,0,0,0.06)';
                    if (!running) e.currentTarget.style.borderColor = '#e2e8f0';
                  }}
                >
                  {/* Animated progress bar at top when running */}
                  {running && moduleRun && (
                    <>
                      <div style={{
                        position: 'absolute',
                        top: 0,
                        left: 0,
                        right: 0,
                        height: 3,
                        background: '#e2e8f0',
                      }}>
                        <div style={{
                          height: '100%',
                          width: `${moduleRun.totalCount > 0 ? (moduleRun.completedCount / moduleRun.totalCount) * 100 : 0}%`,
                          background: '#2563eb',
                          transition: 'width 0.5s ease',
                          borderRadius: '0 2px 2px 0',
                        }} />
                      </div>
                      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
                    </>
                  )}

                  <h3 style={{ margin: '0 0 8px', fontSize: 17, fontWeight: 700, color: '#0f172a' }}>
                    {m.name}
                  </h3>
                  {m.description && (
                    <p style={{ margin: '0 0 16px', fontSize: 13, color: '#64748b', lineHeight: 1.5 }}>
                      {m.description}
                    </p>
                  )}
                  <div style={{ display: 'flex', gap: 8, fontSize: 13, color: '#64748b' }}>
                    <span style={statPill}>{m.testSetCount} test set{m.testSetCount !== 1 ? 's' : ''}</span>
                    <span style={statPill}>{m.totalObjectives} objective{m.totalObjectives !== 1 ? 's' : ''}</span>
                  </div>

                  {/* Footer: show progress when running, otherwise show created date */}
                  <div style={{
                    fontSize: 12, color: running ? '#1e40af' : '#94a3b8',
                    borderTop: '1px solid #f1f5f9',
                    paddingTop: 12, marginTop: 16,
                    display: 'flex', alignItems: 'center', gap: 8,
                  }}>
                    {running && moduleRun ? (
                      <>
                        <div style={{
                          width: 12, height: 12,
                          border: '2px solid #bfdbfe',
                          borderTop: '2px solid #2563eb',
                          borderRadius: '50%',
                          animation: 'spin 0.8s linear infinite',
                          flexShrink: 0,
                        }} />
                        <span style={{ fontWeight: 500 }}>
                          Running {moduleRun.completedCount}/{moduleRun.totalCount} test sets...
                        </span>
                      </>
                    ) : (
                      <span>Created: {new Date(m.createdAt).toLocaleDateString()}</span>
                    )}
                  </div>
                </div>
              </Link>
            );
          })}
        </div>
      )}

      <CreateModuleDialog
        open={showCreate}
        onClose={() => setShowCreate(false)}
        onCreated={() => refetch()}
      />
    </div>
  );
}

const createBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

const cardStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 10, padding: 24,
  boxShadow: '0 1px 3px rgba(0,0,0,0.06)', border: '1px solid #e2e8f0',
  cursor: 'pointer', transition: 'box-shadow 0.15s, border-color 0.15s',
  flex: 1, display: 'flex', flexDirection: 'column',
};

const statPill: React.CSSProperties = {
  background: '#f8fafc', padding: '2px 10px', borderRadius: 6,
  fontSize: 12, fontWeight: 500, border: '1px solid #f1f5f9',
};

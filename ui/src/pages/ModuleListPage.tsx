import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchModules } from '../api/modules';
import { CreateModuleDialog } from '../components/CreateModuleDialog';

export function ModuleListPage() {
  const [showCreate, setShowCreate] = useState(false);

  const { data: modules, isLoading, error, refetch } = useQuery({
    queryKey: ['modules'],
    queryFn: fetchModules,
  });

  if (isLoading) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading modules...</p>;
  if (error) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(error as Error).message}</p>;

  return (
    <div>
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
          {modules.map(m => (
            <Link key={m.id} to={`/modules/${m.id}`} style={{ textDecoration: 'none', color: 'inherit' }}>
              <div style={cardStyle}
                onMouseEnter={e => {
                  e.currentTarget.style.boxShadow = '0 4px 16px rgba(0,0,0,0.1)';
                  e.currentTarget.style.borderColor = '#cbd5e1';
                }}
                onMouseLeave={e => {
                  e.currentTarget.style.boxShadow = '0 1px 3px rgba(0,0,0,0.06)';
                  e.currentTarget.style.borderColor = '#e2e8f0';
                }}
              >
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
                  <span style={statPill}>{m.totalTestCases} case{m.totalTestCases !== 1 ? 's' : ''}</span>
                </div>
                <div style={{
                  fontSize: 12, color: '#94a3b8', borderTop: '1px solid #f1f5f9',
                  paddingTop: 12, marginTop: 16,
                }}>
                  Created: {new Date(m.createdAt).toLocaleDateString()}
                </div>
              </div>
            </Link>
          ))}
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
  height: '100%', display: 'flex', flexDirection: 'column',
};

const statPill: React.CSSProperties = {
  background: '#f8fafc', padding: '2px 10px', borderRadius: 6,
  fontSize: 12, fontWeight: 500, border: '1px solid #f1f5f9',
};

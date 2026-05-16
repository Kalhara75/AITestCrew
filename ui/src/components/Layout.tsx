import { Link, Outlet, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../contexts/AuthContext';
import { useChat } from '../contexts/ChatContext';
import { ChatDrawer } from './chat/ChatDrawer';
import { fetchAgents } from '../api/agents';
import { fetchBackupStatus } from '../api/backupApi';
import type { BackupStatus } from '../api/backupApi';
import { fetchDataPackReport } from '../api/dataPacks';
import type { AgentSummary, DataPackStartupReport } from '../types';

// System health dot tone helpers
type DotTone = 'green' | 'amber' | 'red';

function agentsTone(agents: AgentSummary[] | undefined): DotTone {
  if (!agents || agents.length === 0) return 'amber';
  if (agents.some(x => x.status === 'Online' || x.status === 'Busy')) return 'green';
  return 'amber';
}

function backupTone(s: BackupStatus | undefined): DotTone {
  if (!s || !s.enabled) return 'amber';
  const now = Date.now();
  const amberMs = 90 * 60 * 1000;
  const redMs = 2 * 30 * 60 * 1000;
  const lastErrMs = s.lastErrorAt ? now - new Date(s.lastErrorAt).getTime() : null;
  const lastOkMs = s.lastSuccessAt ? now - new Date(s.lastSuccessAt).getTime() : null;
  if (
    lastErrMs !== null &&
    lastErrMs < 60 * 60 * 1000 &&
    (lastOkMs === null || s.lastErrorAt! > (s.lastSuccessAt ?? ''))
  ) return 'red';
  if (lastOkMs === null) return 'amber';
  if (lastOkMs < amberMs) return 'green';
  if (lastOkMs < redMs) return 'amber';
  return 'red';
}

function dataPacksTone(r: DataPackStartupReport | null | undefined): DotTone {
  if (!r) return 'amber';
  const totalFailures = r.envs.reduce((acc, e) => acc + e.failures, 0);
  if (totalFailures > 0) return 'red';
  const ranCount = r.envs.filter(e => e.status === 'Ran').length;
  if (ranCount === 0) return 'amber';
  return 'green';
}

export function Layout() {
  const location = useLocation();
  const isHome = location.pathname === '/';
  const isSystem = location.pathname === '/system';
  const { user, authRequired, logout } = useAuth();
  const { isOpen: chatOpen, toggle: toggleChat } = useChat();

  const { data: agents } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 5000,
    retry: false,
  });
  const { data: backupStatus } = useQuery({
    queryKey: ['backupStatus'],
    queryFn: fetchBackupStatus,
    refetchInterval: 60_000,
    retry: false,
  });
  const { data: dataPackReport } = useQuery({
    queryKey: ['dataPackReport'],
    queryFn: fetchDataPackReport,
    refetchInterval: 30_000,
    retry: false,
  });

  const tones: DotTone[] = [agentsTone(agents), backupTone(backupStatus), dataPacksTone(dataPackReport)];
  const worstTone: DotTone = tones.includes('red') ? 'red' : tones.includes('amber') ? 'amber' : 'green';
  const dotColour = worstTone === 'red' ? '#ef4444' : worstTone === 'amber' ? '#f59e0b' : '#10b981';
  const dotTooltip = `Agents: ${agentsTone(agents)} · Backup: ${backupTone(backupStatus)} · Data Packs: ${dataPacksTone(dataPackReport)}`;

  return (
    <div style={{ minHeight: '100vh', background: '#f8fafc' }}>
      <header style={{
        background: 'linear-gradient(135deg, #0f172a 0%, #1e293b 100%)',
        color: '#fff',
        padding: '0 32px',
        height: 56,
        display: 'flex',
        alignItems: 'center',
        gap: 32,
        boxShadow: '0 1px 3px rgba(0,0,0,0.2)',
      }}>
        <Link to="/" style={{
          color: '#fff',
          textDecoration: 'none',
          fontSize: 18,
          fontWeight: 700,
          display: 'flex',
          alignItems: 'center',
          gap: 10,
        }}>
          <span style={{
            background: 'linear-gradient(135deg, #38bdf8, #818cf8)',
            borderRadius: 6,
            width: 28,
            height: 28,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: 14,
            fontWeight: 800,
          }}>
            AT
          </span>
          AI Test Crew
        </Link>
        <nav style={{ display: 'flex', gap: 8 }}>
          <NavLink to="/" label="Modules" active={isHome} />
          <Link
            to="/system"
            style={{
              color: isSystem ? '#fff' : '#94a3b8',
              textDecoration: 'none',
              fontSize: 14,
              fontWeight: 500,
              padding: '6px 14px',
              borderRadius: 6,
              background: isSystem ? 'rgba(255,255,255,0.1)' : 'transparent',
              transition: 'background 0.15s, color 0.15s',
              display: 'flex',
              alignItems: 'center',
              gap: 6,
            }}
          >
            System
            <span
              title={dotTooltip}
              style={{
                display: 'inline-block',
                width: 8,
                height: 8,
                borderRadius: '50%',
                background: dotColour,
                flexShrink: 0,
              }}
            />
          </Link>
        </nav>
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 12, fontSize: 13 }}>
          <button
            onClick={toggleChat}
            title="Assistant"
            style={{
              background: chatOpen ? 'rgba(56,189,248,0.2)' : 'rgba(255,255,255,0.1)',
              border: '1px solid rgba(255,255,255,0.2)',
              color: chatOpen ? '#38bdf8' : '#e2e8f0',
              padding: '4px 12px',
              borderRadius: 4,
              cursor: 'pointer',
              fontSize: 12,
              fontWeight: 500,
            }}
          >
            Assistant
          </button>
          {authRequired && user && (
            <>
              <span style={{ color: '#94a3b8' }}>{user.name}</span>
              <button
                onClick={logout}
                style={{
                  background: 'rgba(255,255,255,0.1)',
                  border: '1px solid rgba(255,255,255,0.2)',
                  color: '#94a3b8',
                  padding: '4px 12px',
                  borderRadius: 4,
                  cursor: 'pointer',
                  fontSize: 12,
                }}
              >
                Logout
              </button>
            </>
          )}
        </div>
      </header>
      <main style={{ maxWidth: 1200, margin: '0 auto', padding: '28px 24px' }}>
        <Outlet />
      </main>
      <ChatDrawer />
    </div>
  );
}

function NavLink({ to, label, active }: { to: string; label: string; active: boolean }) {
  return (
    <Link to={to} style={{
      color: active ? '#fff' : '#94a3b8',
      textDecoration: 'none',
      fontSize: 14,
      fontWeight: 500,
      padding: '6px 14px',
      borderRadius: 6,
      background: active ? 'rgba(255,255,255,0.1)' : 'transparent',
      transition: 'background 0.15s, color 0.15s',
    }}>
      {label}
    </Link>
  );
}

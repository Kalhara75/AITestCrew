import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchAgents, forceQuitAgent } from '../api/agents';
import type { AgentSummary } from '../types';

const STATUS_COLOURS: Record<string, { bg: string; fg: string; dot: string }> = {
  Online:  { bg: '#ecfdf5', fg: '#065f46', dot: '#10b981' },
  Busy:    { bg: '#fef3c7', fg: '#92400e', dot: '#f59e0b' },
  Offline: { bg: '#f1f5f9', fg: '#475569', dot: '#94a3b8' },
};

function formatAge(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

export function AgentsPanel() {
  const { data: agents, error } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 5000,
    retry: false,
  });

  // If the endpoint isn't available (file-based storage mode), render nothing.
  if (error) return null;
  if (!agents || agents.length === 0) {
    return (
      <div style={panelStyle}>
        <div style={headerStyle}>
          <h3 style={headingStyle}>Agents</h3>
        </div>
        <p style={{ margin: 0, color: '#94a3b8', fontSize: 13 }}>
          No agents connected. Start a local Runner agent to execute web / desktop tests:
          <br />
          <code style={codeStyle}>dotnet run --project src/AiTestCrew.Runner -- --agent --name "MyPC"</code>
        </p>
      </div>
    );
  }

  return (
    <div style={panelStyle}>
      <div style={headerStyle}>
        <h3 style={headingStyle}>Agents</h3>
        <span style={countPill}>{agents.length} connected</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {agents.map(a => <AgentRow key={a.id} agent={a} />)}
      </div>
    </div>
  );
}

function AgentRow({ agent }: { agent: AgentSummary }) {
  const colour = STATUS_COLOURS[agent.status] ?? STATUS_COLOURS.Offline;
  const queryClient = useQueryClient();
  const [forcing, setForcing] = useState(false);
  const [forceError, setForceError] = useState<string | null>(null);
  const canForceQuit = agent.status === 'Online' || agent.status === 'Busy';

  async function handleForceQuit() {
    const confirmMsg = agent.currentJob
      ? `Force-quit ${agent.name}? Its current job (${agent.currentJob.testSetId}) will be marked failed and the agent process terminated.`
      : `Force-quit ${agent.name}? The agent process will terminate on its next heartbeat (within ~30s).`;
    if (!window.confirm(confirmMsg)) return;
    setForcing(true);
    setForceError(null);
    try {
      await forceQuitAgent(agent.id);
      // Refresh immediately so the row reflects any cancelled job + Offline drop.
      await queryClient.invalidateQueries({ queryKey: ['agents'] });
      await queryClient.invalidateQueries({ queryKey: ['queue'] });
    } catch (err) {
      setForceError(err instanceof Error ? err.message : 'Force-quit failed.');
    } finally {
      setForcing(false);
    }
  }

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 12,
      padding: '10px 12px', borderRadius: 8,
      background: '#fff', border: '1px solid #e2e8f0',
    }}>
      <div style={{
        width: 10, height: 10, borderRadius: '50%',
        background: colour.dot, flexShrink: 0,
      }} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontWeight: 600, color: '#0f172a', fontSize: 14 }}>{agent.name}</span>
          {agent.ownerName && (
            <span style={{ fontSize: 12, color: '#64748b' }}>({agent.ownerName})</span>
          )}
          <span style={{
            fontSize: 11, fontWeight: 600,
            background: colour.bg, color: colour.fg,
            padding: '2px 8px', borderRadius: 10,
          }}>
            {agent.status}
          </span>
        </div>
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 4 }}>
          {agent.capabilities.map(c => (
            <span key={c} style={capPill}>{c}</span>
          ))}
        </div>
        {forceError && (
          <div style={{ fontSize: 12, color: '#991b1b', marginTop: 4 }}>{forceError}</div>
        )}
      </div>
      <div style={{ textAlign: 'right', fontSize: 12, color: '#94a3b8' }}>
        {agent.currentJob ? (
          <>
            <div style={{ color: '#f59e0b', fontWeight: 600 }}>
              Running {agent.currentJob.testSetId}
            </div>
            <div>{agent.currentJob.targetType}</div>
          </>
        ) : (
          <div>Seen {formatAge(agent.lastSeenAt)}</div>
        )}
        {canForceQuit && (
          <button
            onClick={handleForceQuit}
            disabled={forcing}
            style={{
              ...forceBtnStyle,
              opacity: forcing ? 0.6 : 1,
              cursor: forcing ? 'wait' : 'pointer',
            }}
            title="Terminate the agent process (use when a recording is stuck)"
          >
            {forcing ? 'Terminating…' : 'Force quit'}
          </button>
        )}
      </div>
    </div>
  );
}

const panelStyle: React.CSSProperties = {
  background: '#f8fafc', borderRadius: 10, padding: 16,
  border: '1px solid #e2e8f0', marginBottom: 20,
};

const headerStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12,
};

const headingStyle: React.CSSProperties = {
  margin: 0, fontSize: 14, fontWeight: 700, color: '#0f172a', textTransform: 'uppercase', letterSpacing: 0.5,
};

const countPill: React.CSSProperties = {
  fontSize: 12, color: '#475569', background: '#fff',
  padding: '2px 10px', borderRadius: 10, border: '1px solid #e2e8f0', fontWeight: 500,
};

const capPill: React.CSSProperties = {
  fontSize: 11, color: '#334155', background: '#f1f5f9',
  padding: '2px 8px', borderRadius: 6, fontWeight: 500,
};

const forceBtnStyle: React.CSSProperties = {
  marginTop: 6,
  background: '#fef2f2', color: '#991b1b',
  border: '1px solid #fecaca', borderRadius: 6,
  padding: '3px 10px', fontSize: 11, fontWeight: 600,
};

const codeStyle: React.CSSProperties = {
  display: 'inline-block', marginTop: 8,
  fontFamily: 'ui-monospace,SFMono-Regular,Menlo,monospace', fontSize: 12,
  background: '#fff', padding: '6px 10px', borderRadius: 6, border: '1px solid #e2e8f0',
};

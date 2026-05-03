import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchActiveAuthRefreshes, startAuthRefresh, cancelAuthRefresh } from '../api/authRefreshes';
import { fetchAgents } from '../api/agents';
import { fetchQueue } from '../api/queue';
import type { AuthRefreshRequest, AgentSummary, QueueEntry } from '../types';

const surfaceLabel: Record<string, string> = {
  Api: 'API',
  WebBlazor: 'Brave Cloud (Blazor)',
  WebMvc: 'Bravo Web (MVC)',
};

/**
 * Dashboard banner for outstanding auth-refresh requests. One row per active
 * scope; clicking "Refresh auth" enqueues the AuthSetup job. Multiple paused
 * runs share one row (server-side dedup), so a single click unblocks them all.
 */
export function AuthRefreshBanner() {
  const qc = useQueryClient();
  const { data: refreshes, error } = useQuery({
    queryKey: ['authRefreshes', 'active'],
    queryFn: fetchActiveAuthRefreshes,
    refetchInterval: 4000,
    retry: false,
  });
  const { data: agents } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 5000,
    retry: false,
  });
  const { data: queue } = useQuery({
    queryKey: ['queue'],
    queryFn: fetchQueue,
    refetchInterval: 4000,
    retry: false,
  });

  if (error) return null;
  const active = (refreshes ?? []).filter(r => r.status === 'Pending' || r.status === 'InProgress');
  if (active.length === 0) return null;

  return (
    <div style={bannerStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
        <span style={{ fontSize: 16 }}>🔒</span>
        <span style={{ fontSize: 14, fontWeight: 700, color: '#92400e' }}>
          Authentication needed
        </span>
        <span style={countPill}>{active.length}</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {active.map(r => (
          <RefreshRow
            key={r.id}
            refresh={r}
            agents={agents ?? []}
            queue={queue ?? []}
            onStart={async () => {
              try { await startAuthRefresh(r.id); }
              finally {
                qc.invalidateQueries({ queryKey: ['authRefreshes', 'active'] });
                qc.invalidateQueries({ queryKey: ['queue'] });
              }
            }}
            onCancel={async () => {
              try { await cancelAuthRefresh(r.id); }
              finally {
                qc.invalidateQueries({ queryKey: ['authRefreshes', 'active'] });
                qc.invalidateQueries({ queryKey: ['queue'] });
              }
            }}
          />
        ))}
      </div>
    </div>
  );
}

function RefreshRow({
  refresh, agents, queue, onStart, onCancel,
}: {
  refresh: AuthRefreshRequest;
  agents: AgentSummary[];
  queue: QueueEntry[];
  onStart: () => Promise<void>;
  onCancel: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const surface = surfaceLabel[refresh.surface] ?? refresh.surface;

  // Count parked queue entries for this refresh — surfaces "1 run paused" /
  // "5 runs paused" so the user understands the blast radius of a single click.
  const parkedCount = queue.filter(q => q.authRefreshId === refresh.id).length;

  // Find the in-flight AuthSetup job (if any) so we can show "Refreshing on Alice-PC...".
  const inflightAuth = queue.find(q =>
    q.testSetId === refresh.id
    && (q.status === 'Claimed' || q.status === 'Running')
  );
  const targetAgent = inflightAuth?.claimedBy
    ? agents.find(a => a.id === inflightAuth.claimedBy)
    : null;

  const click = async (fn: () => Promise<void>) => {
    setBusy(true);
    try { await fn(); } finally { setBusy(false); }
  };

  return (
    <div style={rowStyle}>
      <div style={{ flex: 1, fontSize: 13, color: '#78350f' }}>
        <b>{surface}</b>
        <span style={{ color: '#a16207' }}>
          {' '}@ <code style={codeStyle}>{refresh.environmentKey}</code>
          {refresh.apiStackKey && <> · stack <code style={codeStyle}>{refresh.apiStackKey}</code></>}
        </span>
        {parkedCount > 0 && (
          <span style={{ marginLeft: 12, color: '#92400e' }}>
            {parkedCount} run{parkedCount === 1 ? '' : 's'} paused
          </span>
        )}
        {refresh.status === 'InProgress' && (
          <div style={{ fontSize: 12, color: '#a16207', marginTop: 2 }}>
            {targetAgent
              ? <>Refreshing on <b>{targetAgent.name}</b>…</>
              : <>Refresh in progress…</>}
          </div>
        )}
      </div>
      {refresh.status === 'Pending' && (
        <button
          onClick={() => click(onStart)}
          disabled={busy}
          style={primaryBtn}
        >
          {busy ? 'Starting…' : 'Refresh auth'}
        </button>
      )}
      <button onClick={() => click(onCancel)} disabled={busy} style={cancelBtn}>
        Dismiss
      </button>
    </div>
  );
}

const bannerStyle: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 10,
  padding: 14, marginBottom: 20,
};

const rowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 12,
  padding: '8px 12px', background: '#fff',
  border: '1px solid #fde68a', borderRadius: 8,
};

const countPill: React.CSSProperties = {
  fontSize: 11, color: '#92400e', background: '#fef3c7',
  padding: '2px 8px', borderRadius: 10, fontWeight: 600,
};

const primaryBtn: React.CSSProperties = {
  background: '#92400e', color: '#fff', border: '1px solid #92400e',
  padding: '4px 14px', borderRadius: 6, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

const cancelBtn: React.CSSProperties = {
  background: 'transparent', border: '1px solid #fed7aa', color: '#92400e',
  padding: '3px 10px', borderRadius: 6, fontSize: 12, fontWeight: 600, cursor: 'pointer',
};

const codeStyle: React.CSSProperties = {
  fontFamily: 'ui-monospace,monospace', fontSize: 11,
  background: '#fef3c7', padding: '1px 6px', borderRadius: 4,
};

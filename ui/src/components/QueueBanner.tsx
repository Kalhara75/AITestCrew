import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchQueue, cancelQueuedJob } from '../api/queue';
import { fetchAgents } from '../api/agents';
import type { QueueEntry, AgentSummary } from '../types';

/**
 * Shows a banner listing queued / claimed / running jobs that need an agent.
 * Hidden when there's nothing in the queue.
 */
export function QueueBanner() {
  const qc = useQueryClient();
  const { data: queue, error } = useQuery({
    queryKey: ['queue'],
    queryFn: fetchQueue,
    refetchInterval: 3000,
    retry: false,
  });
  const { data: agents } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 5000,
    retry: false,
  });

  if (error) return null;
  const pending = (queue ?? []).filter(q =>
    q.status === 'Queued' || q.status === 'Claimed' || q.status === 'Running'
  );
  if (pending.length === 0) return null;

  return (
    <div style={bannerStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
        <span style={{ fontSize: 14, fontWeight: 700, color: '#92400e' }}>Agent queue</span>
        <span style={countPill}>{pending.length} active</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {pending.map(job => (
          <QueueRow
            key={job.id}
            job={job}
            agents={agents ?? []}
            onCancel={async () => {
              try { await cancelQueuedJob(job.id); }
              finally { qc.invalidateQueries({ queryKey: ['queue'] }); }
            }}
          />
        ))}
      </div>
    </div>
  );
}

function QueueRow({ job, agents, onCancel }: { job: QueueEntry; agents: AgentSummary[]; onCancel: () => void }) {
  const agent = job.claimedBy ? agents.find(a => a.id === job.claimedBy) : null;
  const capable = agents.filter(a => a.capabilities.includes(job.targetType) && a.status === 'Online');
  const hasCapableAgent = capable.length > 0;

  let message: React.ReactNode;
  if (job.status === 'Queued') {
    message = hasCapableAgent
      ? <>Queued — waiting for agent with <b>{job.targetType}</b></>
      : <>
          <span style={{ color: '#b91c1c' }}>No online agent</span> with <b>{job.targetType}</b> capability.
          Start one: <code style={codeStyle}>dotnet run --project src/AiTestCrew.Runner -- --agent --name "MyPC"</code>
        </>;
  } else if (job.status === 'Claimed' || job.status === 'Running') {
    message = <>Running on <b>{agent?.name ?? job.claimedBy}</b> ({job.targetType})</>;
  } else {
    message = <>{job.status}</>;
  }

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 12,
      padding: '8px 12px', background: '#fff',
      border: '1px solid #fde68a', borderRadius: 8,
    }}>
      <span style={{ fontSize: 12, fontFamily: 'ui-monospace,monospace', color: '#78350f' }}>
        {job.testSetId}{job.objectiveId ? ` / ${job.objectiveId}` : ''}
      </span>
      <span style={{ flex: 1, fontSize: 13, color: '#78350f' }}>{message}</span>
      {job.status === 'Queued' && (
        <button onClick={onCancel} style={cancelBtn}>Cancel</button>
      )}
    </div>
  );
}

const bannerStyle: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 10,
  padding: 14, marginBottom: 20,
};

const countPill: React.CSSProperties = {
  fontSize: 11, color: '#92400e', background: '#fef3c7',
  padding: '2px 8px', borderRadius: 10, fontWeight: 600,
};

const cancelBtn: React.CSSProperties = {
  background: 'transparent', border: '1px solid #fecaca', color: '#b91c1c',
  padding: '3px 10px', borderRadius: 6, fontSize: 12, fontWeight: 600, cursor: 'pointer',
};

const codeStyle: React.CSSProperties = {
  fontFamily: 'ui-monospace,monospace', fontSize: 11,
  background: '#fef3c7', padding: '1px 6px', borderRadius: 4,
};

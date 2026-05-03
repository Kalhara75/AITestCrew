import { useQuery } from '@tanstack/react-query';
import { fetchQueue } from '../../../api/queue';
import { tokens } from '../tokens';
import { successCardStyle, errorCardStyle } from '../styles';
import { actionCardStyle } from './shared';

interface Props {
  jobId: string;
  agentName?: string;
  initialMessage: string;
}

export function DispatchedRecordingCard({ jobId, agentName, initialMessage }: Props) {
  const { data: queue } = useQuery({
    queryKey: ['queue'],
    queryFn: fetchQueue,
    refetchInterval: 2500,
    retry: false,
  });
  const job = (queue ?? []).find(j => j.id === jobId);
  const status = job?.status ?? 'Queued';
  const isTerminal = status === 'Completed' || status === 'Failed' || status === 'Cancelled';

  let message: string;
  if (status === 'Queued')         message = `Queued for ${agentName ?? 'agent'} — waiting to be claimed.`;
  else if (status === 'Claimed')   message = `Claimed by ${job?.claimedBy ?? agentName ?? 'agent'} — session is about to start.`;
  else if (status === 'Running')   message = `Running on ${job?.claimedBy ?? agentName ?? 'agent'} — complete the session on that machine.`;
  else if (status === 'Completed') message = 'Session completed. Recorded steps have been saved.';
  else if (status === 'Failed')    message = job?.error ? `Session failed: ${job.error}` : 'Session failed on the agent.';
  else if (status === 'Cancelled') message = 'Cancelled.';
  else                              message = initialMessage;

  const tone =
    status === 'Completed' ? successCardStyle :
    status === 'Failed' || status === 'Cancelled' ? errorCardStyle :
    actionCardStyle(tokens.color.recordTint);

  return (
    <div style={tone}>
      <div style={{ fontWeight: tokens.font.weight.semi, marginBottom: 4 }}>{message}</div>
      <div style={{ fontSize: tokens.font.size.xs, color: tokens.color.textSubtle }}>
        job <code>{jobId}</code>{isTerminal ? '' : ' — this card updates live'}
      </div>
    </div>
  );
}

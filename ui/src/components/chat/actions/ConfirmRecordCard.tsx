import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchAgents } from '../../../api/agents';
import { startRecording } from '../../../api/recordings';
import type { RecordingKind, StartRecordingRequest } from '../../../api/recordings';
import { KeyValueGrid } from '../KeyValueGrid';
import { ActionCardHeader, actionCardStyle } from './shared';
import { DispatchedRecordingCard } from './DispatchedRecordingCard';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle } from '../styles';

type RecordPayload = {
  recordingKind?: RecordingKind;
  target?: 'UI_Web_MVC' | 'UI_Web_Blazor' | 'UI_Desktop_WinForms';
  moduleId?: string;
  testSetId?: string;
  caseName?: string;
  objectiveId?: string;
  verificationName?: string;
  waitBeforeSeconds?: number;
  deliveryStepIndex?: number;
  parentKind?: 'Api' | 'WebUi' | 'DesktopUi' | 'AseXml' | 'AseXmlDeliver';
  parentStepIndex?: number;
  environmentKey?: string;
};

export function ConfirmRecordCard({ summary, data }: { summary?: string; data: unknown }) {
  const payload = (data ?? {}) as RecordPayload;
  const [selectedAgentId, setSelectedAgentId] = useState<string>('');
  const [state, setState] = useState<'idle' | 'sending' | 'dispatched' | 'error'>('idle');
  const [resultMessage, setResultMessage] = useState<string>('');
  const [jobId, setJobId] = useState<string | null>(null);

  const { data: agents } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 5000,
    retry: false,
  });

  const candidates = (agents ?? []).filter(a =>
    (a.status === 'Online' || a.status === 'Busy')
    && payload.target
    && a.capabilities.includes(payload.target));

  useEffect(() => {
    if (!selectedAgentId && candidates.length > 0) setSelectedAgentId(candidates[0].id);
  }, [candidates.length, selectedAgentId, candidates]);

  async function execute() {
    if (!payload.recordingKind || !payload.target) {
      setState('error');
      setResultMessage('Missing recordingKind / target.');
      return;
    }
    if (!selectedAgentId) {
      setState('error');
      setResultMessage('No online agent with matching capability. Start a Runner with --agent first.');
      return;
    }
    setState('sending');
    try {
      const req: StartRecordingRequest = {
        kind: payload.recordingKind,
        target: payload.target,
        agentId: selectedAgentId,
        moduleId: payload.moduleId,
        testSetId: payload.testSetId,
        caseName: payload.caseName,
        objectiveId: payload.objectiveId,
        verificationName: payload.verificationName,
        waitBeforeSeconds: payload.waitBeforeSeconds,
        deliveryStepIndex: payload.deliveryStepIndex,
        parentKind: payload.parentKind,
        parentStepIndex: payload.parentStepIndex,
        environmentKey: payload.environmentKey,
      };
      const res = await startRecording(req);
      setJobId(res.jobId);
      setState('dispatched');
      setResultMessage(`Dispatched ${res.jobKind} to agent. Open the agent machine to complete the session.`);
    } catch (err) {
      setState('error');
      setResultMessage(err instanceof Error ? err.message : 'Dispatch failed.');
    }
  }

  if (state === 'dispatched' && jobId) {
    return (
      <DispatchedRecordingCard
        jobId={jobId}
        initialMessage={resultMessage}
        agentName={candidates.find(a => a.id === selectedAgentId)?.name}
      />
    );
  }

  const rows: [string, string | undefined][] = [
    ['kind',          payload.recordingKind],
    ['target',        payload.target],
    ['module',        payload.moduleId],
    ['test set',      payload.testSetId],
    ['case name',     payload.caseName],
    ['objective',     payload.objectiveId],
    ['verification',  payload.verificationName],
    ['parent kind',   payload.parentKind],
    ['parent step',   payload.parentStepIndex?.toString()],
    ['environment',   payload.environmentKey],
    ['wait (s)',      payload.waitBeforeSeconds?.toString()],
  ];

  return (
    <div style={actionCardStyle(tokens.color.recordTint)}>
      <ActionCardHeader kind="confirmRecord" summary={summary} />
      <KeyValueGrid rows={rows} />
      <div style={{ marginTop: 8, fontSize: tokens.font.size.sm }}>
        <div style={{ color: tokens.color.textSubtle, marginBottom: 4 }}>Dispatch to agent</div>
        {candidates.length === 0 ? (
          <div style={{ color: tokens.color.danger }}>
            No online agent with capability <code>{payload.target}</code>.
            Start one: <code style={{ fontSize: tokens.font.size.xs }}>
              dotnet run --project src/AiTestCrew.Runner -- --agent --name "MyPC"
            </code>
          </div>
        ) : (
          <select
            value={selectedAgentId}
            onChange={e => setSelectedAgentId(e.target.value)}
            style={{
              width: '100%',
              padding: '5px 8px',
              fontSize: tokens.font.size.sm,
              border: `1px solid ${tokens.color.border}`,
              borderRadius: tokens.radius.md,
            }}
          >
            {candidates.map(a => (
              <option key={a.id} value={a.id}>
                {a.name} — {a.status}{a.currentJob ? ' (busy)' : ''}
              </option>
            ))}
          </select>
        )}
      </div>
      {state === 'error' && <div style={errorLineStyle}>{resultMessage}</div>}
      <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
        <button
          onClick={execute}
          disabled={state === 'sending' || candidates.length === 0}
          style={{
            ...primaryBtnStyle,
            opacity: state === 'sending' || candidates.length === 0 ? 0.5 : 1,
            cursor: state === 'sending' || candidates.length === 0 ? 'not-allowed' : 'pointer',
          }}
        >
          {state === 'sending' ? 'Dispatching…' : 'Execute'}
        </button>
      </div>
    </div>
  );
}

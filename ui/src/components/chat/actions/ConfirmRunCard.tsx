import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useChat } from '../../../contexts/ChatContext';
import { useActiveRun } from '../../../contexts/ActiveRunContext';
import { triggerRun } from '../../../api/runs';
import type { TriggerRunRequest } from '../../../types';
import { KeyValueGrid } from '../KeyValueGrid';
import { ActionCardHeader, actionCardStyle } from './shared';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle, successCardStyle, linkBtnStyle } from '../styles';

type RunPayload = Partial<TriggerRunRequest>;

export function ConfirmRunCard({ summary, data }: { summary?: string; data: unknown }) {
  const payload = (data ?? {}) as RunPayload;
  const { setIndividualRun } = useActiveRun();
  const { close } = useChat();
  const navigate = useNavigate();
  const [state, setState] = useState<'idle' | 'sending' | 'done' | 'error'>('idle');
  const [resultMessage, setResultMessage] = useState<string>('');
  const [runId, setRunId] = useState<string | null>(null);

  async function execute() {
    if (!payload.mode || !payload.testSetId) {
      setState('error');
      setResultMessage('Missing required fields (mode / testSetId).');
      return;
    }
    setState('sending');
    try {
      const req: TriggerRunRequest = {
        mode: payload.mode,
        objective: payload.objective,
        moduleId: payload.moduleId,
        testSetId: payload.testSetId,
        objectiveId: payload.objectiveId,
        objectiveName: payload.objectiveName,
        environmentKey: payload.environmentKey,
        apiStackKey: payload.apiStackKey,
        apiModule: payload.apiModule,
        verificationWaitOverride: payload.verificationWaitOverride,
      };
      const res = await triggerRun(req);
      setRunId(res.runId);
      setIndividualRun({
        runId: res.runId,
        testSetId: payload.testSetId,
        moduleId: payload.moduleId,
        objectiveId: payload.objectiveId,
      });
      setState('done');
      setResultMessage(`Run ${res.runId} ${res.status.toLowerCase()}.`);
    } catch (err) {
      setState('error');
      setResultMessage(err instanceof Error ? err.message : 'Run failed to start.');
    }
  }

  if (state === 'done') {
    return (
      <div style={successCardStyle}>
        <div style={{ fontWeight: tokens.font.weight.semi, marginBottom: 4 }}>{resultMessage}</div>
        {payload.moduleId && payload.testSetId && runId && (
          <button
            onClick={() => {
              navigate(`/modules/${payload.moduleId}/testsets/${payload.testSetId}/runs/${runId}`);
              close();
            }}
            style={linkBtnStyle}
          >
            Open run page
          </button>
        )}
      </div>
    );
  }

  return (
    <div style={actionCardStyle(tokens.color.runTint)}>
      <ActionCardHeader kind="confirmRun" summary={summary} />
      <KeyValueGrid rows={[
        ['mode',         payload.mode],
        ['module',       payload.moduleId],
        ['test set',     payload.testSetId],
        ['objective',    payload.objectiveId ?? payload.objectiveName ?? payload.objective],
        ['environment',  payload.environmentKey],
        ['stack',        payload.apiStackKey],
        ['api module',   payload.apiModule],
        ['wait override', payload.verificationWaitOverride?.toString()],
      ]} />
      {state === 'error' && <div style={errorLineStyle}>{resultMessage}</div>}
      <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
        <button
          onClick={execute}
          disabled={state === 'sending'}
          style={{
            ...primaryBtnStyle,
            opacity: state === 'sending' ? 0.6 : 1,
            cursor: state === 'sending' ? 'wait' : 'pointer',
          }}
        >
          {state === 'sending' ? 'Starting…' : 'Execute'}
        </button>
      </div>
    </div>
  );
}

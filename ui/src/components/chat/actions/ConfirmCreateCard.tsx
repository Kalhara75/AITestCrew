import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useChat } from '../../../contexts/ChatContext';
import { createModule, createTestSet } from '../../../api/modules';
import { KeyValueGrid } from '../KeyValueGrid';
import { ActionCardHeader, actionCardStyle } from './shared';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle, successCardStyle } from '../styles';

type CreatePayload = {
  target?: 'module' | 'testSet';
  name?: string;
  description?: string;
  moduleId?: string;
};

export function ConfirmCreateCard({ summary, data }: { summary?: string; data: unknown }) {
  const payload = (data ?? {}) as CreatePayload;
  const { close } = useChat();
  const navigate = useNavigate();
  const [state, setState] = useState<'idle' | 'sending' | 'done' | 'error'>('idle');
  const [resultMessage, setResultMessage] = useState<string>('');

  async function execute() {
    if (!payload.target || !payload.name) {
      setState('error');
      setResultMessage('Missing required fields (target / name).');
      return;
    }
    if (payload.target === 'testSet' && !payload.moduleId) {
      setState('error');
      setResultMessage('moduleId is required when creating a test set.');
      return;
    }
    setState('sending');
    try {
      if (payload.target === 'module') {
        const created = await createModule(payload.name, payload.description);
        setState('done');
        setResultMessage(`Created module '${created.name}'.`);
        navigate(`/modules/${created.id}`);
        close();
      } else {
        const created = await createTestSet(payload.moduleId!, payload.name);
        setState('done');
        setResultMessage(`Created test set '${created.name}'.`);
        navigate(`/modules/${payload.moduleId}/testsets/${created.id}`);
        close();
      }
    } catch (err) {
      setState('error');
      setResultMessage(err instanceof Error ? err.message : 'Create failed.');
    }
  }

  if (state === 'done') {
    return (
      <div style={successCardStyle}>
        <div style={{ fontWeight: tokens.font.weight.semi }}>{resultMessage}</div>
      </div>
    );
  }

  const rows: [string, string | undefined][] = payload.target === 'module'
    ? [['target', 'module'], ['name', payload.name], ['description', payload.description]]
    : [['target', 'test set'], ['module', payload.moduleId], ['name', payload.name]];

  return (
    <div style={actionCardStyle(tokens.color.createTint)}>
      <ActionCardHeader kind="confirmCreate" summary={summary} />
      <KeyValueGrid rows={rows} />
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
          {state === 'sending' ? 'Creating…' : 'Execute'}
        </button>
      </div>
    </div>
  );
}

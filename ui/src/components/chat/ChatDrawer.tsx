import { useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useChat } from '../../contexts/ChatContext';
import { useActiveRun } from '../../contexts/ActiveRunContext';
import type { ChatMessageEntry } from '../../contexts/ChatContext';
import type { ChatAction, ChatRequestContext } from '../../api/chat';
import { useQuery } from '@tanstack/react-query';
import { triggerRun } from '../../api/runs';
import { createModule, createTestSet } from '../../api/modules';
import { fetchAgents } from '../../api/agents';
import { fetchQueue } from '../../api/queue';
import { startRecording } from '../../api/recordings';
import type { RecordingKind, StartRecordingRequest } from '../../api/recordings';
import type { TriggerRunRequest } from '../../types';

const DRAWER_WIDTH = 420;
const HEADER_HEIGHT = 56;

function useCurrentContext(): ChatRequestContext | undefined {
  const { pathname } = useLocation();
  const match = /^\/modules\/([^/]+)(?:\/testsets\/([^/]+))?/.exec(pathname);
  if (!match) return undefined;
  return { moduleId: match[1], testSetId: match[2] };
}

export function ChatDrawer() {
  const { isOpen, close, messages, isSending, error, send, clear } = useChat();
  const [input, setInput] = useState('');
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const context = useCurrentContext();

  useEffect(() => {
    if (listRef.current) listRef.current.scrollTop = listRef.current.scrollHeight;
  }, [messages.length, isSending]);

  useEffect(() => {
    if (isOpen) inputRef.current?.focus();
  }, [isOpen]);

  if (!isOpen) return null;

  async function handleSubmit() {
    const text = input;
    setInput('');
    await send(text, context);
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void handleSubmit();
    }
  }

  return (
    <aside style={{
      position: 'fixed',
      top: HEADER_HEIGHT,
      right: 0,
      bottom: 0,
      width: DRAWER_WIDTH,
      background: '#fff',
      borderLeft: '1px solid #e2e8f0',
      boxShadow: '-2px 0 8px rgba(15, 23, 42, 0.06)',
      display: 'flex',
      flexDirection: 'column',
      zIndex: 50,
    }}>
      <header style={{
        display: 'flex', alignItems: 'center', gap: 10,
        padding: '12px 16px', borderBottom: '1px solid #e2e8f0',
      }}>
        <strong style={{ fontSize: 14, color: '#0f172a', letterSpacing: 0.3 }}>Assistant</strong>
        <span style={{ fontSize: 11, color: '#94a3b8', fontWeight: 500 }}>read-only preview</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
          {messages.length > 0 && (
            <button onClick={clear} style={iconBtnStyle} title="Clear conversation">clear</button>
          )}
          <button onClick={close} style={iconBtnStyle} title="Close">close</button>
        </div>
      </header>

      <div ref={listRef} style={{
        flex: 1, overflowY: 'auto', padding: '16px',
        display: 'flex', flexDirection: 'column', gap: 12,
      }}>
        {messages.length === 0 && <EmptyState />}
        {messages.map(m => <MessageBubble key={m.id} message={m} />)}
        {isSending && <TypingIndicator />}
        {error && (
          <div style={{
            padding: '10px 12px', borderRadius: 8,
            background: '#fef2f2', border: '1px solid #fecaca',
            color: '#991b1b', fontSize: 13,
          }}>
            {error}
          </div>
        )}
      </div>

      <div style={{ borderTop: '1px solid #e2e8f0', padding: 12 }}>
        <textarea
          ref={inputRef}
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Ask about modules, test sets, environments, agents..."
          rows={2}
          style={{
            width: '100%', resize: 'none', border: '1px solid #e2e8f0',
            borderRadius: 8, padding: '8px 10px', fontSize: 13,
            fontFamily: 'inherit', outline: 'none', boxSizing: 'border-box',
          }}
        />
        <div style={{ display: 'flex', alignItems: 'center', marginTop: 6, gap: 8 }}>
          <span style={{ fontSize: 11, color: '#94a3b8' }}>Enter to send · Shift+Enter for newline</span>
          <button
            onClick={handleSubmit}
            disabled={isSending || input.trim().length === 0}
            style={{
              marginLeft: 'auto',
              background: '#0f172a', color: '#fff', border: 'none',
              padding: '6px 14px', borderRadius: 6, fontSize: 13, fontWeight: 500,
              cursor: isSending || input.trim().length === 0 ? 'not-allowed' : 'pointer',
              opacity: isSending || input.trim().length === 0 ? 0.5 : 1,
            }}
          >
            Send
          </button>
        </div>
      </div>
    </aside>
  );
}

function EmptyState() {
  return (
    <div style={{ color: '#64748b', fontSize: 13, lineHeight: 1.5 }}>
      <p style={{ margin: '0 0 10px 0' }}>
        Ask me about your test suite. I can list and navigate; I can't trigger runs or recordings yet.
      </p>
      <p style={{ margin: '0 0 6px 0', fontSize: 12, color: '#94a3b8' }}>Try:</p>
      <ul style={{ margin: 0, paddingLeft: 16, fontSize: 12, color: '#475569' }}>
        <li>list modules</li>
        <li>which environments are configured?</li>
        <li>open the MFN delivery test set</li>
        <li>show connected agents</li>
      </ul>
    </div>
  );
}

function TypingIndicator() {
  return (
    <div style={{ fontSize: 12, color: '#94a3b8', paddingLeft: 4 }}>thinking…</div>
  );
}

function MessageBubble({ message }: { message: ChatMessageEntry }) {
  const isUser = message.role === 'user';
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, alignItems: isUser ? 'flex-end' : 'flex-start' }}>
      <div style={{
        maxWidth: '88%',
        background: isUser ? '#0f172a' : '#f1f5f9',
        color: isUser ? '#fff' : '#0f172a',
        padding: '8px 12px', borderRadius: 10,
        fontSize: 13, lineHeight: 1.5,
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
      }}>
        {message.content || (isUser ? '' : '(empty reply)')}
      </div>
      {!isUser && message.actions && message.actions.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, width: '88%' }}>
          {message.actions.map((a, i) => <ActionCard key={i} action={a} />)}
        </div>
      )}
    </div>
  );
}

function ActionCard({ action }: { action: ChatAction }) {
  if (action.kind === 'navigate' && action.path) return <NavigateCard path={action.path} />;
  if (action.kind === 'showData') return <DataCard title={action.title} data={action.data} />;
  if (action.kind === 'confirmRun') return <ConfirmRunCard summary={action.summary} data={action.data} />;
  if (action.kind === 'confirmCreate') return <ConfirmCreateCard summary={action.summary} data={action.data} />;
  if (action.kind === 'confirmRecord') return <ConfirmRecordCard summary={action.summary} data={action.data} />;
  return null;
}

function NavigateCard({ path }: { path: string }) {
  const navigate = useNavigate();
  const { close } = useChat();
  return (
    <button
      onClick={() => { navigate(path); close(); }}
      style={{
        textAlign: 'left',
        background: '#eff6ff', border: '1px solid #bfdbfe', color: '#1d4ed8',
        padding: '8px 12px', borderRadius: 8, fontSize: 13, cursor: 'pointer',
        fontWeight: 500,
      }}
    >
      Open <code style={{ fontFamily: 'ui-monospace,Menlo,monospace', fontSize: 12 }}>{path}</code>
    </button>
  );
}

function DataCard({ title, data }: { title?: string; data: unknown }) {
  return (
    <div style={{
      background: '#fff', border: '1px solid #e2e8f0',
      borderRadius: 8, padding: 10,
    }}>
      {title && (
        <div style={{
          fontSize: 11, fontWeight: 700, color: '#475569',
          textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: 8,
        }}>{title}</div>
      )}
      <DataView data={data} />
    </div>
  );
}

function DataView({ data }: { data: unknown }) {
  if (data == null) return <div style={{ fontSize: 12, color: '#94a3b8' }}>(no data)</div>;

  if (Array.isArray(data) && data.length === 0)
    return <div style={{ fontSize: 12, color: '#94a3b8' }}>(empty)</div>;

  if (Array.isArray(data) && data.every(v => typeof v === 'string' || typeof v === 'number'))
    return (
      <ul style={{ margin: 0, paddingLeft: 18, fontSize: 13, color: '#0f172a' }}>
        {data.map((v, i) => <li key={i}>{String(v)}</li>)}
      </ul>
    );

  if (Array.isArray(data) && data.every(v => typeof v === 'object' && v !== null)) {
    const keys = Array.from(
      new Set(data.flatMap(obj => Object.keys(obj as Record<string, unknown>)))
    ).slice(0, 6);
    return (
      <div style={{ overflowX: 'auto' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
          <thead>
            <tr>{keys.map(k => (
              <th key={k} style={thStyle}>{k}</th>
            ))}</tr>
          </thead>
          <tbody>
            {data.map((row, i) => (
              <tr key={i}>
                {keys.map(k => (
                  <td key={k} style={tdStyle}>{formatCell((row as Record<string, unknown>)[k])}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  return (
    <pre style={{
      margin: 0, fontFamily: 'ui-monospace,Menlo,monospace',
      fontSize: 12, color: '#0f172a',
      whiteSpace: 'pre-wrap', wordBreak: 'break-word',
    }}>
      {JSON.stringify(data, null, 2)}
    </pre>
  );
}

function formatCell(v: unknown): string {
  if (v == null) return '';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

type RunPayload = Partial<TriggerRunRequest>;

function ConfirmRunCard({ summary, data }: { summary?: string; data: unknown }) {
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
        <div style={{ fontWeight: 600, marginBottom: 4 }}>{resultMessage}</div>
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
    <div style={confirmCardStyle}>
      <div style={confirmHeaderStyle}>Confirm run</div>
      {summary && <div style={{ fontSize: 13, color: '#0f172a', marginBottom: 6 }}>{summary}</div>}
      <KeyValueGrid rows={[
        ['mode', payload.mode],
        ['module', payload.moduleId],
        ['test set', payload.testSetId],
        ['objective', payload.objectiveId ?? payload.objectiveName],
        ['environment', payload.environmentKey],
        ['stack', payload.apiStackKey],
        ['api module', payload.apiModule],
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

type CreatePayload = {
  target?: 'module' | 'testSet';
  name?: string;
  description?: string;
  moduleId?: string;
};

function ConfirmCreateCard({ summary, data }: { summary?: string; data: unknown }) {
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
    return <div style={successCardStyle}><div style={{ fontWeight: 600 }}>{resultMessage}</div></div>;
  }

  const rows: [string, string | undefined][] = payload.target === 'module'
    ? [['target', 'module'], ['name', payload.name], ['description', payload.description]]
    : [['target', 'test set'], ['module', payload.moduleId], ['name', payload.name]];

  return (
    <div style={confirmCardStyle}>
      <div style={confirmHeaderStyle}>Confirm create</div>
      {summary && <div style={{ fontSize: 13, color: '#0f172a', marginBottom: 6 }}>{summary}</div>}
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
  environmentKey?: string;
};

function ConfirmRecordCard({ summary, data }: { summary?: string; data: unknown }) {
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

  // Candidate agents: online + has matching capability
  const candidates = (agents ?? []).filter(a =>
    (a.status === 'Online' || a.status === 'Busy')
    && payload.target
    && a.capabilities.includes(payload.target));

  // Default the picker once candidates arrive
  useEffect(() => {
    if (!selectedAgentId && candidates.length > 0) setSelectedAgentId(candidates[0].id);
  }, [candidates.length, selectedAgentId]);

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
    return <DispatchedRecordingCard jobId={jobId} initialMessage={resultMessage}
      agentName={candidates.find(a => a.id === selectedAgentId)?.name} />;
  }

  const rows: [string, string | undefined][] = [
    ['kind', payload.recordingKind],
    ['target', payload.target],
    ['module', payload.moduleId],
    ['test set', payload.testSetId],
    ['case name', payload.caseName],
    ['objective', payload.objectiveId],
    ['verification', payload.verificationName],
    ['environment', payload.environmentKey],
    ['wait (s)', payload.waitBeforeSeconds?.toString()],
  ];

  return (
    <div style={confirmCardStyle}>
      <div style={confirmHeaderStyle}>Confirm recording</div>
      {summary && <div style={{ fontSize: 13, color: '#0f172a', marginBottom: 6 }}>{summary}</div>}
      <KeyValueGrid rows={rows} />
      <div style={{ marginTop: 8, fontSize: 12 }}>
        <div style={{ color: '#64748b', marginBottom: 4 }}>Dispatch to agent</div>
        {candidates.length === 0 ? (
          <div style={{ color: '#991b1b' }}>
            No online agent with capability <code>{payload.target}</code>.
            Start one: <code style={{ fontSize: 11 }}>dotnet run --project src/AiTestCrew.Runner -- --agent --name "MyPC"</code>
          </div>
        ) : (
          <select
            value={selectedAgentId}
            onChange={e => setSelectedAgentId(e.target.value)}
            style={{
              width: '100%', padding: '5px 8px', fontSize: 12,
              border: '1px solid #e2e8f0', borderRadius: 6,
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

function DispatchedRecordingCard({ jobId, agentName, initialMessage }:
  { jobId: string; agentName?: string; initialMessage: string }) {
  const { data: queue } = useQuery({
    queryKey: ['queue'],
    queryFn: fetchQueue,
    refetchInterval: 2500,
    retry: false,
  });
  const job = (queue ?? []).find(j => j.id === jobId);
  const status = job?.status ?? 'Queued';
  const isTerminal = status === 'Completed' || status === 'Failed' || status === 'Cancelled';
  const tone = status === 'Completed' ? successCardStyle :
               status === 'Failed' || status === 'Cancelled' ? {
                 ...successCardStyle, background: '#fef2f2',
                 borderColor: '#fecaca', color: '#991b1b',
               } : confirmCardStyle;

  let message: string;
  if (status === 'Queued') message = `Queued for ${agentName ?? 'agent'} — waiting to be claimed.`;
  else if (status === 'Claimed') message = `Claimed by ${job?.claimedBy ?? agentName ?? 'agent'} — session is about to start.`;
  else if (status === 'Running') message = `Running on ${job?.claimedBy ?? agentName ?? 'agent'} — complete the session on that machine.`;
  else if (status === 'Completed') message = 'Session completed. Recorded steps have been saved.';
  else if (status === 'Failed') message = job?.error ? `Session failed: ${job.error}` : 'Session failed on the agent.';
  else if (status === 'Cancelled') message = 'Cancelled.';
  else message = initialMessage;

  return (
    <div style={tone}>
      <div style={{ fontWeight: 600, marginBottom: 4 }}>{message}</div>
      <div style={{ fontSize: 11, color: '#64748b' }}>
        job <code>{jobId}</code>{isTerminal ? '' : ' — this card updates live'}
      </div>
    </div>
  );
}

function KeyValueGrid({ rows }: { rows: [string, string | undefined][] }) {
  const filled = rows.filter(([, v]) => v !== undefined && v !== null && v !== '');
  if (filled.length === 0) return null;
  return (
    <div style={{
      display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '3px 10px',
      fontSize: 12, color: '#0f172a', margin: '4px 0 2px',
    }}>
      {filled.map(([k, v]) => (
        <div key={k} style={{ display: 'contents' }}>
          <div style={{ color: '#64748b' }}>{k}</div>
          <div style={{ fontFamily: 'ui-monospace,Menlo,monospace', fontSize: 12, wordBreak: 'break-all' }}>{v}</div>
        </div>
      ))}
    </div>
  );
}

const thStyle: React.CSSProperties = {
  textAlign: 'left', padding: '4px 6px',
  color: '#475569', fontWeight: 600,
  borderBottom: '1px solid #e2e8f0',
};
const tdStyle: React.CSSProperties = {
  padding: '4px 6px', color: '#0f172a',
  borderBottom: '1px solid #f1f5f9',
  verticalAlign: 'top',
};
const iconBtnStyle: React.CSSProperties = {
  background: 'transparent', border: '1px solid #e2e8f0',
  color: '#475569', fontSize: 11, padding: '3px 8px',
  borderRadius: 4, cursor: 'pointer',
};

const confirmCardStyle: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fde68a',
  borderRadius: 8, padding: 10,
};

const confirmHeaderStyle: React.CSSProperties = {
  fontSize: 11, fontWeight: 700, color: '#92400e',
  textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: 6,
};

const successCardStyle: React.CSSProperties = {
  background: '#ecfdf5', border: '1px solid #a7f3d0',
  borderRadius: 8, padding: 10, color: '#065f46', fontSize: 13,
};

const errorLineStyle: React.CSSProperties = {
  marginTop: 6, fontSize: 12, color: '#991b1b',
};

const primaryBtnStyle: React.CSSProperties = {
  background: '#0f172a', color: '#fff', border: 'none',
  padding: '6px 14px', borderRadius: 6, fontSize: 13, fontWeight: 500,
};

const linkBtnStyle: React.CSSProperties = {
  background: 'transparent', border: 'none', color: '#065f46',
  padding: 0, fontSize: 13, fontWeight: 600, cursor: 'pointer',
  textDecoration: 'underline',
};

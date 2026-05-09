import { useState } from 'react';
import { peekServiceBusMessages } from '../../../api/eventAssert';
import type { PeekMessage, PeekResponse } from '../../../api/eventAssert';
import type { ServiceBusEntity } from '../../../types';
import { KeyValueGrid } from '../KeyValueGrid';
import { CodeBlock } from '../CodeBlock';
import { ActionCardHeader, actionCardStyle } from './shared';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle } from '../styles';

type PeekPayload = {
  envKey?: string | null;
  connectionKey?: string;
  entity?: ServiceBusEntity;
  max?: number;
  correlationFilter?: string | null;
};

/**
 * REQ-004 §9a — NL peek-then-author chat card. Calls
 * `POST /api/event-assert/peek` and renders an expandable list of
 * messages currently on the queue / subscription. Read-only — never
 * consumes; the editor's editor dialog uses the same endpoint and same
 * 2 KB body-preview truncation.
 */
export function PeekServiceBusMessagesCard({
  summary, data,
}: {
  summary?: string;
  data: unknown;
}) {
  const payload = (data ?? {}) as PeekPayload;
  const [state, setState] = useState<'idle' | 'fetching' | 'done' | 'error'>('idle');
  const [response, setResponse] = useState<PeekResponse | null>(null);
  const [errorMsg, setErrorMsg] = useState<string>('');

  async function execute() {
    if (!payload.connectionKey || !payload.entity?.name) {
      setState('error');
      setErrorMsg('Missing connectionKey or entity.name.');
      return;
    }
    setState('fetching');
    try {
      const resp = await peekServiceBusMessages({
        envKey: payload.envKey ?? null,
        connectionKey: payload.connectionKey,
        entity: payload.entity,
        max: payload.max ?? 10,
        correlationFilter: payload.correlationFilter ?? null,
      });
      setResponse(resp);
      setState('done');
    } catch (err) {
      setState('error');
      setErrorMsg(err instanceof Error ? err.message : 'Peek failed.');
    }
  }

  const ent = payload.entity;
  const entityLabel = !ent ? '(no entity)'
    : ent.type === 'Topic'
      ? `Topic ${ent.name} / Sub ${ent.subscriptionName ?? '?'}`
      : `Queue ${ent.name}`;

  const rows: [string, string | undefined][] = [
    ['env', payload.envKey ?? '(default)'],
    ['connection', payload.connectionKey],
    ['entity', entityLabel],
    ['max', (payload.max ?? 10).toString()],
  ];
  if (payload.correlationFilter) rows.push(['correlationId', payload.correlationFilter]);

  return (
    <div style={actionCardStyle(tokens.color.dataTint)}>
      <ActionCardHeader kind="peekServiceBusMessages" summary={summary} />
      <KeyValueGrid rows={rows} />
      {state === 'error' && <div style={errorLineStyle}>{errorMsg}</div>}
      {state !== 'done' && (
        <div style={{ marginTop: 8, display: 'flex', justifyContent: 'flex-end' }}>
          <button
            onClick={execute}
            disabled={state === 'fetching'}
            style={{ ...primaryBtnStyle, opacity: state === 'fetching' ? 0.6 : 1 }}
          >
            {state === 'fetching' ? 'Peeking…' : 'Peek messages'}
          </button>
        </div>
      )}
      {state === 'done' && response && (
        <div style={{ marginTop: 8 }}>
          <div style={{
            fontSize: tokens.font.size.sm,
            color: tokens.color.muted,
            marginBottom: 4,
          }}>
            {response.totalPeeked === 0
              ? 'No messages currently on the entity.'
              : response.messages.length === response.totalPeeked
                ? `${response.totalPeeked} message(s) currently on the entity.`
                : `${response.totalPeeked} on the entity (${response.messages.length} after correlation filter).`}
          </div>
          {response.messages.map((m, idx) => (
            <PeekMessageRow key={idx} idx={idx} m={m} />
          ))}
        </div>
      )}
    </div>
  );
}

function PeekMessageRow({ idx, m }: { idx: number; m: PeekMessage }) {
  const [expanded, setExpanded] = useState(false);

  const enqueued = m.enqueuedTimeUtc ? new Date(m.enqueuedTimeUtc).toISOString() : '';
  const summary = `[${idx}] ${m.messageId ?? '(no id)'}${m.correlationId ? ` (corr=${m.correlationId})` : ''}${m.contentType ? ` · ${m.contentType}` : ''}`;

  return (
    <div style={{
      border: `1px solid ${tokens.color.border}`,
      borderRadius: tokens.radius.md,
      padding: 6,
      marginBottom: 4,
      background: tokens.color.bg,
    }}>
      <button
        onClick={() => setExpanded(v => !v)}
        style={{
          background: 'transparent',
          border: 'none',
          padding: 0,
          cursor: 'pointer',
          textAlign: 'left',
          color: tokens.color.text,
          fontSize: tokens.font.size.sm,
          fontFamily: tokens.font.mono,
        }}
      >
        {expanded ? '▾' : '▸'} {summary}
      </button>
      {expanded && (
        <div style={{ marginTop: 6 }}>
          <KeyValueGrid rows={[
            ['enqueued (UTC)', enqueued],
            ['delivery count', m.deliveryCount.toString()],
            ['body length', m.body.length.toString()],
            ['body format', m.body.format],
          ]} />
          {Object.keys(m.applicationProperties).length > 0 && (
            <div style={{ marginTop: 6 }}>
              <div style={{
                fontSize: tokens.font.size.xs,
                color: tokens.color.muted,
                marginBottom: 2,
              }}>
                ApplicationProperties
              </div>
              <KeyValueGrid rows={Object.entries(m.applicationProperties)} />
            </div>
          )}
          {m.body.preview && (
            <div style={{ marginTop: 6 }}>
              <CodeBlock
                language={m.body.format === 'Json' ? 'json' : m.body.format === 'Xml' ? 'xml' : 'text'}
                code={m.body.preview}
              />
            </div>
          )}
        </div>
      )}
    </div>
  );
}

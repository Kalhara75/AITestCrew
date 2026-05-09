import { useState } from 'react';
import { addPostStep } from '../../../api/modules';
import type { PostStepParentKind } from '../../../api/modules';
import type { PostStep } from '../../../types';
import { KeyValueGrid } from '../KeyValueGrid';
import { CodeBlock } from '../CodeBlock';
import { ActionCardHeader, actionCardStyle } from './shared';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle, successCardStyle } from '../styles';
import { summariseEventAssert } from './eventAssertSummary';

type CreatePostStepPayload = {
  moduleId?: string;
  testSetId?: string;
  objectiveId?: string;
  parentKind?: PostStepParentKind;
  parentStepIndex?: number;
  postStep?: PostStep;
};

export function ConfirmCreatePostStepCard({ summary, data }: { summary?: string; data: unknown }) {
  const payload = (data ?? {}) as CreatePostStepPayload;
  const [state, setState] = useState<'idle' | 'sending' | 'done' | 'error'>('idle');
  const [message, setMessage] = useState<string>('');

  async function execute() {
    if (!payload.moduleId || !payload.testSetId || !payload.objectiveId
        || !payload.parentKind || payload.parentStepIndex === undefined
        || !payload.postStep) {
      setState('error');
      setMessage('Missing moduleId / testSetId / objectiveId / parentKind / parentStepIndex / postStep.');
      return;
    }
    setState('sending');
    try {
      await addPostStep(
        payload.moduleId, payload.testSetId, payload.objectiveId,
        payload.parentKind, payload.parentStepIndex,
        payload.postStep);
      setState('done');
      setMessage(`Added '${payload.postStep.description || payload.postStep.target}' to ${payload.parentKind}[${payload.parentStepIndex}].`);
    } catch (err) {
      setState('error');
      setMessage(err instanceof Error ? err.message : 'Create failed.');
    }
  }

  if (state === 'done') {
    return <div style={successCardStyle}>{message}</div>;
  }

  const ps = payload.postStep;
  const rows: [string, string | undefined][] = [
    ['objective', payload.objectiveId],
    ['parent', payload.parentKind !== undefined && payload.parentStepIndex !== undefined
      ? `${payload.parentKind}[${payload.parentStepIndex}]` : undefined],
    ['target', ps?.target],
    ['description', ps?.description],
    ['wait (s)', ps?.waitBeforeSeconds?.toString()],
    ['role', ps?.role],
  ];
  if (ps?.dbCheck) {
    if (ps.dbCheck.expectedRowCount !== undefined && ps.dbCheck.expectedRowCount !== null)
      rows.push(['expect rows', ps.dbCheck.expectedRowCount.toString()]);
    const assertions = ps.dbCheck.columnAssertions ?? [];
    if (assertions.length > 0) {
      rows.push([
        'assertions',
        assertions.map(a => {
          const col = a.jsonPath ? `${a.column}.${a.jsonPath}` : a.column;
          if (a.operator === 'IsNull' || a.operator === 'IsNotNull') return `${col} ${a.operator}`;
          if (a.operator === 'Between') return `${col} ${a.operator} '${a.expected}' … '${a.expected2 ?? ''}'`;
          return `${col} ${a.operator} '${a.expected}'`;
        }).join('; '),
      ]);
    }
    const captures = ps.dbCheck.captures ?? [];
    if (captures.length > 0) {
      rows.push([
        'captures',
        captures.map(c => {
          const src = c.jsonPath ? `${c.column}.${c.jsonPath}` : c.column;
          return `{{${c.as}}} ← ${src}${c.required ? '' : ' (optional)'}`;
        }).join('; '),
      ]);
    }
    // Defensive — legacy chat-action card shape (in-flight conversations).
    if (ps.dbCheck.expectedColumnValues
        && Object.keys(ps.dbCheck.expectedColumnValues).length > 0
        && assertions.length === 0) {
      rows.push(['expect cols (legacy)', JSON.stringify(ps.dbCheck.expectedColumnValues)]);
    }
    rows.push(['connection', ps.dbCheck.connectionKey]);
  }
  if (ps?.eventAssert) {
    summariseEventAssert(ps.eventAssert).forEach(r => rows.push(r));
  }

  return (
    <div style={actionCardStyle(tokens.color.postTint)}>
      <ActionCardHeader kind="confirmCreatePostStep" summary={summary} />
      <KeyValueGrid rows={rows} />
      {ps?.dbCheck?.sql && (
        <CodeBlock language="sql" code={ps.dbCheck.sql} />
      )}
      {state === 'error' && <div style={errorLineStyle}>{message}</div>}
      <div style={{ marginTop: 8, display: 'flex', justifyContent: 'flex-end' }}>
        <button
          onClick={execute}
          disabled={state === 'sending'}
          style={{ ...primaryBtnStyle, opacity: state === 'sending' ? 0.6 : 1 }}
        >
          {state === 'sending' ? 'Adding…' : 'Add'}
        </button>
      </div>
    </div>
  );
}

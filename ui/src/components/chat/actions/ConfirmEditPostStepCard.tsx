import { useState } from 'react';
import { updatePostStep } from '../../../api/modules';
import type { PostStepParentKind } from '../../../api/modules';
import type { PostStep } from '../../../types';
import { KeyValueGrid } from '../KeyValueGrid';
import { CodeBlock } from '../CodeBlock';
import { ActionCardHeader, actionCardStyle } from './shared';
import { tokens } from '../tokens';
import { primaryBtnStyle, errorLineStyle, successCardStyle } from '../styles';
import { summariseEventAssert } from './eventAssertSummary';

type EditPostStepPayload = {
  moduleId?: string;
  testSetId?: string;
  objectiveId?: string;
  parentKind?: PostStepParentKind;
  parentStepIndex?: number;
  postStepIndex?: number;
  postStep?: PostStep;
};

/**
 * REQ-004 §9b — NL-driven edit chat card. Generic across every post-step
 * payload type (dbCheck, eventAssert, webUi, desktopUi, api, aseXml,
 * aseXmlDeliver), so REQ-002's DB asserts and earlier UI post-steps gain
 * NL-edit support for free. PUTs through the existing post-step CRUD
 * endpoint — no new server route.
 *
 * The card renders a flat summary of the FULL replacement payload (the LLM
 * is required to emit the entire VerificationStep, not a diff). A future
 * enhancement could fetch the prior post-step and render a side-by-side
 * diff; for v1 we trust the user to read the new shape and accept.
 */
export function ConfirmEditPostStepCard({
  summary, data,
}: {
  summary?: string;
  data: unknown;
}) {
  const payload = (data ?? {}) as EditPostStepPayload;
  const [state, setState] = useState<'idle' | 'sending' | 'done' | 'error'>('idle');
  const [message, setMessage] = useState<string>('');

  async function execute() {
    if (!payload.moduleId || !payload.testSetId || !payload.objectiveId
        || !payload.parentKind || payload.parentStepIndex === undefined
        || payload.postStepIndex === undefined
        || !payload.postStep) {
      setState('error');
      setMessage('Missing moduleId / testSetId / objectiveId / parentKind / parentStepIndex / postStepIndex / postStep.');
      return;
    }
    setState('sending');
    try {
      await updatePostStep(
        payload.moduleId, payload.testSetId, payload.objectiveId,
        payload.parentKind, payload.parentStepIndex, payload.postStepIndex,
        payload.postStep);
      setState('done');
      setMessage(`Updated '${payload.postStep.description || payload.postStep.target}' on ${payload.parentKind}[${payload.parentStepIndex}] post-step #${payload.postStepIndex}.`);
    } catch (err) {
      setState('error');
      setMessage(err instanceof Error ? err.message : 'Update failed.');
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
    ['post-step', payload.postStepIndex !== undefined ? `#${payload.postStepIndex}` : undefined],
    ['target', ps?.target],
    ['description', ps?.description],
    ['wait (s)', ps?.waitBeforeSeconds?.toString()],
    ['role', ps?.role],
  ];

  // Reuse the same per-payload summarisation as confirmCreatePostStep, but
  // inline rather than via a shared helper — the two cards differ enough
  // (success message, button copy, header) that pulling out a hook adds
  // little. Keeps each card self-contained.
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
    rows.push(['connection', ps.dbCheck.connectionKey]);
  }
  if (ps?.eventAssert) {
    summariseEventAssert(ps.eventAssert).forEach(r => rows.push(r));
  }

  return (
    <div style={actionCardStyle(tokens.color.postTint)}>
      <ActionCardHeader kind="confirmEditPostStep" summary={summary} />
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
          {state === 'sending' ? 'Updating…' : 'Update'}
        </button>
      </div>
    </div>
  );
}

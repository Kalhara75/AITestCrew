import type { EventAssertStepDefinition } from '../../../types';

/**
 * Defensive summariser shared between ConfirmCreatePostStepCard and
 * ConfirmEditPostStepCard. The eventAssert payload comes off the wire from
 * an LLM emission — TS-typed as required, but in practice ANY field can be
 * missing if the model ran out of context, hallucinated a partial shape, or
 * the persisted action_json from an earlier app version dropped a field.
 *
 * Every access guards against undefined / null so a single malformed action
 * card can no longer crash the entire chat thread on rehydrate (the
 * symptom that turned the app white after a chat turn).
 */
export function summariseEventAssert(
  ea: EventAssertStepDefinition | undefined | null
): Array<[string, string | undefined]> {
  if (!ea) return [];
  const rows: Array<[string, string | undefined]> = [];

  // Entity — guard every access. A missing entity is shown as a placeholder
  // rather than throwing on `ea.entity.type`.
  const entity = ea.entity;
  if (entity) {
    const entityLabel = entity.type === 'Topic'
      ? `Topic ${entity.name ?? '?'} / Sub ${entity.subscriptionName ?? '?'}`
      : `Queue ${entity.name ?? '?'}`;
    rows.push(['entity', entityLabel]);
  } else {
    rows.push(['entity', '(not specified)']);
  }

  // Match mode + count bounds.
  const matchMode = ea.matchMode ?? 'AnyMessage';
  let modeLabel: string = matchMode;
  if (matchMode === 'ExactCount' || matchMode === 'MinCount' || matchMode === 'MaxCount') {
    modeLabel = `${matchMode}(${ea.expectedCount ?? '?'})`;
  } else if (matchMode === 'CountRange') {
    modeLabel = `CountRange(${ea.expectedCount ?? '?'}, ${ea.maxCount ?? '?'})`;
  }
  rows.push(['match', modeLabel]);

  if (ea.drainBeforeParent) rows.push(['drain', 'YES — pre-parent']);
  if (ea.bodyFormat && ea.bodyFormat !== 'Auto') rows.push(['body format', ea.bodyFormat]);
  if (ea.receiveMode && ea.receiveMode !== 'PeekLock') rows.push(['receive', ea.receiveMode]);
  if (ea.correlationFilter) rows.push(['correlationId', ea.correlationFilter]);
  if (ea.sessionId) rows.push(['sessionId', ea.sessionId]);
  if (ea.timeoutSeconds !== undefined && ea.timeoutSeconds !== null) {
    rows.push(['timeout (s)', String(ea.timeoutSeconds)]);
  }

  const criteria = ea.criteria ?? [];
  if (criteria.length > 0) {
    rows.push([
      'criteria',
      criteria.map(c => {
        if (!c) return '?';
        const field = c.field ?? '?';
        const op = c.operator ?? 'Equals';
        if (op === 'IsNull' || op === 'IsNotNull') return `${field} ${op}`;
        if (op === 'Between') return `${field} ${op} '${c.expected ?? ''}' … '${c.expected2 ?? ''}'`;
        return `${field} ${op} '${c.expected ?? ''}'`;
      }).join('; '),
    ]);
  }

  const captures = ea.captures ?? [];
  if (captures.length > 0) {
    rows.push([
      'captures',
      captures.map(c => {
        if (!c) return '?';
        return `{{${c.as ?? '?'}}} ← ${c.field ?? '?'}${c.required === false ? ' (optional)' : ''}`;
      }).join('; '),
    ]);
  }

  if (ea.connectionKey) rows.push(['connection', ea.connectionKey]);
  return rows;
}

import { useEffect, useState } from 'react';
import type {
  EventAssertStepDefinition, EventCriterion, EventCapture, AssertionOperator,
  ServiceBusEntityType, BodyFormat, ReceiveMode, MatchMode,
} from '../types';
import { peekServiceBusMessages, getServiceBusConnections, type PeekResponse, type PeekMessage } from '../api/eventAssert';

const OPERATORS: AssertionOperator[] = [
  'Equals', 'NotEquals', 'Contains', 'NotContains',
  'StartsWith', 'EndsWith', 'Regex',
  'GreaterThan', 'LessThan', 'Between',
  'IsNull', 'IsNotNull',
  'EqualsNumeric', 'EqualsDate',
];

const BODY_FORMATS: BodyFormat[] = ['Auto', 'Json', 'Xml', 'Text', 'Binary'];
const RECEIVE_MODES: ReceiveMode[] = ['PeekLock', 'ReceiveAndDelete'];
const MATCH_MODES: MatchMode[] = [
  'AnyMessage', 'AllMessages', 'ExactlyOne',
  'ExactCount', 'MinCount', 'MaxCount', 'CountRange',
];

export interface EditEventAssertSavePayload {
  name: string;
  definition: EventAssertStepDefinition;
}

interface Props {
  open: boolean;
  title?: string;
  definition: EventAssertStepDefinition;
  caseName: string;
  /** Active environment key — used for the connections dropdown + peek. */
  envKey?: string | null;
  onClose: () => void;
  onSave: (payload: EditEventAssertSavePayload) => Promise<void>;
  onDelete?: () => Promise<void>;
  deleteLabel?: string;
  deleteConfirmMessage?: string;
}

/**
 * Editor for an `EventAssertStepDefinition` post-step. Mirrors
 * `EditDbCheckStepDialog`'s shape so PostStepsPanel's wire-up is symmetric.
 *
 * Sections:
 *   1. Header — name, connection (dropdown sourced from
 *      /api/event-assert/connections), timeout, max messages.
 *   2. Entity — type radio (Queue/Topic) + name + conditional subscription.
 *   3. Modes — body format / receive mode / match mode (with conditional
 *      ExpectedCount / MaxCount inputs); drain-before-parent + complete-on-pass
 *      checkboxes; optional CorrelationFilter + SessionId.
 *   4. Criteria table — field path + operator + expected (+ expected2 / tolerances
 *      conditional on operator) + ignoreCase.
 *   5. Captures table — field, as, required.
 *   6. Peek panel — calls /api/event-assert/peek, renders an expandable preview;
 *      each peeked message has "Use as criterion" / "Use as capture" actions
 *      that pre-fill a row from the actual message values.
 */
export function EditEventAssertStepDialog({
  open, title, definition, caseName, envKey,
  onClose, onSave, onDelete, deleteLabel, deleteConfirmMessage,
}: Props) {
  const [form, setForm] = useState<EventAssertStepDefinition>(() => normaliseDefinition(definition));
  const [name, setName] = useState(caseName);
  const [connections, setConnections] = useState<string[]>([]);
  const [peek, setPeek] = useState<PeekResponse | null>(null);
  const [peekError, setPeekError] = useState<string | null>(null);
  const [peekRunning, setPeekRunning] = useState(false);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    getServiceBusConnections(envKey ?? null)
      .then(r => setConnections(r.keys))
      .catch(() => setConnections([]));
  }, [open, envKey]);

  if (!open) return null;

  const setField = <K extends keyof EventAssertStepDefinition>(
    key: K, value: EventAssertStepDefinition[K]
  ) => setForm(prev => ({ ...prev, [key]: value }));

  // ── Criteria helpers ──────────────────────────────────────────────

  const setCriterion = (idx: number, patch: Partial<EventCriterion>) =>
    setForm(prev => {
      const next = [...prev.criteria];
      next[idx] = { ...next[idx], ...patch };
      return { ...prev, criteria: next };
    });

  const addCriterion = (seed?: Partial<EventCriterion>) =>
    setForm(prev => ({
      ...prev,
      criteria: [
        ...prev.criteria,
        {
          field: '',
          operator: 'Equals',
          expected: '',
          expected2: undefined,
          ignoreCase: true,
          toleranceSeconds: undefined,
          toleranceDelta: undefined,
          ...seed,
        },
      ],
    }));

  const removeCriterion = (idx: number) =>
    setForm(prev => ({ ...prev, criteria: prev.criteria.filter((_, i) => i !== idx) }));

  // ── Captures helpers ──────────────────────────────────────────────

  const setCapture = (idx: number, patch: Partial<EventCapture>) =>
    setForm(prev => {
      const next = [...prev.captures];
      next[idx] = { ...next[idx], ...patch };
      return { ...prev, captures: next };
    });

  const addCapture = (seed?: Partial<EventCapture>) =>
    setForm(prev => ({
      ...prev,
      captures: [...prev.captures, { field: '', as: '', required: true, ...seed }],
    }));

  const removeCapture = (idx: number) =>
    setForm(prev => ({ ...prev, captures: prev.captures.filter((_, i) => i !== idx) }));

  // ── Peek ──────────────────────────────────────────────────────────

  const handlePeek = async () => {
    setPeekRunning(true);
    setPeekError(null);
    setPeek(null);
    try {
      if (!form.connectionKey || !form.entity.name) {
        throw new Error('Connection and entity name are required');
      }
      if (form.entity.type === 'Topic' && !form.entity.subscriptionName) {
        throw new Error('Subscription name is required for Topic entities');
      }
      const result = await peekServiceBusMessages({
        envKey: envKey ?? null,
        connectionKey: form.connectionKey,
        entity: form.entity,
        max: 10,
        correlationFilter: form.correlationFilter ?? null,
      });
      setPeek(result);
    } catch (err) {
      setPeekError(err instanceof Error ? err.message : 'Peek failed');
    } finally {
      setPeekRunning(false);
    }
  };

  // ── Save / Delete ─────────────────────────────────────────────────

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      // Mode-specific bound clean-up: clear count fields on modes that don't use
      // them so the persisted JSON matches the runtime contract.
      const usesExpectedCount =
        form.matchMode === 'ExactCount' || form.matchMode === 'MinCount'
        || form.matchMode === 'MaxCount' || form.matchMode === 'CountRange';
      const usesMaxCount = form.matchMode === 'CountRange';
      const cleaned: EventAssertStepDefinition = {
        ...form,
        expectedCount: usesExpectedCount ? form.expectedCount : undefined,
        maxCount: usesMaxCount ? form.maxCount : undefined,
        correlationFilter: form.correlationFilter || undefined,
        sessionId: form.sessionId || undefined,
        entity: {
          type: form.entity.type,
          name: form.entity.name,
          subscriptionName: form.entity.type === 'Topic' ? form.entity.subscriptionName : undefined,
        },
      };
      await onSave({ name, definition: cleaned });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete) return;
    setDeleting(true);
    setError(null);
    try {
      await onDelete();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed');
      setDeleting(false);
      setConfirmDelete(false);
    }
  };

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 18px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          {title ?? 'Edit Event Assertion Post-Step'}
        </h2>

        <div style={{ maxHeight: 'calc(80vh - 130px)', overflowY: 'auto', paddingRight: 6 }}>
          <label style={labelStyle}>Name</label>
          <input style={inputStyle} value={name} onChange={e => setName(e.target.value)} />

          <div style={{ display: 'flex', gap: 12, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Connection</label>
              <select style={inputStyle} value={form.connectionKey}
                onChange={e => setField('connectionKey', e.target.value)}>
                {!form.connectionKey && <option value="">(select…)</option>}
                {connections.map(k => <option key={k} value={k}>{k}</option>)}
                {form.connectionKey && !connections.includes(form.connectionKey) && (
                  <option value={form.connectionKey}>{form.connectionKey} (not configured)</option>
                )}
              </select>
            </div>
            <div style={{ width: 130 }}>
              <label style={labelStyle}>Timeout (s)</label>
              <input style={inputStyle} type="number" value={form.timeoutSeconds}
                onChange={e => setField('timeoutSeconds', Number.parseInt(e.target.value, 10) || 30)} />
            </div>
            <div style={{ width: 130 }}>
              <label style={labelStyle}>Max messages</label>
              <input style={inputStyle} type="number" value={form.maxMessages}
                onChange={e => setField('maxMessages', Number.parseInt(e.target.value, 10) || 50)} />
            </div>
          </div>

          {/* Entity */}
          <div style={{ marginTop: 12, display: 'flex', gap: 12, alignItems: 'flex-end' }}>
            <div>
              <label style={labelStyle}>Entity type</label>
              <div style={{ display: 'flex', gap: 12, alignItems: 'center', height: 32 }}>
                <label style={radioLabel}>
                  <input type="radio" name="entityType" checked={form.entity.type === 'Queue'}
                    onChange={() => setField('entity', { ...form.entity, type: 'Queue', subscriptionName: undefined })} />
                  Queue
                </label>
                <label style={radioLabel}>
                  <input type="radio" name="entityType" checked={form.entity.type === 'Topic'}
                    onChange={() => setField('entity', { ...form.entity, type: 'Topic' as ServiceBusEntityType })} />
                  Topic
                </label>
              </div>
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>{form.entity.type} name</label>
              <input style={inputStyle} value={form.entity.name}
                placeholder={form.entity.type === 'Queue' ? 'order-events' : 'meter-events'}
                onChange={e => setField('entity', { ...form.entity, name: e.target.value })} />
            </div>
            {form.entity.type === 'Topic' && (
              <div style={{ flex: 1 }}>
                <label style={labelStyle}>Subscription name</label>
                <input style={inputStyle} value={form.entity.subscriptionName ?? ''}
                  placeholder="test-runner"
                  onChange={e => setField('entity', { ...form.entity, subscriptionName: e.target.value })} />
              </div>
            )}
          </div>

          {/* Mode dropdowns */}
          <div style={{ display: 'flex', gap: 12, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Body format</label>
              <select style={inputStyle} value={form.bodyFormat}
                onChange={e => setField('bodyFormat', e.target.value as BodyFormat)}>
                {BODY_FORMATS.map(f => <option key={f} value={f}>{f}</option>)}
              </select>
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle} title="PeekLock + Complete on pass is safe for shared subscriptions">
                Receive mode
              </label>
              <select style={inputStyle} value={form.receiveMode}
                onChange={e => setField('receiveMode', e.target.value as ReceiveMode)}>
                {RECEIVE_MODES.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Match mode</label>
              <select style={inputStyle} value={form.matchMode}
                onChange={e => setField('matchMode', e.target.value as MatchMode)}>
                {MATCH_MODES.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </div>
            {(form.matchMode === 'ExactCount' || form.matchMode === 'MinCount'
              || form.matchMode === 'MaxCount' || form.matchMode === 'CountRange') && (
              <div style={{ width: 110 }}>
                <label style={labelStyle}>
                  {form.matchMode === 'CountRange' ? 'Min' : form.matchMode === 'MaxCount' ? 'Max' : 'Count'}
                </label>
                <input style={inputStyle} type="number" value={form.expectedCount ?? ''}
                  onChange={e => setField('expectedCount',
                    e.target.value === '' ? undefined : Number.parseInt(e.target.value, 10))} />
              </div>
            )}
            {form.matchMode === 'CountRange' && (
              <div style={{ width: 110 }}>
                <label style={labelStyle}>Max</label>
                <input style={inputStyle} type="number" value={form.maxCount ?? ''}
                  onChange={e => setField('maxCount',
                    e.target.value === '' ? undefined : Number.parseInt(e.target.value, 10))} />
              </div>
            )}
          </div>

          {/* Drain + Complete + filters */}
          <div style={{ marginTop: 12, display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'center' }}>
            <label style={checkboxLabel}
              title="Drains stale messages from the entity (ReceiveAndDelete) BEFORE the parent step runs. Use for ExactlyOne / MaxCount on shared subs.">
              <input type="checkbox" checked={form.drainBeforeParent}
                onChange={e => setField('drainBeforeParent', e.target.checked)} />
              Drain before parent
            </label>
            <label style={checkboxLabel}
              title="On PeekLock + pass: Complete passing messages and Abandon others. Off → Abandon all (debug-friendly, leaves messages in place).">
              <input type="checkbox" checked={form.completeOnPass}
                onChange={e => setField('completeOnPass', e.target.checked)} />
              Complete on pass
            </label>
          </div>

          <div style={{ display: 'flex', gap: 12, marginTop: 12 }}>
            <div style={{ flex: 2 }}>
              <label style={labelStyle}>CorrelationId filter (optional, {`{{Token}}`} OK)</label>
              <input style={inputStyle} value={form.correlationFilter ?? ''}
                placeholder="abc-{{TestRunId}}"
                onChange={e => setField('correlationFilter', e.target.value)} />
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Session ID (optional)</label>
              <input style={inputStyle} value={form.sessionId ?? ''}
                onChange={e => setField('sessionId', e.target.value)} />
            </div>
          </div>

          {/* Peek button + panel */}
          <div style={{ marginTop: 16, display: 'flex', alignItems: 'center', gap: 8 }}>
            <button onClick={handlePeek}
              disabled={peekRunning || !form.connectionKey || !form.entity.name}
              style={tryQueryBtn}>
              {peekRunning ? 'Peeking…' : 'Peek messages'}
            </button>
            {peekError && <span style={{ color: '#dc2626', fontSize: 12 }}>{peekError}</span>}
            {peek && (
              <span style={{ color: '#475569', fontSize: 12 }}>
                {peek.totalPeeked} message{peek.totalPeeked === 1 ? '' : 's'} on the entity
                {peek.messages.length !== peek.totalPeeked
                  && ` (${peek.messages.length} after correlation filter)`}.
              </span>
            )}
          </div>

          {peek && peek.messages.length > 0 && (
            <div style={{ marginTop: 8 }}>
              {peek.messages.map((m, i) => (
                <PeekedMessage key={i} idx={i} m={m}
                  onAddCriterion={(field, expected) => addCriterion({ field, expected })}
                  onAddCapture={(field, asName) => addCapture({ field, as: asName })}
                />
              ))}
            </div>
          )}

          {/* Criteria */}
          <div style={{ marginTop: 16 }}>
            <div style={{ marginBottom: 6, fontSize: 12, fontWeight: 600, color: '#475569' }}>
              Criteria ({form.criteria.length})
              <span style={{ marginLeft: 8, fontWeight: 400, color: '#94a3b8' }}>
                Field paths: <code style={inlineCode}>MessageId</code>, <code style={inlineCode}>ApplicationProperties.X</code>,{' '}
                <code style={inlineCode}>Body.X</code>, <code style={inlineCode}>BodyXml.//X</code>, <code style={inlineCode}>BodyText</code>, <code style={inlineCode}>BodyLength</code>
              </span>
            </div>
            {form.criteria.map((c, i) => (
              <div key={i} style={rowStyle}>
                <input placeholder="Field" value={c.field}
                  style={{ ...inputStyle, width: 220, fontFamily: 'ui-monospace,Consolas,monospace' }}
                  onChange={e => setCriterion(i, { field: e.target.value })} />
                <select value={c.operator} style={{ ...inputStyle, width: 130 }}
                  onChange={e => setCriterion(i, { operator: e.target.value as AssertionOperator })}>
                  {OPERATORS.map(o => <option key={o} value={o}>{o}</option>)}
                </select>
                {!isUnaryOperator(c.operator) && (
                  <input placeholder="Expected" value={c.expected}
                    style={{ ...inputStyle, flex: 1, minWidth: 120 }}
                    onChange={e => setCriterion(i, { expected: e.target.value })} />
                )}
                {c.operator === 'Between' && (
                  <input placeholder="Expected2 (upper)" value={c.expected2 ?? ''}
                    style={{ ...inputStyle, width: 140 }}
                    onChange={e => setCriterion(i, { expected2: e.target.value })} />
                )}
                {c.operator === 'EqualsDate' && (
                  <input placeholder="Tolerance (s)" value={c.toleranceSeconds ?? ''}
                    type="number" style={{ ...inputStyle, width: 110 }}
                    onChange={e => setCriterion(i, {
                      toleranceSeconds: e.target.value ? Number(e.target.value) : undefined,
                    })} />
                )}
                {c.operator === 'EqualsNumeric' && (
                  <input placeholder="Tolerance" value={c.toleranceDelta ?? ''}
                    type="number" step="any" style={{ ...inputStyle, width: 110 }}
                    onChange={e => setCriterion(i, {
                      toleranceDelta: e.target.value ? Number(e.target.value) : undefined,
                    })} />
                )}
                <label style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, color: '#475569' }}>
                  <input type="checkbox" checked={c.ignoreCase}
                    onChange={e => setCriterion(i, { ignoreCase: e.target.checked })} />
                  ignoreCase
                </label>
                <button onClick={() => removeCriterion(i)} style={iconBtnStyle} title="Remove">×</button>
              </div>
            ))}
            <button onClick={() => addCriterion()} style={addBtnStyle}>+ Add criterion</button>
          </div>

          {/* Captures */}
          <div style={{ marginTop: 16 }}>
            <div style={{ marginBottom: 6, fontSize: 12, fontWeight: 600, color: '#475569' }}>
              Captures ({form.captures.length})
            </div>
            {form.captures.map((c, i) => (
              <div key={i} style={rowStyle}>
                <input placeholder="Field" value={c.field}
                  style={{ ...inputStyle, width: 240, fontFamily: 'ui-monospace,Consolas,monospace' }}
                  onChange={e => setCapture(i, { field: e.target.value })} />
                <input placeholder='As (e.g. "MessageId")' value={c.as}
                  style={{ ...inputStyle, width: 160 }}
                  onChange={e => setCapture(i, { as: e.target.value })} />
                <label style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, color: '#475569' }}>
                  <input type="checkbox" checked={c.required}
                    onChange={e => setCapture(i, { required: e.target.checked })} />
                  required
                </label>
                <button onClick={() => removeCapture(i)} style={iconBtnStyle} title="Remove">×</button>
              </div>
            ))}
            <button onClick={() => addCapture()} style={addBtnStyle}>+ Add capture</button>
          </div>
        </div>

        {error && <p style={{ color: '#dc2626', fontSize: 13, margin: '10px 0 0' }}>{error}</p>}

        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 16 }}>
          <div>
            {onDelete && !confirmDelete && (
              <button onClick={() => setConfirmDelete(true)} style={deleteBtnStyle}>
                {deleteLabel ?? 'Delete Step'}
              </button>
            )}
            {confirmDelete && onDelete && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 13, color: '#dc2626' }}>
                  {deleteConfirmMessage ?? 'Delete this step?'}
                </span>
                <button onClick={handleDelete} disabled={deleting} style={deleteBtnStyle}>
                  {deleting ? 'Deleting…' : 'Yes, delete'}
                </button>
                <button onClick={() => setConfirmDelete(false)} style={cancelBtnStyle}>Cancel</button>
              </div>
            )}
          </div>
          <div style={{ display: 'flex', gap: 10 }}>
            <button onClick={onClose} style={cancelBtnStyle}>Cancel</button>
            <button onClick={handleSave} disabled={saving} style={saveBtnStyle}>
              {saving ? 'Saving…' : 'Save'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function PeekedMessage({
  idx, m, onAddCriterion, onAddCapture,
}: {
  idx: number;
  m: PeekMessage;
  onAddCriterion: (field: string, expected: string) => void;
  onAddCapture: (field: string, asName: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const summary = `[${idx}] ${m.messageId ?? '(no id)'}${m.correlationId ? ` (corr=${m.correlationId})` : ''}${m.contentType ? ` · ${m.contentType}` : ''}`;
  const enqueued = m.enqueuedTimeUtc ? new Date(m.enqueuedTimeUtc).toISOString() : '';

  // Available "+ criterion / + capture" sources from this message: every
  // system property + every ApplicationProperties.<name>. Body fields aren't
  // auto-listed (the path syntax depends on body shape) — the user can
  // hand-type Body.X or BodyXml.X and use the message body preview as a guide.
  const sources: Array<{ field: string; value: string | null }> = [
    { field: 'MessageId', value: m.messageId ?? null },
    { field: 'CorrelationId', value: m.correlationId ?? null },
    { field: 'Subject', value: m.subject ?? null },
    { field: 'ContentType', value: m.contentType ?? null },
    ...Object.entries(m.applicationProperties).map(([k, v]) => ({
      field: `ApplicationProperties.${k}`,
      value: v,
    })),
  ];

  return (
    <div style={{
      border: '1px solid #e2e8f0', borderRadius: 6, padding: 6,
      marginBottom: 4, background: '#fff',
    }}>
      <button
        onClick={() => setExpanded(v => !v)}
        style={{
          background: 'transparent', border: 'none', padding: 0, cursor: 'pointer',
          textAlign: 'left', color: '#0f172a', fontSize: 12,
          fontFamily: 'ui-monospace,Consolas,monospace',
        }}
      >
        {expanded ? '▾' : '▸'} {summary}
      </button>
      {expanded && (
        <div style={{ marginTop: 6 }}>
          <div style={{ fontSize: 11, color: '#475569', marginBottom: 4 }}>
            <strong>enqueued:</strong> {enqueued}
            <span style={{ marginLeft: 10 }}>
              <strong>delivery:</strong> {m.deliveryCount}
            </span>
            <span style={{ marginLeft: 10 }}>
              <strong>body:</strong> {m.body.format} ({m.body.length} bytes)
            </span>
          </div>
          {sources.filter(s => s.value !== null).map((s, i) => (
            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12, marginBottom: 3 }}>
              <code style={{ ...inlineCode, minWidth: 220 }}>{s.field}</code>
              <span style={{ flex: 1, fontFamily: 'ui-monospace,Consolas,monospace', color: '#0f172a' }}>
                {s.value}
              </span>
              <button
                style={pickBtn}
                title={`Add an Equals criterion: ${s.field} = '${s.value}'`}
                onClick={() => onAddCriterion(s.field, s.value!)}
              >+ criterion</button>
              <button
                style={pickBtn}
                title={`Capture ${s.field} into {{${tailOf(s.field)}}}`}
                onClick={() => onAddCapture(s.field, tailOf(s.field))}
              >+ capture</button>
            </div>
          ))}
          {m.body.preview && (
            <div style={{ marginTop: 6 }}>
              <pre style={{
                margin: 0, padding: '6px 8px', background: '#f8fafc',
                border: '1px solid #e2e8f0', borderRadius: 4, fontSize: 11,
                whiteSpace: 'pre-wrap', wordBreak: 'break-word',
                fontFamily: 'ui-monospace,Consolas,monospace',
                maxHeight: 200, overflow: 'auto',
              }}>{m.body.preview}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function tailOf(fieldPath: string): string {
  const dot = fieldPath.lastIndexOf('.');
  return dot >= 0 ? fieldPath.slice(dot + 1) : fieldPath;
}

function isUnaryOperator(op: AssertionOperator): boolean {
  return op === 'IsNull' || op === 'IsNotNull';
}

function normaliseDefinition(def: EventAssertStepDefinition): EventAssertStepDefinition {
  return {
    name: def.name ?? '',
    connectionKey: def.connectionKey ?? '',
    entity: {
      type: def.entity?.type ?? 'Queue',
      name: def.entity?.name ?? '',
      subscriptionName: def.entity?.subscriptionName,
    },
    bodyFormat: def.bodyFormat ?? 'Auto',
    receiveMode: def.receiveMode ?? 'PeekLock',
    matchMode: def.matchMode ?? 'AnyMessage',
    expectedCount: def.expectedCount,
    maxCount: def.maxCount,
    timeoutSeconds: def.timeoutSeconds || 30,
    maxMessages: def.maxMessages || 50,
    drainBeforeParent: !!def.drainBeforeParent,
    completeOnPass: def.completeOnPass !== false,
    correlationFilter: def.correlationFilter,
    sessionId: def.sessionId,
    criteria: def.criteria ? def.criteria.map(c => ({ ...c, ignoreCase: c.ignoreCase !== false })) : [],
    captures: def.captures ? def.captures.map(c => ({ ...c, required: c.required !== false })) : [],
  };
}

// ── Styles ─────────────────────────────────────────────────────

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.45)',
  display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28,
  width: '90vw', maxWidth: 1100, boxShadow: '0 20px 60px rgba(0,0,0,0.2)',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 12, fontWeight: 600,
  color: '#64748b', textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 4,
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', border: '1px solid #e2e8f0',
  borderRadius: 6, fontSize: 13, outline: 'none', boxSizing: 'border-box',
};
const rowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4,
  padding: '6px 8px', background: '#f8fafc', borderRadius: 6, border: '1px solid #e2e8f0',
};
const radioLabel: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, color: '#0f172a',
};
const checkboxLabel: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, color: '#475569',
};
const iconBtnStyle: React.CSSProperties = {
  background: 'none', border: '1px solid #e2e8f0', borderRadius: 4,
  padding: '2px 8px', cursor: 'pointer', fontSize: 14, color: '#ef4444',
};
const addBtnStyle: React.CSSProperties = {
  marginTop: 6, padding: '6px 14px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#475569',
};
const tryQueryBtn: React.CSSProperties = {
  padding: '6px 14px', background: '#e0e7ff', border: '1px solid #c7d2fe',
  borderRadius: 6, cursor: 'pointer', fontSize: 12, color: '#3730a3', fontWeight: 600,
};
const pickBtn: React.CSSProperties = {
  padding: '2px 6px', background: 'none', border: '1px solid #c7d2fe',
  borderRadius: 3, cursor: 'pointer', fontSize: 11, color: '#3730a3',
};
const cancelBtnStyle: React.CSSProperties = {
  padding: '8px 20px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 14, color: '#475569',
};
const deleteBtnStyle: React.CSSProperties = {
  padding: '8px 18px', background: '#fef2f2', border: '1px solid #fecaca',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#dc2626', fontWeight: 600,
};
const saveBtnStyle: React.CSSProperties = {
  padding: '8px 20px', background: '#3b82f6', border: 'none',
  borderRadius: 6, cursor: 'pointer', fontSize: 14, color: '#fff', fontWeight: 600,
};
const inlineCode: React.CSSProperties = {
  fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12,
  background: '#f1f5f9', borderRadius: 3, padding: '1px 4px', color: '#475569',
};

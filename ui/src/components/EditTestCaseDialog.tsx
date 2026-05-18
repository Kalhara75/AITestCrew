import { useEffect, useState } from 'react';
import { updateObjective, deleteObjective } from '../api/modules';
import { dryRunApiStep, type ApiDryRunResponse } from '../api/apiStep';
import type { ApiTestDefinition, ApiAssertion, ApiCapture, TestObjective } from '../types';

const OPERATORS = [
  'Equals', 'NotEquals', 'Contains', 'NotContains',
  'StartsWith', 'EndsWith', 'Regex',
  'GreaterThan', 'LessThan', 'Between',
  'IsNull', 'IsNotNull', 'EqualsNumeric', 'EqualsDate',
] as const;
type AssertionSource = 'Status' | 'Header' | 'Body' | 'BodyText';
const SOURCES: AssertionSource[] = ['Status', 'Header', 'Body', 'BodyText'];

interface Props {
  open: boolean;
  objective: TestObjective;
  stepIndex: number;
  moduleId: string;
  testSetId: string;
  envKey?: string | null;
  stackKey?: string | null;
  apiModule?: string | null;
  onClose: () => void;
  onSaved: () => void;
  onDeleted?: () => void;
}

export function EditTestCaseDialog({
  open, objective, stepIndex, moduleId, testSetId,
  envKey, stackKey, apiModule,
  onClose, onSaved, onDeleted,
}: Props) {
  const step = objective.apiSteps[stepIndex];
  const [form, setForm] = useState<ApiTestDefinition>(() => structuredClone(step));
  const [name, setName] = useState(objective.name);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [dryRunning, setDryRunning] = useState(false);
  const [dryRunResult, setDryRunResult] = useState<ApiDryRunResponse | null>(null);
  const [dryRunError, setDryRunError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) { setDryRunResult(null); setDryRunError(null); }
  }, [open]);

  if (!open) return null;

  const set = <K extends keyof ApiTestDefinition>(key: K, value: ApiTestDefinition[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const assertions = form.apiAssertions ?? [];
  const setAssertion = (idx: number, patch: Partial<ApiAssertion>) =>
    set('apiAssertions', assertions.map((a, i) => i === idx ? { ...a, ...patch } : a));
  const addAssertion = () =>
    set('apiAssertions', [...assertions, { source: 'Body', operator: 'Equals', expected: '', ignoreCase: true }]);
  const removeAssertion = (idx: number) =>
    set('apiAssertions', assertions.filter((_, i) => i !== idx));

  const captures = form.captures ?? [];
  const setCapture = (idx: number, patch: Partial<ApiCapture>) =>
    set('captures', captures.map((c, i) => i === idx ? { ...c, ...patch } : c));
  const addCapture = () =>
    set('captures', [...captures, { source: 'Body', jsonPath: '', as: '', required: true }]);
  const removeCapture = (idx: number) =>
    set('captures', captures.filter((_, i) => i !== idx));

  const handleTryCall = async () => {
    setDryRunning(true); setDryRunError(null); setDryRunResult(null);
    try {
      const result = await dryRunApiStep({
        envKey: envKey ?? null, stackKey: stackKey ?? null, moduleKey: apiModule ?? null,
        method: form.method, endpoint: form.endpoint, headers: form.headers,
        queryParams: form.queryParams, body: form.body, parameters: {},
      });
      setDryRunResult(result);
    } catch (err) {
      setDryRunError(err instanceof Error ? err.message : 'Try call failed');
    } finally { setDryRunning(false); }
  };

  const handleSave = async () => {
    setSaving(true); setError(null);
    try {
      const updatedSteps = [...objective.apiSteps];
      updatedSteps[stepIndex] = form;
      await updateObjective(moduleId, testSetId, objective.id, { ...objective, name, apiSteps: updatedSteps });
      onSaved(); onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally { setSaving(false); }
  };

  const isLastStep = objective.apiSteps.length <= 1;
  const handleDelete = async () => {
    setDeleting(true); setError(null);
    try {
      if (isLastStep) {
        await deleteObjective(moduleId, testSetId, objective.id);
      } else {
        const updatedSteps = objective.apiSteps.filter((_, i) => i !== stepIndex);
        await updateObjective(moduleId, testSetId, objective.id, { ...objective, apiSteps: updatedSteps });
      }
      onDeleted?.(); onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed');
    } finally { setDeleting(false); setConfirmDelete(false); }
  };
  const isWriteMethod = ['POST', 'PUT', 'PATCH', 'DELETE'].includes(form.method.toUpperCase());
  const statusColour = dryRunResult
    ? dryRunResult.status >= 200 && dryRunResult.status < 300 ? '#16a34a'
      : dryRunResult.status >= 400 ? '#dc2626' : '#f59e0b'
    : '';

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={{ ...dialogStyle, width: 700 }} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 20px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>Edit Test Case</h2>
        <div style={{ maxHeight: 'calc(80vh - 120px)', overflowY: 'auto', paddingRight: 8 }}>
          <label style={labelStyle}>Name</label>
          <input style={inputStyle} value={name} onChange={e => setName(e.target.value)} />
          <label style={{ ...labelStyle, marginTop: 12 }}>Description</label>
          <input style={inputStyle} placeholder="Optional step description — shown as Test Name in the inner table" value={form.description ?? ''} onChange={e => set('description', e.target.value)} />
          <div style={{ display: 'flex', gap: 12, marginTop: 14 }}>
            <div style={{ width: 120 }}>
              <label style={labelStyle}>Method</label>
              <select style={{ ...inputStyle, cursor: 'pointer' }} value={form.method}
                onChange={e => set('method', e.target.value)}>
                {['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Endpoint</label>
              <input style={inputStyle} value={form.endpoint} onChange={e => set('endpoint', e.target.value)} />
            </div>
          </div>
          <div style={{ marginTop: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
            <button onClick={handleTryCall} disabled={dryRunning} style={tryCallBtnStyle}>
              {dryRunning ? 'Calling...' : 'Try Call'}
            </button>
            {isWriteMethod && (
              <span style={{ fontSize: 12, color: '#b45309', background: '#fef3c7', padding: '2px 8px', borderRadius: 4, border: '1px solid #fde68a' }}>
                {form.method} will execute against the live API
              </span>
            )}
            {dryRunResult && (
              <span style={{ fontSize: 13, fontWeight: 700, color: statusColour }}>
                {dryRunResult.status} {dryRunResult.reasonPhrase}
              </span>
            )}
            {dryRunError && <span style={{ fontSize: 12, color: '#dc2626' }}>{dryRunError}</span>}
          </div>
          {dryRunResult && (
            <div style={{ marginTop: 10, border: '1px solid #e2e8f0', borderRadius: 6, overflow: 'hidden' }}>
              <div style={{ background: '#f8fafc', borderBottom: '1px solid #e2e8f0', padding: '6px 12px', display: 'flex', justifyContent: 'space-between' }}>
                <span style={{ fontSize: 12, fontWeight: 600, color: '#475569' }}>Response</span>
                {dryRunResult.bodyTruncated && <span style={{ fontSize: 11, color: '#94a3b8' }}>Body truncated at 32KB</span>}
              </div>
              <div style={{ padding: 10 }}>
                <div style={{ fontSize: 11, color: '#94a3b8', marginBottom: 4 }}>Headers</div>
                <div style={{ fontSize: 12, fontFamily: 'ui-monospace,Consolas,monospace', color: '#334155', marginBottom: 8 }}>
                  {Object.entries(dryRunResult.headers).slice(0, 8).map(([k, v]) => (
                    <div key={k}><b>{k}</b>: {v}</div>
                  ))}
                  {Object.keys(dryRunResult.headers).length > 8 && (
                    <div style={{ color: '#94a3b8' }}>...{Object.keys(dryRunResult.headers).length - 8} more</div>
                  )}
                </div>
                <div style={{ fontSize: 11, color: '#94a3b8', marginBottom: 4 }}>Body</div>
                <textarea readOnly
                  style={{ ...inputStyle, minHeight: 80, maxHeight: 200, resize: 'vertical', fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12, background: '#f8fafc' }}
                  value={dryRunResult.body}
                />
              </div>
            </div>
          )}
          <KeyValueEditor label="Headers" value={form.headers} onChange={v => set('headers', v)} />
          <KeyValueEditor label="Query Params" value={form.queryParams} onChange={v => set('queryParams', v)} />
          <div style={{ marginTop: 14 }}>
            <label style={labelStyle}>Body <span style={{ fontWeight: 400, color: '#94a3b8' }}>(JSON)</span></label>
            <textarea style={{ ...inputStyle, minHeight: 80, resize: 'vertical', fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 13 }}
              value={form.body != null ? JSON.stringify(form.body, null, 2) : ''}
              onChange={e => {
                const v = e.target.value.trim();
                if (!v) { set('body', null); return; }
                try { set('body', JSON.parse(v)); } catch { /* keep typing */ }
              }}
            />
          </div>
          <SectionHeader label="Assertions" count={assertions.length}
            hint="Rule-by-rule evaluation. Non-empty list bypasses LLM hybrid validation." />
          {assertions.length === 0 && <p style={hintStyle}>No assertions - LLM hybrid validation runs.</p>}
          {assertions.map((a, idx) => (
            <AssertionRow key={idx} assertion={a}
              onChange={patch => setAssertion(idx, patch)}
              onRemove={() => removeAssertion(idx)} />
          ))}
          <button onClick={addAssertion} style={{ ...smallBtnStyle, color: '#2563eb', fontSize: 12, marginTop: 4 }}>+ Add Assertion</button>
          <SectionHeader label="Captures" count={captures.length}
            hint="Bind response values as {{Token}} for downstream post-steps." />
          {captures.length === 0 && <p style={hintStyle}>No captures defined.</p>}
          {captures.map((c, idx) => (
            <CaptureRow key={idx} capture={c}
              onChange={patch => setCapture(idx, patch)}
              onRemove={() => removeCapture(idx)} />
          ))}
          <button onClick={addCapture} style={{ ...smallBtnStyle, color: '#2563eb', fontSize: 12, marginTop: 4 }}>+ Add Capture</button>
          <details style={{ marginTop: 14 }}>
            <summary style={{ cursor: 'pointer', fontSize: 13, fontWeight: 600, color: '#94a3b8' }}>Legacy validation fields</summary>
            <div style={{ marginTop: 8 }}>
              <div style={{ marginBottom: 10 }}>
                <label style={labelStyle}>Expected Status</label>
                <input type="number" style={{ ...inputStyle, width: 160 }} value={form.expectedStatus}
                  onChange={e => set('expectedStatus', parseInt(e.target.value) || 0)} />
              </div>
              <TagListEditor label="Expected Body Contains" value={form.expectedBodyContains}
                onChange={v => set('expectedBodyContains', v)} />
              <TagListEditor label="Expected Body NOT Contains" value={form.expectedBodyNotContains}
                onChange={v => set('expectedBodyNotContains', v)} />
            </div>
          </details>
          <label style={{ ...labelStyle, marginTop: 14, display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" checked={form.isFuzzTest} onChange={e => set('isFuzzTest', e.target.checked)} />
            Fuzz Test
          </label>
        </div>
        {error && <p style={errorStyle}>{error}</p>}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 20 }}>
          <div>
            {onDeleted && !confirmDelete && (
              <button onClick={() => setConfirmDelete(true)} style={deleteBtnStyle} disabled={deleting}>Delete Step</button>
            )}
            {onDeleted && confirmDelete && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 13, color: '#dc2626' }}>
                  {isLastStep ? 'Last step - entire test case will be deleted.' : 'Delete this step?'}
                </span>
                <button onClick={handleDelete} disabled={deleting} style={deleteBtnStyle}>
                  {deleting ? 'Deleting...' : 'Yes, delete'}
                </button>
                <button onClick={() => setConfirmDelete(false)} style={cancelBtnStyle} disabled={deleting}>No</button>
              </div>
            )}
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={onClose} style={cancelBtnStyle} disabled={saving}>Cancel</button>
            <button onClick={handleSave} style={saveBtnStyle} disabled={saving}>
              {saving ? 'Saving...' : 'Save'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function SectionHeader({ label, count, hint }: { label: string; count: number; hint: string }) {
  return (
    <div style={{ marginTop: 18, borderTop: '1px solid #f1f5f9', paddingTop: 12 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
        <span style={{ fontSize: 13, fontWeight: 700, color: '#1e40af' }}>{label}</span>
        {count > 0 && <span style={{ fontSize: 11, color: '#64748b' }}>{count} rule{count > 1 ? 's' : ''}</span>}
      </div>
      <p style={{ margin: '2px 0 6px', fontSize: 11, color: '#94a3b8' }}>{hint}</p>
    </div>
  );
}

function AssertionRow({ assertion, onChange, onRemove }: {
  assertion: ApiAssertion;
  onChange: (patch: Partial<ApiAssertion>) => void;
  onRemove: () => void;
}) {
  const needsHeader = assertion.source === 'Header';
  const needsJsonPath = assertion.source === 'Body';
  const needsExpected2 = assertion.operator === 'Between';
  const noValueOps = ['IsNull', 'IsNotNull'];
  const needsExpected = !noValueOps.includes(assertion.operator ?? '');
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '110px 1fr auto', gap: 6, marginBottom: 6, alignItems: 'start' }}>
      <select style={{ ...inputStyle, fontSize: 12 }} value={assertion.source ?? 'Body'}
        onChange={e => onChange({ source: e.target.value as AssertionSource, headerName: undefined, jsonPath: undefined })}>
        {SOURCES.map(s => <option key={s} value={s}>{s}</option>)}
      </select>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        {needsHeader && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="Header name" value={assertion.headerName ?? ''}
            onChange={e => onChange({ headerName: e.target.value })} />
        )}
        {needsJsonPath && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="JSONPath e.g. $.data.id" value={assertion.jsonPath ?? ''}
            onChange={e => onChange({ jsonPath: e.target.value })} />
        )}
        <select style={{ ...inputStyle, fontSize: 12 }} value={assertion.operator ?? 'Equals'}
          onChange={e => onChange({ operator: e.target.value })}>
          {OPERATORS.map(op => <option key={op} value={op}>{op}</option>)}
        </select>
        {needsExpected && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="Expected value or {{Token}}" value={assertion.expected ?? ''}
            onChange={e => onChange({ expected: e.target.value })} />
        )}
        {needsExpected2 && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="Upper bound (Between)" value={assertion.expected2 ?? ''}
            onChange={e => onChange({ expected2: e.target.value })} />
        )}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, alignItems: 'flex-end' }}>
        <label style={{ fontSize: 12, color: '#64748b', display: 'flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
          <input type="checkbox" checked={assertion.ignoreCase ?? true}
            onChange={e => onChange({ ignoreCase: e.target.checked })} />
          Ignore case
        </label>
        <button onClick={onRemove} style={{ ...smallBtnStyle, color: '#dc2626', padding: '3px 7px' }} title="Remove">x</button>
      </div>
    </div>
  );
}

function CaptureRow({ capture, onChange, onRemove }: {
  capture: ApiCapture;
  onChange: (patch: Partial<ApiCapture>) => void;
  onRemove: () => void;
}) {
  const needsHeader = capture.source === 'Header';
  const needsJsonPath = capture.source === 'Body';
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '110px 1fr auto', gap: 6, marginBottom: 6, alignItems: 'start' }}>
      <select style={{ ...inputStyle, fontSize: 12 }} value={capture.source ?? 'Body'}
        onChange={e => onChange({ source: e.target.value as AssertionSource, headerName: undefined, jsonPath: undefined })}>
        {SOURCES.map(s => <option key={s} value={s}>{s}</option>)}
      </select>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        {needsHeader && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="Header name" value={capture.headerName ?? ''}
            onChange={e => onChange({ headerName: e.target.value })} />
        )}
        {needsJsonPath && (
          <input style={{ ...inputStyle, fontSize: 12 }} placeholder="JSONPath e.g. $.data.id" value={capture.jsonPath ?? ''}
            onChange={e => onChange({ jsonPath: e.target.value })} />
        )}
        <input style={{ ...inputStyle, fontSize: 12 }} placeholder="Bind as Token (no braces)" value={capture.as ?? ''}
          onChange={e => onChange({ as: e.target.value })} />
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, alignItems: 'flex-end' }}>
        <label style={{ fontSize: 12, color: '#64748b', display: 'flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
          <input type="checkbox" checked={capture.required ?? true}
            onChange={e => onChange({ required: e.target.checked })} />
          Required
        </label>
        <button onClick={onRemove} style={{ ...smallBtnStyle, color: '#dc2626', padding: '3px 7px' }} title="Remove">x</button>
      </div>
    </div>
  );
}

function KeyValueEditor({ label, value, onChange }: {
  label: string; value: Record<string, string>; onChange: (v: Record<string, string>) => void;
}) {
  const entries = Object.entries(value);
  const update = (oldKey: string, newKey: string, newVal: string) => {
    const next = { ...value };
    if (oldKey !== newKey) delete next[oldKey];
    next[newKey] = newVal;
    onChange(next);
  };
  const remove = (key: string) => { const next = { ...value }; delete next[key]; onChange(next); };
  const add = () => onChange({ ...value, '': '' });
  return (
    <div style={{ marginTop: 14 }}>
      <label style={labelStyle}>{label}</label>
      {entries.map(([k, v], i) => (
        <div key={i} style={{ display: 'flex', gap: 6, marginBottom: 4 }}>
          <input style={{ ...inputStyle, flex: 1 }} placeholder="Key" value={k}
            onChange={e => update(k, e.target.value, v)} />
          <input style={{ ...inputStyle, flex: 2 }} placeholder="Value" value={v}
            onChange={e => update(k, k, e.target.value)} />
          <button onClick={() => remove(k)} style={smallBtnStyle} title="Remove">x</button>
        </div>
      ))}
      <button onClick={add} style={{ ...smallBtnStyle, color: '#2563eb', fontSize: 12 }}>+ Add</button>
    </div>
  );
}

function TagListEditor({ label, value, onChange }: {
  label: string; value: string[]; onChange: (v: string[]) => void;
}) {
  const [input, setInput] = useState('');
  const add = () => {
    const trimmed = input.trim();
    if (!trimmed) return;
    onChange([...value, trimmed]);
    setInput('');
  };
  return (
    <div style={{ marginTop: 14 }}>
      <label style={labelStyle}>{label}</label>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginBottom: 6 }}>
        {value.map((tag, i) => (
          <span key={i} style={tagStyle}>
            {tag}
            <button onClick={() => onChange(value.filter((_, j) => j !== i))}
              style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#94a3b8', padding: '0 0 0 4px', fontSize: 12 }}>
              x
            </button>
          </span>
        ))}
      </div>
      <div style={{ display: 'flex', gap: 6 }}>
        <input style={{ ...inputStyle, flex: 1 }} placeholder="Add value..." value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} />
        <button onClick={add} style={{ ...smallBtnStyle, color: '#2563eb' }}>Add</button>
      </div>
    </div>
  );
}

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex',
  alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28, width: 620, maxWidth: '95vw',
  maxHeight: '90vh', boxShadow: '0 20px 60px rgba(0,0,0,0.15)', display: 'flex', flexDirection: 'column',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 13, fontWeight: 600, color: '#475569', marginBottom: 6,
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', fontSize: 14, border: '1px solid #e2e8f0',
  borderRadius: 6, outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
};
const tagStyle: React.CSSProperties = {
  fontSize: 12, padding: '2px 8px', borderRadius: 4, background: '#f1f5f9',
  color: '#334155', border: '1px solid #e2e8f0', display: 'inline-flex', alignItems: 'center',
};
const smallBtnStyle: React.CSSProperties = {
  background: 'none', border: '1px solid #e2e8f0', borderRadius: 4,
  cursor: 'pointer', padding: '4px 8px', color: '#64748b', fontSize: 13,
};
const hintStyle: React.CSSProperties = {
  margin: '0 0 6px', fontSize: 12, color: '#94a3b8', fontStyle: 'italic',
};
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 13, marginTop: 12, padding: '6px 12px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};
const tryCallBtnStyle: React.CSSProperties = {
  padding: '6px 14px', background: '#f1f5f9', border: '1px solid #cbd5e1',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, fontWeight: 600, color: '#1e40af',
};
const deleteBtnStyle: React.CSSProperties = {
  padding: '8px 18px', background: '#fef2f2', border: '1px solid #fecaca',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#dc2626', fontWeight: 600,
};
const cancelBtnStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const saveBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

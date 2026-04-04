import { useState } from 'react';
import { updateTestCase } from '../api/modules';
import type { ApiTestCase } from '../types';

interface Props {
  open: boolean;
  testCase: ApiTestCase;
  taskId: string;
  caseIndex: number;
  moduleId: string;
  testSetId: string;
  onClose: () => void;
  onSaved: () => void;
}

export function EditTestCaseDialog({
  open, testCase, taskId, caseIndex, moduleId, testSetId, onClose, onSaved,
}: Props) {
  const [form, setForm] = useState<ApiTestCase>(() => structuredClone(testCase));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) return null;

  const set = <K extends keyof ApiTestCase>(key: K, value: ApiTestCase[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await updateTestCase(moduleId, testSetId, taskId, caseIndex, form);
      onSaved();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 20px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          Edit Test Case
        </h2>

        <div style={{ maxHeight: 'calc(80vh - 120px)', overflowY: 'auto', paddingRight: 8 }}>
          {/* Name */}
          <label style={labelStyle}>Name</label>
          <input style={inputStyle} value={form.name} onChange={e => set('name', e.target.value)} />

          {/* Method + Endpoint row */}
          <div style={{ display: 'flex', gap: 12, marginTop: 14 }}>
            <div style={{ width: 120 }}>
              <label style={labelStyle}>Method</label>
              <select style={{ ...inputStyle, cursor: 'pointer' }} value={form.method}
                onChange={e => set('method', e.target.value)}>
                {['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map(m =>
                  <option key={m} value={m}>{m}</option>
                )}
              </select>
            </div>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Endpoint</label>
              <input style={inputStyle} value={form.endpoint} onChange={e => set('endpoint', e.target.value)} />
            </div>
          </div>

          {/* Expected Status */}
          <div style={{ marginTop: 14, width: 160 }}>
            <label style={labelStyle}>Expected Status</label>
            <input type="number" style={inputStyle} value={form.expectedStatus}
              onChange={e => set('expectedStatus', parseInt(e.target.value) || 0)} />
          </div>

          {/* Headers */}
          <KeyValueEditor label="Headers" value={form.headers}
            onChange={v => set('headers', v)} />

          {/* Query Params */}
          <KeyValueEditor label="Query Params" value={form.queryParams}
            onChange={v => set('queryParams', v)} />

          {/* Body */}
          <div style={{ marginTop: 14 }}>
            <label style={labelStyle}>Body <span style={{ fontWeight: 400, color: '#94a3b8' }}>(JSON)</span></label>
            <textarea style={{ ...inputStyle, minHeight: 80, resize: 'vertical', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 }}
              value={form.body != null ? JSON.stringify(form.body, null, 2) : ''}
              onChange={e => {
                const v = e.target.value.trim();
                if (!v) { set('body', null); return; }
                try { set('body', JSON.parse(v)); } catch { /* let user keep typing */ }
              }}
            />
          </div>

          {/* Expected Body Contains */}
          <TagListEditor label="Expected Body Contains" value={form.expectedBodyContains}
            onChange={v => set('expectedBodyContains', v)} />

          {/* Expected Body Not Contains */}
          <TagListEditor label="Expected Body NOT Contains" value={form.expectedBodyNotContains}
            onChange={v => set('expectedBodyNotContains', v)} />

          {/* Fuzz test */}
          <label style={{ ...labelStyle, marginTop: 14, display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" checked={form.isFuzzTest}
              onChange={e => set('isFuzzTest', e.target.checked)} />
            Fuzz Test
          </label>
        </div>

        {error && <p style={errorStyle}>{error}</p>}

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
          <button onClick={onClose} style={cancelBtnStyle} disabled={saving}>Cancel</button>
          <button onClick={handleSave} style={saveBtnStyle} disabled={saving}>
            {saving ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Key-value pair editor ──
function KeyValueEditor({ label, value, onChange }: {
  label: string;
  value: Record<string, string>;
  onChange: (v: Record<string, string>) => void;
}) {
  const entries = Object.entries(value);

  const update = (oldKey: string, newKey: string, newVal: string) => {
    const next = { ...value };
    if (oldKey !== newKey) delete next[oldKey];
    next[newKey] = newVal;
    onChange(next);
  };

  const remove = (key: string) => {
    const next = { ...value };
    delete next[key];
    onChange(next);
  };

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

// ── Tag list editor ──
function TagListEditor({ label, value, onChange }: {
  label: string;
  value: string[];
  onChange: (v: string[]) => void;
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

// ── Styles ──
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
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 13, marginTop: 12, padding: '6px 12px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};
const cancelBtnStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const saveBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

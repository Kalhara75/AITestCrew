import { useState } from 'react';
import { updateSetupSteps, clearSetupSteps } from '../api/modules';
import type { WebUiStep } from '../types';

const ACTIONS = [
  'navigate', 'click', 'fill', 'select', 'check', 'uncheck', 'hover', 'press',
  'assert-url-contains', 'assert-title-contains', 'assert-text',
  'assert-visible', 'assert-hidden', 'wait',
];

const NO_SELECTOR = new Set(['navigate', 'assert-url-contains', 'assert-title-contains', 'wait']);

interface Props {
  setupStartUrl: string;
  setupSteps: WebUiStep[];
  moduleId: string;
  testSetId: string;
  onUpdated: () => void;
}

export function SetupStepsPanel({ setupStartUrl, setupSteps, moduleId, testSetId, onUpdated }: Props) {
  const [expanded, setExpanded] = useState(setupSteps.length > 0);
  const [editing, setEditing] = useState(false);
  const [startUrl, setStartUrl] = useState(setupStartUrl);
  const [steps, setSteps] = useState<WebUiStep[]>(() => structuredClone(setupSteps));
  const [saving, setSaving] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [confirmClear, setConfirmClear] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const hasSteps = setupSteps.length > 0;

  const resetForm = () => {
    setStartUrl(setupStartUrl);
    setSteps(structuredClone(setupSteps));
    setError(null);
  };

  const handleEdit = () => {
    resetForm();
    if (steps.length === 0) {
      setSteps([{ action: 'navigate', selector: null, value: '/', timeoutMs: 5000 }]);
    }
    setEditing(true);
    setExpanded(true);
  };

  const handleCancel = () => {
    resetForm();
    setEditing(false);
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await updateSetupSteps(moduleId, testSetId, startUrl, steps);
      setEditing(false);
      onUpdated();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleClear = async () => {
    setClearing(true);
    setError(null);
    try {
      await clearSetupSteps(moduleId, testSetId);
      setEditing(false);
      setConfirmClear(false);
      onUpdated();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Clear failed');
    } finally {
      setClearing(false);
    }
  };

  const setStep = (index: number, updated: WebUiStep) =>
    setSteps(prev => {
      const next = [...prev];
      next[index] = updated;
      return next;
    });

  const addStep = () =>
    setSteps(prev => [...prev, { action: 'click', selector: null, value: null, timeoutMs: 5000 }]);

  const removeStep = (index: number) =>
    setSteps(prev => prev.filter((_, i) => i !== index));

  const moveStep = (index: number, direction: -1 | 1) => {
    const next = index + direction;
    if (next < 0 || next >= steps.length) return;
    setSteps(prev => {
      const arr = [...prev];
      [arr[index], arr[next]] = [arr[next], arr[index]];
      return arr;
    });
  };

  return (
    <div style={panelStyle}>
      {/* Header */}
      <div
        style={headerStyle}
        onClick={() => setExpanded(!expanded)}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 12, color: '#64748b', transform: expanded ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>
            &#9654;
          </span>
          <span style={{ fontWeight: 600, fontSize: 14, color: '#0f172a' }}>
            Setup Steps
          </span>
          {hasSteps ? (
            <span style={badgeStyle}>
              {setupSteps.length} step{setupSteps.length !== 1 ? 's' : ''}
            </span>
          ) : (
            <span style={{ fontSize: 12, color: '#94a3b8' }}>not configured</span>
          )}
        </div>
        {!editing && (
          <button
            onClick={e => { e.stopPropagation(); handleEdit(); }}
            style={editBtnStyle}
          >
            {hasSteps ? 'Edit' : '+ Add Setup Steps'}
          </button>
        )}
      </div>

      {/* Body */}
      {expanded && (
        <div style={{ padding: '0 16px 16px' }}>
          {!editing && !hasSteps && (
            <div style={{ padding: '12px 0', color: '#64748b', fontSize: 13 }}>
              <p style={{ margin: '0 0 6px' }}>
                Setup steps (e.g. login) run automatically before every test case in this test set.
              </p>
              <p style={{ margin: 0, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, color: '#94a3b8' }}>
                Record via CLI: dotnet run -- --record-setup --module {moduleId} --testset {testSetId}
              </p>
            </div>
          )}

          {!editing && hasSteps && (
            <>
              {setupStartUrl && (
                <p style={{ margin: '8px 0', fontSize: 13, color: '#475569' }}>
                  <span style={{ fontWeight: 600, marginRight: 4 }}>Start URL:</span>
                  <span style={{ fontFamily: 'ui-monospace, Consolas, monospace', color: '#334155' }}>{setupStartUrl}</span>
                </p>
              )}
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13, marginTop: 8 }}>
                <thead>
                  <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
                    <th style={thStyle}>#</th>
                    <th style={thStyle}>Action</th>
                    <th style={thStyle}>Selector</th>
                    <th style={thStyle}>Value</th>
                  </tr>
                </thead>
                <tbody>
                  {setupSteps.map((s, i) => (
                    <tr key={i} style={{ borderBottom: '1px solid #f1f5f9' }}>
                      <td style={tdStyle}>{i + 1}</td>
                      <td style={{ ...tdStyle, fontWeight: 500 }}>{s.action}</td>
                      <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, color: '#334155' }}>
                        {s.selector || '-'}
                      </td>
                      <td style={{ ...tdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, color: '#334155' }}>
                        {s.action === 'fill' && s.value ? '••••••••' : (s.value || '-')}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}

          {editing && (
            <div style={{ marginTop: 8 }}>
              {/* Start URL */}
              <label style={labelStyle}>Setup Start URL</label>
              <input
                style={inputStyle}
                value={startUrl}
                placeholder="/ (login page URL)"
                onChange={e => setStartUrl(e.target.value)}
              />

              {/* Steps */}
              <div style={{ marginTop: 16, marginBottom: 6 }}>
                <span style={{ fontWeight: 600, fontSize: 13, color: '#475569' }}>
                  Steps ({steps.length})
                </span>
              </div>

              {steps.map((step, i) => (
                <div key={i} style={stepRowStyle}>
                  <span style={{ width: 24, textAlign: 'right', color: '#94a3b8', fontSize: 12, flexShrink: 0 }}>
                    {i + 1}
                  </span>
                  <select
                    value={step.action}
                    onChange={e => setStep(i, { ...step, action: e.target.value,
                      selector: NO_SELECTOR.has(e.target.value) ? null : step.selector })}
                    style={{ ...inputStyle, width: 180, flexShrink: 0 }}
                  >
                    {ACTIONS.map(a => <option key={a} value={a}>{a}</option>)}
                  </select>
                  <input
                    placeholder="selector (CSS)"
                    value={step.selector ?? ''}
                    disabled={NO_SELECTOR.has(step.action)}
                    onChange={e => setStep(i, { ...step, selector: e.target.value || null })}
                    style={{
                      ...inputStyle, flex: 1, minWidth: 0,
                      background: NO_SELECTOR.has(step.action) ? '#f8fafc' : undefined,
                      color: NO_SELECTOR.has(step.action) ? '#cbd5e1' : undefined,
                    }}
                  />
                  <input
                    placeholder="value"
                    value={step.value ?? ''}
                    onChange={e => setStep(i, { ...step, value: e.target.value || null })}
                    style={{ ...inputStyle, flex: 1, minWidth: 0 }}
                  />
                  <input
                    type="number"
                    value={step.timeoutMs}
                    onChange={e => setStep(i, { ...step, timeoutMs: parseInt(e.target.value) || 5000 })}
                    style={{ ...inputStyle, width: 80, flexShrink: 0 }}
                    title="Timeout (ms)"
                  />
                  <div style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                    <button onClick={() => moveStep(i, -1)} disabled={i === 0} style={iconBtnStyle} title="Move up">&#8593;</button>
                    <button onClick={() => moveStep(i, 1)} disabled={i === steps.length - 1} style={iconBtnStyle} title="Move down">&#8595;</button>
                    <button onClick={() => removeStep(i)} style={{ ...iconBtnStyle, color: '#ef4444' }} title="Delete step">&#10005;</button>
                  </div>
                </div>
              ))}

              <button onClick={addStep} style={addStepBtnStyle}>+ Add Step</button>

              {error && (
                <p style={{ color: '#dc2626', fontSize: 13, margin: '10px 0 0' }}>{error}</p>
              )}

              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 16 }}>
                <div>
                  {hasSteps && !confirmClear && (
                    <button onClick={() => setConfirmClear(true)} style={clearBtnStyle}>
                      Clear Setup Steps
                    </button>
                  )}
                  {confirmClear && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <span style={{ fontSize: 13, color: '#dc2626' }}>Remove all setup steps?</span>
                      <button onClick={handleClear} disabled={clearing} style={clearBtnStyle}>
                        {clearing ? 'Clearing...' : 'Yes, Clear'}
                      </button>
                      <button onClick={() => setConfirmClear(false)} style={cancelBtnStyle}>No</button>
                    </div>
                  )}
                </div>
                <div style={{ display: 'flex', gap: 10 }}>
                  <button onClick={handleCancel} style={cancelBtnStyle}>Cancel</button>
                  <button onClick={handleSave} disabled={saving || steps.length === 0} style={saveBtnStyle}>
                    {saving ? 'Saving...' : 'Save'}
                  </button>
                </div>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── Styles ──

const panelStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0',
  marginBottom: 20,
};

const headerStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between', alignItems: 'center',
  padding: '12px 16px', cursor: 'pointer', userSelect: 'none',
};

const badgeStyle: React.CSSProperties = {
  background: '#dbeafe', color: '#1d4ed8', fontSize: 11, fontWeight: 600,
  padding: '2px 8px', borderRadius: 10,
};

const editBtnStyle: React.CSSProperties = {
  padding: '5px 14px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#475569',
};

const thStyle: React.CSSProperties = {
  padding: '6px 10px', fontSize: 12, fontWeight: 600, color: '#64748b',
  textTransform: 'uppercase', letterSpacing: 0.4,
};

const tdStyle: React.CSSProperties = {
  padding: '8px 10px',
};

const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 12, fontWeight: 600,
  color: '#64748b', textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 4,
};

const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', border: '1px solid #e2e8f0',
  borderRadius: 6, fontSize: 13, outline: 'none', boxSizing: 'border-box',
};

const stepRowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8,
  padding: '6px 8px', marginBottom: 4, background: '#f8fafc',
  borderRadius: 6, border: '1px solid #e2e8f0',
};

const iconBtnStyle: React.CSSProperties = {
  background: 'none', border: '1px solid #e2e8f0', borderRadius: 4,
  padding: '2px 6px', cursor: 'pointer', fontSize: 12, color: '#64748b',
};

const addStepBtnStyle: React.CSSProperties = {
  marginTop: 8, padding: '6px 14px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#475569', width: '100%',
};

const cancelBtnStyle: React.CSSProperties = {
  padding: '8px 20px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 14, color: '#475569',
};

const clearBtnStyle: React.CSSProperties = {
  padding: '8px 18px', background: '#fef2f2', border: '1px solid #fecaca',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#dc2626', fontWeight: 600,
};

const saveBtnStyle: React.CSSProperties = {
  padding: '8px 20px', background: '#1d4ed8', border: 'none',
  borderRadius: 6, cursor: 'pointer', fontSize: 14, color: '#fff', fontWeight: 600,
};

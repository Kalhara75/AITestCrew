import { useState } from 'react';
import { updateTeardownSteps, clearTeardownSteps } from '../api/modules';
import type { SqlTeardownStep } from '../types';

interface Props {
  teardownSteps: SqlTeardownStep[];
  moduleId: string;
  testSetId: string;
  environmentKey?: string | null;
  dataTeardownEnabled: boolean;
  onUpdated: () => void;
}

/**
 * Panel for editing per-test-set SQL teardown statements. Runs once per
 * objective before agent dispatch; gated per-env by `DataTeardownEnabled`.
 * Mirrors SetupStepsPanel for a consistent editor experience.
 */
export function TeardownStepsPanel({
  teardownSteps, moduleId, testSetId, environmentKey, dataTeardownEnabled, onUpdated,
}: Props) {
  const [expanded, setExpanded] = useState(teardownSteps.length > 0);
  const [editing, setEditing] = useState(false);
  const [steps, setSteps] = useState<SqlTeardownStep[]>(() => structuredClone(teardownSteps));
  const [saving, setSaving] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [confirmClear, setConfirmClear] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const hasSteps = teardownSteps.length > 0;

  const resetForm = () => {
    setSteps(structuredClone(teardownSteps));
    setError(null);
  };

  const handleEdit = () => {
    resetForm();
    if (steps.length === 0) {
      setSteps([{ name: 'Clear test data', sql: "DELETE FROM <table> WHERE <column> = '{{Token}}'" }]);
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
      await updateTeardownSteps(moduleId, testSetId, steps);
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
      await clearTeardownSteps(moduleId, testSetId);
      setEditing(false);
      setConfirmClear(false);
      onUpdated();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Clear failed');
    } finally {
      setClearing(false);
    }
  };

  const setStep = (index: number, updated: SqlTeardownStep) =>
    setSteps(prev => {
      const next = [...prev];
      next[index] = updated;
      return next;
    });

  const addStep = () =>
    setSteps(prev => [...prev, { name: '', sql: '' }]);

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
      <div style={headerStyle} onClick={() => setExpanded(!expanded)}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 12, color: '#64748b', transform: expanded ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>
            &#9654;
          </span>
          <span style={{ fontWeight: 600, fontSize: 14, color: '#0f172a' }}>
            Data Teardown (SQL)
          </span>
          {hasSteps ? (
            <span style={badgeStyle}>
              {teardownSteps.length} step{teardownSteps.length !== 1 ? 's' : ''}
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
            {hasSteps ? 'Edit' : '+ Add Teardown SQL'}
          </button>
        )}
      </div>

      {expanded && (
        <div style={{ padding: '0 16px 16px' }}>
          {!dataTeardownEnabled && (
            <div style={warningStyle}>
              <strong>Teardown is disabled for this environment</strong>
              {environmentKey ? <> (<code>{environmentKey}</code>)</> : null}.
              {' '}Runs against this env will fail teardown fast until
              <code> DataTeardownEnabled</code> is set to <code>true</code> under
              <code> TestEnvironment.Environments.&lt;env&gt;</code> in <code>appsettings.json</code>.
              You can still edit teardown SQL here to prepare ahead of enabling it.
            </div>
          )}

          {!editing && !hasSteps && (
            <div style={{ padding: '12px 0', color: '#64748b', fontSize: 13 }}>
              <p style={{ margin: '0 0 6px' }}>
                Teardown SQL runs ONCE per objective, immediately before the agent dispatches,
                to clear server-side state written by prior runs (e.g. meter reads from an MDN delivery).
              </p>
              <p style={{ margin: 0, fontSize: 12, color: '#94a3b8' }}>
                Tokens like <code>{'{{NMI}}'}</code> resolve from the objective's environment parameters
                and the first delivery step's field values.
                SQL must contain <code>WHERE</code>; <code>DROP/TRUNCATE/ALTER/etc.</code> are rejected.
              </p>
            </div>
          )}

          {!editing && hasSteps && (
            <div style={{ marginTop: 8 }}>
              {teardownSteps.map((s, i) => (
                <div key={i} style={stepPreviewStyle}>
                  <div style={{ fontWeight: 600, fontSize: 13, color: '#334155', marginBottom: 4 }}>
                    {i + 1}. {s.name || <span style={{ color: '#94a3b8', fontStyle: 'italic' }}>(unnamed)</span>}
                  </div>
                  <pre style={sqlPreviewStyle}>{s.sql}</pre>
                </div>
              ))}
            </div>
          )}

          {editing && (
            <div style={{ marginTop: 8 }}>
              <div style={{ marginTop: 8, marginBottom: 6 }}>
                <span style={{ fontWeight: 600, fontSize: 13, color: '#475569' }}>
                  Steps ({steps.length})
                </span>
              </div>

              {steps.map((step, i) => (
                <div key={i} style={stepEditStyle}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
                    <span style={{ width: 24, textAlign: 'right', color: '#94a3b8', fontSize: 12, flexShrink: 0 }}>
                      {i + 1}
                    </span>
                    <input
                      placeholder="Step name (e.g. Clear MDM reads)"
                      value={step.name}
                      onChange={e => setStep(i, { ...step, name: e.target.value })}
                      style={{ ...inputStyle, flex: 1, minWidth: 0 }}
                    />
                    <div style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                      <button onClick={() => moveStep(i, -1)} disabled={i === 0} style={iconBtnStyle} title="Move up">&#8593;</button>
                      <button onClick={() => moveStep(i, 1)} disabled={i === steps.length - 1} style={iconBtnStyle} title="Move down">&#8595;</button>
                      <button onClick={() => removeStep(i)} style={{ ...iconBtnStyle, color: '#ef4444' }} title="Delete step">&#10005;</button>
                    </div>
                  </div>
                  <textarea
                    placeholder="DELETE FROM bra.T_MDM_MeterRead WHERE NMI='{{NMI}}' AND ReadDate='{{ReadDate}}'"
                    value={step.sql}
                    onChange={e => setStep(i, { ...step, sql: e.target.value })}
                    rows={4}
                    style={sqlInputStyle}
                    spellCheck={false}
                  />
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
                      Clear Teardown SQL
                    </button>
                  )}
                  {confirmClear && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <span style={{ fontSize: 13, color: '#dc2626' }}>Remove all teardown steps?</span>
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

const panelStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0',
  marginBottom: 20,
};

const headerStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between', alignItems: 'center',
  padding: '12px 16px', cursor: 'pointer', userSelect: 'none',
};

const badgeStyle: React.CSSProperties = {
  background: '#fef3c7', color: '#92400e', fontSize: 11, fontWeight: 600,
  padding: '2px 8px', borderRadius: 10,
};

const warningStyle: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fcd34d', color: '#92400e',
  padding: '10px 12px', borderRadius: 6, fontSize: 13, margin: '12px 0',
};

const editBtnStyle: React.CSSProperties = {
  padding: '5px 14px', background: '#f1f5f9', border: '1px solid #e2e8f0',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#475569',
};

const inputStyle: React.CSSProperties = {
  padding: '7px 10px', border: '1px solid #e2e8f0',
  borderRadius: 6, fontSize: 13, outline: 'none', boxSizing: 'border-box',
};

const sqlInputStyle: React.CSSProperties = {
  ...inputStyle,
  width: '100%',
  fontFamily: 'ui-monospace, Consolas, monospace',
  fontSize: 12,
  lineHeight: 1.5,
  resize: 'vertical',
};

const stepPreviewStyle: React.CSSProperties = {
  padding: '10px 12px', marginBottom: 8,
  background: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: 6,
};

const sqlPreviewStyle: React.CSSProperties = {
  margin: 0, padding: 8,
  background: '#0f172a', color: '#e2e8f0',
  borderRadius: 4,
  fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12,
  whiteSpace: 'pre-wrap', wordBreak: 'break-word',
};

const stepEditStyle: React.CSSProperties = {
  padding: 10, marginBottom: 8,
  background: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: 6,
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

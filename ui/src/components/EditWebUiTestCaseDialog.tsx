import { useState } from 'react';
import type { WebUiTestDefinition, WebUiStep } from '../types';

const ACTIONS = [
  'navigate', 'click', 'fill', 'select', 'check', 'uncheck', 'hover', 'press',
  'assert-url-contains', 'assert-title-contains', 'assert-text',
  'assert-visible', 'assert-hidden', 'wait',
];

// Actions where selector is not applicable
const NO_SELECTOR = new Set(['navigate', 'assert-url-contains', 'assert-title-contains', 'wait']);

// Actions where "match first" is applicable (assertions that target an element locator)
const MATCH_FIRST_APPLICABLE = new Set(['assert-text', 'assert-visible', 'assert-hidden']);

export interface EditWebUiSavePayload {
  /** The (possibly edited) display name / case name. */
  name: string;
  /** The full WebUiTestDefinition with possibly-modified steps. */
  definition: WebUiTestDefinition;
}

interface Props {
  open: boolean;
  /** Title shown at the top of the dialog. Defaults to "Edit Web UI Test Case". */
  title?: string;
  /** Initial definition shown in the form (cloned on mount). */
  definition: WebUiTestDefinition;
  /** Initial display name shown above Description. Editable; passed back to onSave. */
  caseName: string;
  onClose: () => void;
  onSave: (payload: EditWebUiSavePayload) => Promise<void>;
  /** Optional. When present, the bottom-left "Delete" button appears and calls this on confirm. */
  onDelete?: () => Promise<void>;
  /** Override the delete button label. Defaults to "Delete Step". */
  deleteLabel?: string;
  /** Override the confirmation message text. */
  deleteConfirmMessage?: string;
}

/**
 * Generic Web UI test-case / step-set editor.
 *
 * Originally introduced for individual web UI test cases (`WebUiTestCaseTable`)
 * and now reused by aseXML post-delivery verifications. The dialog is data-
 * shape-agnostic: callers pass a `WebUiTestDefinition` plus save/delete
 * callbacks describing how to persist the edits in their own context.
 *
 * For verifications: the same shape (`WebUiTestDefinition`) is used inside
 * `VerificationStep.WebUi`, so editing flows through the verification update
 * endpoint instead of the objective update endpoint.
 */
export function EditWebUiTestCaseDialog({
  open, title, definition, caseName, onClose, onSave, onDelete,
  deleteLabel, deleteConfirmMessage,
}: Props) {
  const [form, setForm] = useState<WebUiTestDefinition>(() => structuredClone(definition));
  const [name, setName] = useState(caseName);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) return null;

  const setField = <K extends keyof WebUiTestDefinition>(key: K, value: WebUiTestDefinition[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const setStep = (index: number, updated: WebUiStep) =>
    setForm(prev => {
      const steps = [...prev.steps];
      steps[index] = updated;
      return { ...prev, steps };
    });

  const addStep = () =>
    setForm(prev => ({
      ...prev,
      steps: [...prev.steps, { action: 'click', selector: null, value: null, timeoutMs: 5000 }],
    }));

  const removeStep = (index: number) =>
    setForm(prev => ({ ...prev, steps: prev.steps.filter((_, i) => i !== index) }));

  const moveStep = (index: number, direction: -1 | 1) => {
    const next = index + direction;
    if (next < 0 || next >= form.steps.length) return;
    setForm(prev => {
      const steps = [...prev.steps];
      [steps[index], steps[next]] = [steps[next], steps[index]];
      return { ...prev, steps };
    });
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await onSave({ name, definition: form });
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
          {title ?? 'Edit Web UI Test Case'}
        </h2>

        <div style={{ maxHeight: 'calc(80vh - 130px)', overflowY: 'auto', paddingRight: 6 }}>

          {/* Name */}
          <label style={labelStyle}>Name</label>
          <input style={inputStyle} value={name}
            onChange={e => setName(e.target.value)} />

          {/* Description */}
          <label style={{ ...labelStyle, marginTop: 12 }}>Description</label>
          <input style={inputStyle} value={form.description}
            onChange={e => setField('description', e.target.value)} />

          {/* Start URL */}
          <label style={{ ...labelStyle, marginTop: 12 }}>Start URL</label>
          <input style={inputStyle} value={form.startUrl} placeholder="/"
            onChange={e => setField('startUrl', e.target.value)} />

          {/* Screenshot on failure */}
          <label style={{ ...labelStyle, marginTop: 12, display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" checked={form.takeScreenshotOnFailure}
              onChange={e => setField('takeScreenshotOnFailure', e.target.checked)} />
            Take screenshot on failure
          </label>

          {/* Steps */}
          <div style={{ marginTop: 20, marginBottom: 6 }}>
            <span style={{ fontWeight: 600, fontSize: 13, color: '#475569' }}>
              Steps ({form.steps.length})
            </span>
          </div>

          {form.steps.map((step, i) => (
            <div key={i} style={stepRowStyle}>
              {/* Step number */}
              <span style={{ width: 24, textAlign: 'right', color: '#94a3b8', fontSize: 12, flexShrink: 0 }}>
                {i + 1}
              </span>

              {/* Action */}
              <select
                value={step.action}
                onChange={e => setStep(i, { ...step, action: e.target.value,
                  selector: NO_SELECTOR.has(e.target.value) ? null : step.selector })}
                style={{ ...inputStyle, width: 180, flexShrink: 0 }}
              >
                {ACTIONS.map(a => <option key={a} value={a}>{a}</option>)}
              </select>

              {/* Selector */}
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

              {/* Value */}
              <input
                placeholder="value"
                value={step.value ?? ''}
                onChange={e => setStep(i, { ...step, value: e.target.value || null })}
                style={{ ...inputStyle, flex: 1, minWidth: 0 }}
              />

              {/* Timeout */}
              <input
                type="number"
                value={step.timeoutMs}
                onChange={e => setStep(i, { ...step, timeoutMs: parseInt(e.target.value) || 5000 })}
                style={{ ...inputStyle, width: 80, flexShrink: 0 }}
                title="Timeout (ms)"
              />

              {/* Match first (assertions only) */}
              {MATCH_FIRST_APPLICABLE.has(step.action) && (
                <label
                  style={{
                    display: 'inline-flex', alignItems: 'center', gap: 4,
                    fontSize: 11, color: '#64748b', flexShrink: 0, whiteSpace: 'nowrap',
                    cursor: 'pointer',
                  }}
                  title="When the selector matches multiple elements (e.g. grid rows), assert only against the first match"
                >
                  <input
                    type="checkbox"
                    checked={!!step.matchFirst}
                    onChange={e => setStep(i, { ...step, matchFirst: e.target.checked })}
                    style={{ margin: 0 }}
                  />
                  first
                </label>
              )}

              {/* Reorder + Delete */}
              <div style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                <button onClick={() => moveStep(i, -1)} disabled={i === 0} style={iconBtnStyle} title="Move up">↑</button>
                <button onClick={() => moveStep(i, 1)} disabled={i === form.steps.length - 1} style={iconBtnStyle} title="Move down">↓</button>
                <button onClick={() => removeStep(i)} style={{ ...iconBtnStyle, color: '#ef4444' }} title="Delete step">✕</button>
              </div>
            </div>
          ))}

          <button onClick={addStep} style={addStepBtnStyle}>
            + Add Step
          </button>
        </div>

        {error && (
          <p style={{ color: '#dc2626', fontSize: 13, margin: '10px 0 0' }}>{error}</p>
        )}

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
                  {deleting ? 'Deleting...' : 'Yes, delete'}
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

// ── Styles ─────────────────────────────────────────────────────────────────────

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.45)',
  display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};

const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28,
  width: '90vw', maxWidth: 960, boxShadow: '0 20px 60px rgba(0,0,0,0.2)',
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

const deleteBtnStyle: React.CSSProperties = {
  padding: '8px 18px', background: '#fef2f2', border: '1px solid #fecaca',
  borderRadius: 6, cursor: 'pointer', fontSize: 13, color: '#dc2626', fontWeight: 600,
};

const saveBtnStyle: React.CSSProperties = {
  padding: '8px 20px', background: '#1d4ed8', border: 'none',
  borderRadius: 6, cursor: 'pointer', fontSize: 14, color: '#fff', fontWeight: 600,
};

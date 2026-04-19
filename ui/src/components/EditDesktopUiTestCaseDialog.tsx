import { useState } from 'react';
import type { DesktopUiTestDefinition, DesktopUiStep } from '../types';

const ACTIONS = [
  'click', 'double-click', 'right-click', 'fill', 'select',
  'check', 'uncheck', 'press', 'hover',
  'assert-text', 'assert-visible', 'assert-hidden', 'assert-enabled', 'assert-disabled',
  'wait-for-window', 'switch-window', 'close-window', 'menu-navigate', 'wait',
];

// Actions where element selectors are not applicable
const NO_ELEMENT = new Set(['press', 'wait-for-window', 'switch-window', 'close-window', 'menu-navigate']);
// Actions that use the menuPath field
const USES_MENU_PATH = new Set(['menu-navigate']);
// Actions that use the windowTitle field
const USES_WINDOW_TITLE = new Set(['wait-for-window', 'switch-window', 'close-window']);

function emptyStep(): DesktopUiStep {
  return {
    action: 'click', automationId: null, name: null, className: null,
    controlType: null, treePath: null, value: null, menuPath: null,
    windowTitle: null, timeoutMs: 5000,
  };
}

export interface EditDesktopUiSavePayload {
  /** The (possibly edited) display name / case name. */
  name: string;
  /** The full DesktopUiTestDefinition with possibly-modified steps. */
  definition: DesktopUiTestDefinition;
}

interface Props {
  open: boolean;
  /** Title shown at the top of the dialog. Defaults to "Edit Desktop UI Test Case". */
  title?: string;
  /** Initial definition shown in the form (cloned on mount). */
  definition: DesktopUiTestDefinition;
  /** Initial display name shown above Description. Editable; passed back to onSave. */
  caseName: string;
  onClose: () => void;
  onSave: (payload: EditDesktopUiSavePayload) => Promise<void>;
  /** Optional. When present, the bottom-left "Delete" button appears and calls this on confirm. */
  onDelete?: () => Promise<void>;
  /** Override the delete button label. Defaults to "Delete Step". */
  deleteLabel?: string;
  /** Override the confirmation message text. Defaults to "Delete this step?". */
  deleteConfirmMessage?: string;
}

/**
 * Generic Desktop UI test-case / step-set editor.
 *
 * Mirrors EditWebUiTestCaseDialog: data-shape-agnostic — callers pass a
 * DesktopUiTestDefinition plus save/delete callbacks describing how to persist
 * the edits in their own context (standalone objective, aseXML post-delivery
 * verification, etc.). The dialog never calls the persistence API directly.
 */
export function EditDesktopUiTestCaseDialog({
  open, title, definition, caseName, onClose, onSave, onDelete,
  deleteLabel, deleteConfirmMessage,
}: Props) {
  const [form, setForm] = useState<DesktopUiTestDefinition>(() => structuredClone(definition));
  const [name, setName] = useState(caseName);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!open) return null;

  const setField = <K extends keyof DesktopUiTestDefinition>(key: K, value: DesktopUiTestDefinition[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const setStep = (index: number, updated: DesktopUiStep) =>
    setForm(prev => {
      const steps = [...prev.steps];
      steps[index] = updated;
      return { ...prev, steps };
    });

  const addStep = () =>
    setForm(prev => ({ ...prev, steps: [...prev.steps, emptyStep()] }));

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
          {title ?? 'Edit Desktop UI Test Case'}
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

          {form.steps.map((s, i) => {
            const noElement = NO_ELEMENT.has(s.action);
            const showMenu = USES_MENU_PATH.has(s.action);
            const showWindow = USES_WINDOW_TITLE.has(s.action);

            return (
              <div key={i} style={stepBlockStyle}>
                {/* Row 1: step number, action, value, timeout, controls */}
                <div style={stepRowStyle}>
                  <span style={{ width: 24, textAlign: 'right', color: '#94a3b8', fontSize: 12, flexShrink: 0 }}>
                    {i + 1}
                  </span>

                  {/* Action */}
                  <select
                    value={s.action}
                    onChange={e => setStep(i, { ...s, action: e.target.value })}
                    style={{ ...inputStyle, width: 170, flexShrink: 0 }}
                  >
                    {ACTIONS.map(a => <option key={a} value={a}>{a}</option>)}
                  </select>

                  {/* Value */}
                  <input
                    placeholder="value"
                    value={s.value ?? ''}
                    onChange={e => setStep(i, { ...s, value: e.target.value || null })}
                    style={{ ...inputStyle, flex: 1, minWidth: 0 }}
                  />

                  {/* Timeout */}
                  <input
                    type="number"
                    value={s.timeoutMs}
                    onChange={e => setStep(i, { ...s, timeoutMs: parseInt(e.target.value) || 5000 })}
                    style={{ ...inputStyle, width: 72, flexShrink: 0 }}
                    title="Timeout (ms)"
                  />

                  {/* Reorder + Delete */}
                  <div style={{ display: 'flex', gap: 2, flexShrink: 0 }}>
                    <button onClick={() => moveStep(i, -1)} disabled={i === 0} style={iconBtnStyle} title="Move up">&#8593;</button>
                    <button onClick={() => moveStep(i, 1)} disabled={i === form.steps.length - 1} style={iconBtnStyle} title="Move down">&#8595;</button>
                    <button onClick={() => removeStep(i)} style={{ ...iconBtnStyle, color: '#ef4444' }} title="Delete step">&#10005;</button>
                  </div>
                </div>

                {/* Row 2: Element selectors (hidden for non-element actions) */}
                {!noElement && (
                  <div style={{ ...stepRowStyle, paddingLeft: 34, gap: 6 }}>
                    <input placeholder="AutomationId" value={s.automationId ?? ''}
                      onChange={e => setStep(i, { ...s, automationId: e.target.value || null })}
                      style={{ ...inputStyle, flex: 1, minWidth: 0 }} title="AutomationId (priority 1)" />
                    <input placeholder="Name" value={s.name ?? ''}
                      onChange={e => setStep(i, { ...s, name: e.target.value || null })}
                      style={{ ...inputStyle, flex: 1, minWidth: 0 }} title="Name (priority 2)" />
                    <input placeholder="ClassName" value={s.className ?? ''}
                      onChange={e => setStep(i, { ...s, className: e.target.value || null })}
                      style={{ ...inputStyle, width: 110, flexShrink: 0 }} title="ClassName (priority 3)" />
                    <input placeholder="ControlType" value={s.controlType ?? ''}
                      onChange={e => setStep(i, { ...s, controlType: e.target.value || null })}
                      style={{ ...inputStyle, width: 100, flexShrink: 0 }} title="ControlType (priority 3)" />
                    <input placeholder="TreePath" value={s.treePath ?? ''}
                      onChange={e => setStep(i, { ...s, treePath: e.target.value || null })}
                      style={{ ...inputStyle, flex: 1, minWidth: 0, color: '#94a3b8' }} title="TreePath fallback (priority 4)" />
                  </div>
                )}

                {/* Row 3: MenuPath (for menu-navigate) */}
                {showMenu && (
                  <div style={{ ...stepRowStyle, paddingLeft: 34 }}>
                    <input placeholder="Menu Path (e.g. File > Save As)" value={s.menuPath ?? ''}
                      onChange={e => setStep(i, { ...s, menuPath: e.target.value || null })}
                      style={{ ...inputStyle, flex: 1 }} />
                  </div>
                )}

                {/* Row 3: WindowTitle (for window actions) */}
                {showWindow && (
                  <div style={{ ...stepRowStyle, paddingLeft: 34 }}>
                    <input placeholder="Window Title (substring match)" value={s.windowTitle ?? ''}
                      onChange={e => setStep(i, { ...s, windowTitle: e.target.value || null })}
                      style={{ ...inputStyle, flex: 1 }} />
                  </div>
                )}
              </div>
            );
          })}

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
              {saving ? 'Saving...' : 'Save'}
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
  width: '90vw', maxWidth: 1060, boxShadow: '0 20px 60px rgba(0,0,0,0.2)',
};

const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 12, fontWeight: 600,
  color: '#64748b', textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 4,
};

const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', border: '1px solid #e2e8f0',
  borderRadius: 6, fontSize: 13, outline: 'none', boxSizing: 'border-box',
};

const stepBlockStyle: React.CSSProperties = {
  marginBottom: 6, background: '#f8fafc', borderRadius: 6, border: '1px solid #e2e8f0',
  padding: '6px 0',
};

const stepRowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8, padding: '3px 8px',
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

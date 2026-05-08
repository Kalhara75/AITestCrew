import { useEffect, useState } from 'react';
import type {
  DbCheckStepDefinition, ColumnAssertion, ColumnCapture, AssertionOperator,
} from '../types';
import { dryRunDbCheck, getDbConnections, type DryRunResponse } from '../api/dbCheck';

const OPERATORS: AssertionOperator[] = [
  'Equals', 'NotEquals', 'Contains', 'NotContains',
  'StartsWith', 'EndsWith', 'Regex',
  'GreaterThan', 'LessThan', 'Between',
  'IsNull', 'IsNotNull',
  'EqualsNumeric', 'EqualsDate',
];

type Mode = 'rowCount' | 'columnAssertions';

export interface EditDbCheckSavePayload {
  name: string;
  definition: DbCheckStepDefinition;
}

interface Props {
  open: boolean;
  title?: string;
  definition: DbCheckStepDefinition;
  caseName: string;
  /** Active environment key — used for the connections dropdown + dry-run. */
  envKey?: string | null;
  onClose: () => void;
  onSave: (payload: EditDbCheckSavePayload) => Promise<void>;
  onDelete?: () => Promise<void>;
  deleteLabel?: string;
  deleteConfirmMessage?: string;
}

/**
 * Editor for a `DbCheckStepDefinition` post-step. Mirrors `EditWebUiTestCaseDialog`'s
 * shape so the existing PostStepsPanel wire-up is symmetric. Three sections:
 *
 *   1. Header — name, connection (dropdown sourced from /api/db-check/connections),
 *      timeout.
 *   2. SQL textarea with {{Token}} highlighting + a "Try query" button that hits
 *      /api/db-check/dry-run and shows columns + first 5 rows. Each cell has a
 *      `+` button that adds an `Equals` assertion for `(column, cellValue)`.
 *   3. Mode toggle (row count | column assertions), assertion table, captures table.
 *
 * The legacy `expectedColumnValues` dict is normalised on mount — defensive only,
 * the backend shim should have done it on deserialise.
 */
export function EditDbCheckStepDialog({
  open, title, definition, caseName, envKey,
  onClose, onSave, onDelete, deleteLabel, deleteConfirmMessage,
}: Props) {
  const [form, setForm] = useState<DbCheckStepDefinition>(() => normaliseDefinition(definition));
  const [name, setName] = useState(caseName);
  const [mode, setMode] = useState<Mode>(() =>
    form.expectedRowCount !== undefined && form.expectedRowCount !== null && form.columnAssertions.length === 0
      ? 'rowCount' : 'columnAssertions'
  );
  const [connections, setConnections] = useState<string[]>([]);
  const [dryRun, setDryRun] = useState<DryRunResponse | null>(null);
  const [dryRunError, setDryRunError] = useState<string | null>(null);
  const [dryRunRunning, setDryRunRunning] = useState(false);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    getDbConnections(envKey ?? null)
      .then(r => setConnections(r.keys))
      .catch(() => setConnections(['BravoDb']));
  }, [open, envKey]);

  if (!open) return null;

  const setField = <K extends keyof DbCheckStepDefinition>(key: K, value: DbCheckStepDefinition[K]) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const setAssertion = (idx: number, patch: Partial<ColumnAssertion>) =>
    setForm(prev => {
      const next = [...prev.columnAssertions];
      next[idx] = { ...next[idx], ...patch };
      return { ...prev, columnAssertions: next };
    });

  const addAssertion = (seed?: Partial<ColumnAssertion>) =>
    setForm(prev => ({
      ...prev,
      columnAssertions: [
        ...prev.columnAssertions,
        {
          column: '',
          jsonPath: undefined,
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

  const removeAssertion = (idx: number) =>
    setForm(prev => ({
      ...prev,
      columnAssertions: prev.columnAssertions.filter((_, i) => i !== idx),
    }));

  const setCapture = (idx: number, patch: Partial<ColumnCapture>) =>
    setForm(prev => {
      const next = [...prev.captures];
      next[idx] = { ...next[idx], ...patch };
      return { ...prev, captures: next };
    });

  const addCapture = () =>
    setForm(prev => ({
      ...prev,
      captures: [...prev.captures, { column: '', jsonPath: undefined, as: '', required: true }],
    }));

  const removeCapture = (idx: number) =>
    setForm(prev => ({ ...prev, captures: prev.captures.filter((_, i) => i !== idx) }));

  const handleTryQuery = async () => {
    setDryRunRunning(true);
    setDryRunError(null);
    setDryRun(null);
    try {
      const result = await dryRunDbCheck({
        envKey: envKey ?? null,
        connectionKey: form.connectionKey || 'BravoDb',
        sql: form.sql,
        parameters: {},
      });
      setDryRun(result);
    } catch (err) {
      setDryRunError(err instanceof Error ? err.message : 'Try query failed');
    } finally {
      setDryRunRunning(false);
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      const toSave: DbCheckStepDefinition = {
        ...form,
        // Drop the legacy field — the backend shim already promoted it on the
        // way in; sending it back would duplicate assertions on the server.
        expectedColumnValues: undefined,
        // Mode is mutually exclusive: clear the unused side.
        expectedRowCount: mode === 'rowCount' ? form.expectedRowCount : undefined,
        columnAssertions: mode === 'columnAssertions' ? form.columnAssertions : [],
      };
      await onSave({ name, definition: toSave });
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
          {title ?? 'Edit DB Check Post-Step'}
        </h2>

        <div style={{ maxHeight: 'calc(80vh - 130px)', overflowY: 'auto', paddingRight: 6 }}>
          <label style={labelStyle}>Name</label>
          <input style={inputStyle} value={name} onChange={e => setName(e.target.value)} />

          <div style={{ display: 'flex', gap: 12, marginTop: 12 }}>
            <div style={{ flex: 1 }}>
              <label style={labelStyle}>Connection</label>
              <select style={inputStyle} value={form.connectionKey || 'BravoDb'}
                onChange={e => setField('connectionKey', e.target.value)}>
                {connections.map(k => <option key={k} value={k}>{k}</option>)}
              </select>
            </div>
            <div style={{ width: 120 }}>
              <label style={labelStyle}>Timeout (s)</label>
              <input style={inputStyle} type="number" value={form.timeoutSeconds}
                onChange={e => setField('timeoutSeconds', Number.parseInt(e.target.value, 10) || 15)} />
            </div>
          </div>

          {/* SQL */}
          <label style={{ ...labelStyle, marginTop: 12 }}>SQL (single SELECT, {`{{Token}}`} placeholders OK)</label>
          <textarea
            style={{ ...inputStyle, fontFamily: 'ui-monospace,Consolas,monospace', minHeight: 90, resize: 'vertical' }}
            value={form.sql}
            placeholder="SELECT * FROM Jobs WHERE MessageID = '{{MessageID}}'"
            onChange={e => setField('sql', e.target.value)}
          />

          <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: 8 }}>
            <button onClick={handleTryQuery} disabled={dryRunRunning || !form.sql.trim()} style={tryQueryBtn}>
              {dryRunRunning ? 'Running…' : 'Try query'}
            </button>
            {dryRunError && (
              <span style={{ color: '#dc2626', fontSize: 12 }}>{dryRunError}</span>
            )}
            {dryRun && (
              <span style={{ color: '#475569', fontSize: 12 }}>
                {dryRun.totalRowCount} row{dryRun.totalRowCount === 1 ? '' : 's'}; showing first {dryRun.rows.length}.
              </span>
            )}
          </div>

          {dryRun && dryRun.columns.length > 0 && (
            <div style={{ marginTop: 8, overflowX: 'auto', border: '1px solid #e2e8f0', borderRadius: 6 }}>
              <table style={{ borderCollapse: 'collapse', fontSize: 12, minWidth: '100%' }}>
                <thead>
                  <tr>
                    {dryRun.columns.map(c => (
                      <th key={c.name} style={dryRunTh} title={c.sqlType}>{c.name}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {dryRun.rows.map((row, ri) => (
                    <tr key={ri}>
                      {dryRun.columns.map(c => {
                        const v = row[c.name];
                        const sqlTypeIsJson = /json|nvarchar|text/i.test(c.sqlType);
                        return (
                          <td key={c.name} style={dryRunTd}>
                            <span>{v === null ? <em style={{ color: '#94a3b8' }}>NULL</em> : v}</span>
                            <button
                              style={addAssertionBtn}
                              title={sqlTypeIsJson
                                ? 'Add a JSONPath assertion for this column'
                                : 'Add an Equals assertion for this cell'}
                              onClick={() => {
                                if (mode !== 'columnAssertions') setMode('columnAssertions');
                                if (sqlTypeIsJson) {
                                  const path = window.prompt(
                                    `JSONPath inside ${c.name} (e.g. $.OrderId)`,
                                    '$.');
                                  if (path === null) return;
                                  addAssertion({ column: c.name, jsonPath: path, expected: v ?? '' });
                                } else {
                                  addAssertion({ column: c.name, expected: v ?? '' });
                                }
                              }}
                            >+</button>
                          </td>
                        );
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {/* Mode toggle */}
          <div style={{ marginTop: 16, display: 'flex', gap: 16 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
              <input type="radio" name="mode" checked={mode === 'rowCount'}
                onChange={() => setMode('rowCount')} />
              Assert row count
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
              <input type="radio" name="mode" checked={mode === 'columnAssertions'}
                onChange={() => setMode('columnAssertions')} />
              Assert column values
            </label>
          </div>

          {mode === 'rowCount' && (
            <div style={{ marginTop: 8 }}>
              <label style={labelStyle}>Expected row count</label>
              <input style={{ ...inputStyle, width: 160 }} type="number"
                value={form.expectedRowCount ?? ''}
                placeholder="e.g. 1"
                onChange={e => {
                  const v = e.target.value;
                  setField('expectedRowCount', v === '' ? undefined : Number.parseInt(v, 10));
                }} />
            </div>
          )}

          {mode === 'columnAssertions' && (
            <div style={{ marginTop: 8 }}>
              <div style={{ marginBottom: 6, fontSize: 12, fontWeight: 600, color: '#475569' }}>
                Column assertions ({form.columnAssertions.length})
              </div>
              {form.columnAssertions.map((a, i) => (
                <div key={i} style={assertionRowStyle}>
                  <input placeholder="Column" value={a.column} style={{ ...inputStyle, width: 140 }}
                    onChange={e => setAssertion(i, { column: e.target.value })} />
                  <input placeholder="$.JsonPath (optional)" value={a.jsonPath ?? ''}
                    style={{ ...inputStyle, width: 160, fontFamily: 'ui-monospace,Consolas,monospace' }}
                    onChange={e => setAssertion(i, { jsonPath: e.target.value || undefined })} />
                  <select value={a.operator} style={{ ...inputStyle, width: 130 }}
                    onChange={e => setAssertion(i, { operator: e.target.value as AssertionOperator })}>
                    {OPERATORS.map(o => <option key={o} value={o}>{o}</option>)}
                  </select>
                  {!isUnaryOperator(a.operator) && (
                    <input placeholder="Expected" value={a.expected}
                      style={{ ...inputStyle, flex: 1, minWidth: 120 }}
                      onChange={e => setAssertion(i, { expected: e.target.value })} />
                  )}
                  {a.operator === 'Between' && (
                    <input placeholder="Expected2 (upper)" value={a.expected2 ?? ''}
                      style={{ ...inputStyle, width: 140 }}
                      onChange={e => setAssertion(i, { expected2: e.target.value })} />
                  )}
                  {(a.operator === 'EqualsDate') && (
                    <input placeholder="Tolerance (s)" value={a.toleranceSeconds ?? ''}
                      type="number" style={{ ...inputStyle, width: 110 }}
                      onChange={e => setAssertion(i, {
                        toleranceSeconds: e.target.value ? Number(e.target.value) : undefined,
                      })} />
                  )}
                  {(a.operator === 'EqualsNumeric') && (
                    <input placeholder="Tolerance" value={a.toleranceDelta ?? ''}
                      type="number" step="any" style={{ ...inputStyle, width: 110 }}
                      onChange={e => setAssertion(i, {
                        toleranceDelta: e.target.value ? Number(e.target.value) : undefined,
                      })} />
                  )}
                  <label style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, color: '#475569' }}>
                    <input type="checkbox" checked={a.ignoreCase}
                      onChange={e => setAssertion(i, { ignoreCase: e.target.checked })} />
                    ignoreCase
                  </label>
                  <button onClick={() => removeAssertion(i)} style={iconBtnStyle} title="Remove">×</button>
                </div>
              ))}
              <button onClick={() => addAssertion()} style={addBtnStyle}>+ Add assertion</button>
            </div>
          )}

          {/* Captures (always shown) */}
          <div style={{ marginTop: 16 }}>
            <div style={{ marginBottom: 6, fontSize: 12, fontWeight: 600, color: '#475569' }}>
              Captures ({form.captures.length})
            </div>
            {form.captures.map((c, i) => (
              <div key={i} style={assertionRowStyle}>
                <input placeholder="Column" value={c.column} style={{ ...inputStyle, width: 140 }}
                  onChange={e => setCapture(i, { column: e.target.value })} />
                <input placeholder="$.JsonPath (optional)" value={c.jsonPath ?? ''}
                  style={{ ...inputStyle, width: 160, fontFamily: 'ui-monospace,Consolas,monospace' }}
                  onChange={e => setCapture(i, { jsonPath: e.target.value || undefined })} />
                <input placeholder='As (e.g. "JobId")' value={c.as} style={{ ...inputStyle, width: 140 }}
                  onChange={e => setCapture(i, { as: e.target.value })} />
                <label style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, color: '#475569' }}>
                  <input type="checkbox" checked={c.required}
                    onChange={e => setCapture(i, { required: e.target.checked })} />
                  required
                </label>
                <button onClick={() => removeCapture(i)} style={iconBtnStyle} title="Remove">×</button>
              </div>
            ))}
            <button onClick={addCapture} style={addBtnStyle}>+ Add capture</button>
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

function isUnaryOperator(op: AssertionOperator): boolean {
  return op === 'IsNull' || op === 'IsNotNull';
}

/**
 * Promotes any legacy `expectedColumnValues` into `columnAssertions` defensively
 * — the backend shim should already have done this on deserialise, but a stale
 * client cache or in-flight chat-action card may still hand us the old shape.
 */
function normaliseDefinition(def: DbCheckStepDefinition): DbCheckStepDefinition {
  const cloned: DbCheckStepDefinition = {
    name: def.name ?? '',
    connectionKey: def.connectionKey || 'BravoDb',
    sql: def.sql ?? '',
    expectedRowCount: def.expectedRowCount,
    columnAssertions: def.columnAssertions ? def.columnAssertions.map(a => ({ ...a })) : [],
    captures: def.captures ? def.captures.map(c => ({ ...c })) : [],
    timeoutSeconds: def.timeoutSeconds || 15,
  };
  if (cloned.columnAssertions.length === 0 && def.expectedColumnValues) {
    for (const [col, expected] of Object.entries(def.expectedColumnValues)) {
      cloned.columnAssertions.push({
        column: col,
        operator: 'Equals',
        expected,
        ignoreCase: true,
      });
    }
  }
  return cloned;
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
const assertionRowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4,
  padding: '6px 8px', background: '#f8fafc', borderRadius: 6, border: '1px solid #e2e8f0',
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
const dryRunTh: React.CSSProperties = {
  padding: '4px 8px', background: '#f8fafc', borderBottom: '1px solid #e2e8f0',
  textAlign: 'left', fontSize: 11, fontWeight: 600, color: '#475569',
};
const dryRunTd: React.CSSProperties = {
  padding: '4px 8px', borderBottom: '1px solid #f1f5f9', fontSize: 12,
  color: '#0f172a', verticalAlign: 'top', whiteSpace: 'nowrap',
  maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis',
};
const addAssertionBtn: React.CSSProperties = {
  marginLeft: 6, padding: '0 6px', background: 'none', border: '1px solid #c7d2fe',
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

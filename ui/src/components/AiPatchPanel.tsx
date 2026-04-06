import { useState } from 'react';
import { previewAiPatch, applyAiPatch } from '../api/modules';
import type { TestObjective, ObjectivePatchEntry, ApiTestCase } from '../types';

interface Props {
  moduleId: string;
  testSetId: string;
  objectives: TestObjective[];
  onApplied: () => void;
}

export function AiPatchPanel({ moduleId, testSetId, objectives, onApplied }: Props) {
  const [expanded, setExpanded] = useState(false);
  const [instruction, setInstruction] = useState('');
  const [scopeType, setScopeType] = useState<'all' | 'objective'>('all');
  const [scopeObjectiveId, setScopeObjectiveId] = useState(objectives[0]?.id ?? '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<{
    original: ObjectivePatchEntry[];
    patched: ObjectivePatchEntry[];
  } | null>(null);
  const [applying, setApplying] = useState(false);

  const handlePreview = async () => {
    if (!instruction.trim()) return;
    setLoading(true);
    setError(null);
    setPreview(null);
    try {
      const scope = scopeType === 'objective' ? { objectiveId: scopeObjectiveId } : undefined;
      const result = await previewAiPatch(moduleId, testSetId, { instruction: instruction.trim(), scope });
      setPreview(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Preview failed');
    } finally {
      setLoading(false);
    }
  };

  const handleApply = async () => {
    if (!preview) return;
    setApplying(true);
    setError(null);
    try {
      await applyAiPatch(moduleId, testSetId, { patches: preview.patched });
      setPreview(null);
      setInstruction('');
      onApplied();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Apply failed');
    } finally {
      setApplying(false);
    }
  };

  if (!expanded) {
    return (
      <button onClick={() => setExpanded(true)} style={toggleBtnStyle}>
        AI Edit Test Cases
      </button>
    );
  }

  return (
    <div style={panelStyle}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
        <span style={{ fontSize: 14, fontWeight: 600, color: '#0f172a' }}>
          AI Edit Test Cases
        </span>
        <button onClick={() => { setExpanded(false); setPreview(null); }} style={closeBtnStyle}>
          Close
        </button>
      </div>

      {!preview ? (
        <>
          <textarea
            style={{ ...inputStyle, minHeight: 60, resize: 'vertical', marginBottom: 10 }}
            value={instruction}
            onChange={e => setInstruction(e.target.value)}
            placeholder='e.g. "remove the /api/v1 prefix from all endpoints" or "change expectedStatus to 200 for the happy path test"'
          />

          <div style={{ display: 'flex', gap: 12, alignItems: 'center', marginBottom: 12 }}>
            <span style={{ fontSize: 13, color: '#64748b' }}>Scope:</span>
            <label style={radioLabelStyle}>
              <input type="radio" checked={scopeType === 'all'} onChange={() => setScopeType('all')} />
              All objectives
            </label>
            <label style={radioLabelStyle}>
              <input type="radio" checked={scopeType === 'objective'} onChange={() => setScopeType('objective')} />
              Specific objective
            </label>
            {scopeType === 'objective' && (
              <select style={{ ...inputStyle, width: 'auto', flex: 1 }} value={scopeObjectiveId}
                onChange={e => setScopeObjectiveId(e.target.value)}>
                {objectives.filter(o => o.apiSteps.length > 0).map(o => (
                  <option key={o.id} value={o.id}>
                    {o.id} - {o.name.slice(0, 60)}
                  </option>
                ))}
              </select>
            )}
          </div>

          <button onClick={handlePreview} disabled={loading || !instruction.trim()} style={previewBtnStyle}>
            {loading ? 'Generating preview...' : 'Preview Changes'}
          </button>
        </>
      ) : (
        <>
          <div style={{ fontSize: 13, color: '#64748b', marginBottom: 10 }}>
            Instruction: <em>{instruction}</em>
          </div>
          <DiffView original={preview.original} patched={preview.patched} />
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 12 }}>
            <button onClick={() => setPreview(null)} style={discardBtnStyle} disabled={applying}>
              Discard
            </button>
            <button onClick={handleApply} style={applyBtnStyle} disabled={applying}>
              {applying ? 'Applying...' : 'Apply Changes'}
            </button>
          </div>
        </>
      )}

      {error && <p style={errorStyle}>{error}</p>}
    </div>
  );
}

// ── Diff view ──
function DiffView({ original, patched }: {
  original: ObjectivePatchEntry[];
  patched: ObjectivePatchEntry[];
}) {
  return (
    <div style={{ maxHeight: 400, overflowY: 'auto' }}>
      {original.map((orig, i) => {
        const patch = patched[i];
        const diffs = getFieldDiffs(orig.testCase, patch.testCase);
        if (diffs.length === 0) return null;
        return (
          <div key={i} style={diffCardStyle}>
            <div style={{ fontSize: 12, color: '#64748b', marginBottom: 6 }}>
              <strong>{orig.testCase.name}</strong>
              <span style={{ marginLeft: 8, fontSize: 11, color: '#94a3b8' }}>
                (objective: {orig.objectiveId})
              </span>
            </div>
            <table style={{ width: '100%', fontSize: 13, borderCollapse: 'collapse' }}>
              <thead>
                <tr>
                  <th style={diffThStyle}>Field</th>
                  <th style={{ ...diffThStyle, background: '#fef2f2', color: '#991b1b' }}>Before</th>
                  <th style={{ ...diffThStyle, background: '#f0fdf4', color: '#166534' }}>After</th>
                </tr>
              </thead>
              <tbody>
                {diffs.map(d => (
                  <tr key={d.field}>
                    <td style={diffTdStyle}>{d.field}</td>
                    <td style={{ ...diffTdStyle, background: '#fef2f2', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, wordBreak: 'break-all' }}>
                      {d.before}
                    </td>
                    <td style={{ ...diffTdStyle, background: '#f0fdf4', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, wordBreak: 'break-all' }}>
                      {d.after}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        );
      })}
    </div>
  );
}

function getFieldDiffs(a: ApiTestCase, b: ApiTestCase): { field: string; before: string; after: string }[] {
  const diffs: { field: string; before: string; after: string }[] = [];
  const fields: (keyof ApiTestCase)[] = [
    'name', 'method', 'endpoint', 'expectedStatus', 'isFuzzTest',
    'headers', 'queryParams', 'body', 'expectedBodyContains', 'expectedBodyNotContains',
  ];
  for (const f of fields) {
    const av = JSON.stringify(a[f] ?? null);
    const bv = JSON.stringify(b[f] ?? null);
    if (av !== bv) {
      diffs.push({ field: f, before: av, after: bv });
    }
  }
  return diffs;
}

// ── Styles ──
const toggleBtnStyle: React.CSSProperties = {
  background: '#f8fafc', color: '#2563eb', border: '1px solid #e2e8f0',
  padding: '6px 14px', borderRadius: 6, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', marginBottom: 12,
};
const panelStyle: React.CSSProperties = {
  background: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: 8,
  padding: 16, marginBottom: 16,
};
const closeBtnStyle: React.CSSProperties = {
  background: 'none', border: 'none', color: '#94a3b8', cursor: 'pointer', fontSize: 13,
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', fontSize: 14, border: '1px solid #e2e8f0',
  borderRadius: 6, outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
};
const radioLabelStyle: React.CSSProperties = {
  fontSize: 13, color: '#475569', display: 'flex', alignItems: 'center', gap: 4,
};
const previewBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const discardBtnStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const applyBtnStyle: React.CSSProperties = {
  background: '#16a34a', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 13, marginTop: 12, padding: '6px 12px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};
const diffCardStyle: React.CSSProperties = {
  background: '#fff', border: '1px solid #e2e8f0', borderRadius: 6,
  padding: 10, marginBottom: 8,
};
const diffThStyle: React.CSSProperties = {
  padding: '4px 8px', fontSize: 11, textAlign: 'left', fontWeight: 600,
  borderBottom: '1px solid #e2e8f0', color: '#64748b', textTransform: 'uppercase',
};
const diffTdStyle: React.CSSProperties = {
  padding: '4px 8px', borderBottom: '1px solid #f1f5f9',
};

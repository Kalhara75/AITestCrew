import { useEffect, useMemo, useState } from 'react';
import { apiFetch } from '../api/client';
import { fetchEnvironments } from '../api/config';
import type { EnvironmentsResponse, TestObjective } from '../types';

interface Props {
  moduleId: string;
  testSetId: string;
  objective: TestObjective;
  onSaved: () => void;
}

/**
 * Per-objective editor for:
 *   (a) AllowedEnvironments — which customer environments this objective runs on
 *   (b) EnvironmentParameters — per-env {{Token}} -> value overrides applied at playback
 *
 * Non-visual note: empty `allowedEnvironments` keeps "default-env only" behaviour
 * so legacy objectives (recorded before multi-env) keep running as they always did.
 */
export function EnvironmentParametersEditor({ moduleId, testSetId, objective, onSaved }: Props) {
  const [envs, setEnvs] = useState<EnvironmentsResponse | null>(null);
  const [allowed, setAllowed] = useState<string[]>(objective.allowedEnvironments ?? []);
  const [params, setParams] = useState<Record<string, Record<string, string>>>(
    objective.environmentParameters ?? {}
  );
  const [activeEnv, setActiveEnv] = useState<string>('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchEnvironments().then(setEnvs).catch(() => setEnvs(null));
  }, []);

  // Re-seed local state when the objective changes.
  useEffect(() => {
    setAllowed(objective.allowedEnvironments ?? []);
    setParams(objective.environmentParameters ?? {});
  }, [objective.id]);

  const envKeys = useMemo(() => envs?.environments.map(e => e.key) ?? [], [envs]);
  const defaultEnv = envs?.defaultEnvironment ?? null;

  // Ensure activeEnv is always one of the configured envs.
  useEffect(() => {
    if (envKeys.length === 0) return;
    if (!envKeys.includes(activeEnv)) {
      setActiveEnv(defaultEnv && envKeys.includes(defaultEnv) ? defaultEnv : envKeys[0]);
    }
  }, [envKeys.join(','), defaultEnv]);

  const toggleAllowed = (key: string) => {
    setAllowed(prev => prev.includes(key) ? prev.filter(k => k !== key) : [...prev, key]);
  };

  const setParam = (envKey: string, token: string, value: string) => {
    setParams(prev => ({
      ...prev,
      [envKey]: { ...(prev[envKey] ?? {}), [token]: value },
    }));
  };

  const renameParam = (envKey: string, oldToken: string, newToken: string) => {
    if (oldToken === newToken) return;
    setParams(prev => {
      const envMap = { ...(prev[envKey] ?? {}) };
      const value = envMap[oldToken] ?? '';
      delete envMap[oldToken];
      if (newToken) envMap[newToken] = value;
      return { ...prev, [envKey]: envMap };
    });
  };

  const deleteParam = (envKey: string, token: string) => {
    setParams(prev => {
      const envMap = { ...(prev[envKey] ?? {}) };
      delete envMap[token];
      return { ...prev, [envKey]: envMap };
    });
  };

  const addParam = (envKey: string) => {
    setParams(prev => {
      const envMap = { ...(prev[envKey] ?? {}) };
      let i = 1;
      while (envMap[`Token${i}`] !== undefined) i++;
      envMap[`Token${i}`] = '';
      return { ...prev, [envKey]: envMap };
    });
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      // Clean out empty-token rows before saving
      const cleaned: Record<string, Record<string, string>> = {};
      for (const [env, entries] of Object.entries(params)) {
        const filtered: Record<string, string> = {};
        for (const [k, v] of Object.entries(entries)) {
          if (k.trim().length > 0) filtered[k] = v;
        }
        if (Object.keys(filtered).length > 0) cleaned[env] = filtered;
      }

      const body = {
        ...objective,
        allowedEnvironments: allowed,
        environmentParameters: cleaned,
      };
      await apiFetch(`/modules/${moduleId}/testsets/${testSetId}/objectives/${objective.id}`, {
        method: 'PUT',
        body: JSON.stringify(body),
      });
      onSaved();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const currentEnvParams = params[activeEnv] ?? {};
  const entries = Object.entries(currentEnvParams);

  if (envKeys.length === 0) {
    return (
      <div style={cardStyle}>
        <h3 style={titleStyle}>Environment Parameters</h3>
        <p style={{ color: '#94a3b8', fontSize: 13 }}>
          No environments configured. Add entries under
          <code style={{ margin: '0 4px' }}>TestEnvironment.Environments</code>
          in <code>appsettings.json</code> to enable multi-environment test execution.
        </p>
      </div>
    );
  }

  const allowedNote = allowed.length === 0
    ? `No env selected → defaults to '${defaultEnv ?? '(default)'}' only`
    : `Runs on: ${allowed.join(', ')}`;

  return (
    <div style={cardStyle}>
      <h3 style={titleStyle}>Environment Parameters</h3>

      <div style={{ marginBottom: 16 }}>
        <label style={labelStyle}>Allowed environments</label>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          {envs!.environments.map(e => (
            <label key={e.key} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
              <input
                type="checkbox"
                checked={allowed.includes(e.key)}
                onChange={() => toggleAllowed(e.key)}
              />
              <span>{e.displayName}{e.isDefault ? ' (default)' : ''}</span>
            </label>
          ))}
        </div>
        <p style={{ color: '#64748b', fontSize: 12, margin: '6px 0 0' }}>{allowedNote}</p>
      </div>

      <div style={{ marginBottom: 12 }}>
        <label style={labelStyle}>Token values for</label>
        <select
          value={activeEnv}
          onChange={e => setActiveEnv(e.target.value)}
          style={selectStyle}
        >
          {envs!.environments.map(e => (
            <option key={e.key} value={e.key}>
              {e.displayName}{e.isDefault ? ' (default)' : ''}
            </option>
          ))}
        </select>
      </div>

      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr style={{ borderBottom: '1px solid #e2e8f0', color: '#64748b' }}>
            <th style={{ textAlign: 'left', padding: '6px 8px', width: '40%' }}>TOKEN</th>
            <th style={{ textAlign: 'left', padding: '6px 8px' }}>VALUE</th>
            <th style={{ width: 60 }}></th>
          </tr>
        </thead>
        <tbody>
          {entries.length === 0 && (
            <tr>
              <td colSpan={3} style={{ padding: 12, color: '#94a3b8', fontStyle: 'italic' }}>
                No parameters yet — add a token like <code>NMI</code> with the value for this environment,
                then reference it inside a step as <code>{'{{NMI}}'}</code>.
              </td>
            </tr>
          )}
          {entries.map(([token, value]) => (
            <tr key={token} style={{ borderBottom: '1px solid #f1f5f9' }}>
              <td style={{ padding: '4px 8px' }}>
                <input
                  type="text"
                  defaultValue={token}
                  onBlur={e => renameParam(activeEnv, token, e.target.value.trim())}
                  style={inputStyle}
                />
              </td>
              <td style={{ padding: '4px 8px' }}>
                <input
                  type="text"
                  value={value}
                  onChange={e => setParam(activeEnv, token, e.target.value)}
                  style={inputStyle}
                />
              </td>
              <td style={{ padding: '4px 8px', textAlign: 'right' }}>
                <button onClick={() => deleteParam(activeEnv, token)} style={deleteBtnStyle} title="Remove">
                  ×
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <button onClick={() => addParam(activeEnv)} style={addBtnStyle}>+ Add token</button>

      {error && <p style={errorStyle}>{error}</p>}
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 16 }}>
        <button onClick={save} disabled={saving} style={saveBtnStyle}>
          {saving ? 'Saving...' : 'Save'}
        </button>
      </div>
    </div>
  );
}

const cardStyle: React.CSSProperties = {
  padding: 16, borderRadius: 10, border: '1px solid #e2e8f0', background: '#fafbfc',
  marginTop: 16,
};
const titleStyle: React.CSSProperties = {
  margin: '0 0 12px', fontSize: 14, fontWeight: 700, color: '#0f172a',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 12, fontWeight: 600, color: '#475569', marginBottom: 6,
};
const selectStyle: React.CSSProperties = {
  padding: '6px 10px', fontSize: 13, border: '1px solid #e2e8f0', borderRadius: 6, background: '#fff',
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '4px 8px', fontSize: 13, border: '1px solid #e2e8f0',
  borderRadius: 4, boxSizing: 'border-box',
};
const deleteBtnStyle: React.CSSProperties = {
  background: 'none', border: '1px solid #fecaca', color: '#b91c1c',
  padding: '2px 8px', borderRadius: 4, cursor: 'pointer', fontSize: 14, lineHeight: '16px',
};
const addBtnStyle: React.CSSProperties = {
  marginTop: 8, background: 'none', border: '1px dashed #cbd5e1',
  padding: '4px 12px', borderRadius: 6, fontSize: 12, color: '#475569', cursor: 'pointer',
};
const saveBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none',
  padding: '6px 18px', borderRadius: 6, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 12, marginTop: 10, padding: '6px 10px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};

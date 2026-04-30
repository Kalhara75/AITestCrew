import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchDataPackReport } from '../api/dataPacks';
import type { DataPackEnvReport, DataPackScriptReport } from '../types';

const ENV_STATUS: Record<string, { label: string; bg: string; fg: string; dot: string }> = {
  Ran:                  { label: 'Ran',                   bg: '#ecfdf5', fg: '#065f46', dot: '#10b981' },
  SkippedNotConfigured: { label: 'Skipped (not configured)', bg: '#fef3c7', fg: '#92400e', dot: '#f59e0b' },
  SkippedOptOut:        { label: 'Skipped (opt-out)',     bg: '#f1f5f9', fg: '#475569', dot: '#94a3b8' },
  SkippedNoConnection:  { label: 'Skipped (no DB conn)',  bg: '#fef3c7', fg: '#92400e', dot: '#f59e0b' },
  ConnectionFailed:     { label: 'Connection failed',     bg: '#fee2e2', fg: '#991b1b', dot: '#ef4444' },
};

const SCRIPT_STATUS: Record<string, { fg: string; bg: string; icon: string }> = {
  Success: { fg: '#065f46', bg: '#ecfdf5', icon: '\u2713' },
  Failed:  { fg: '#991b1b', bg: '#fee2e2', icon: '\u2717' },
  Skipped: { fg: '#475569', bg: '#f1f5f9', icon: '\u2014' },
};

export function DataPacksPanel() {
  const { data, error, isLoading } = useQuery({
    queryKey: ['dataPackReport'],
    queryFn: fetchDataPackReport,
    refetchInterval: 30000,
    retry: false,
  });

  if (isLoading) return null;

  if (error) {
    return (
      <div style={panelStyle}>
        <div style={headerStyle}>
          <h3 style={headingStyle}>Startup Data Packs</h3>
        </div>
        <p style={{ margin: 0, color: '#92400e', fontSize: 13, background: '#fef3c7', padding: '8px 12px', borderRadius: 6 }}>
          Could not reach <code style={codeInline}>/api/data-packs/startup-report</code>.
          The deployed WebApi may be running an older build that pre-dates this feature — rebuild and redeploy the container.
        </p>
      </div>
    );
  }

  if (!data) {
    return (
      <div style={panelStyle}>
        <div style={headerStyle}>
          <h3 style={headingStyle}>Startup Data Packs</h3>
        </div>
        <p style={{ margin: 0, color: '#64748b', fontSize: 13 }}>
          No startup run captured yet — the WebApi has not finished scanning since it last started.
        </p>
      </div>
    );
  }

  const totalFailures = data.envs.reduce((s, e) => s + e.failures, 0);
  const ranCount = data.envs.filter(e => e.status === 'Ran').length;

  if (data.envs.length === 0) {
    return (
      <div style={panelStyle}>
        <div style={headerStyle}>
          <h3 style={headingStyle}>Startup Data Packs</h3>
        </div>
        <div style={metaStyle}>
          <span>
            Root: <code style={codeInline}>{data.rootPath}</code>
            {!data.rootExists && (
              <span style={{ color: '#92400e', marginLeft: 8 }}>(not found on disk)</span>
            )}
          </span>
          <span style={{ marginLeft: 'auto' }}>
            Completed {new Date(data.completedAtUtc).toLocaleString()} ({formatElapsed(data.elapsed)})
          </span>
        </div>
        <p style={{ margin: 0, color: '#64748b', fontSize: 13 }}>
          {data.rootExists
            ? 'No environment folders found under the data-packs root. Add scripts under data/datapacks/datateardown/<envKey>/ and rebuild.'
            : 'The data-packs root does not exist in the deployed container — check that data/datapacks/**/*.sql is being packaged into bin/datapacks/ at build time.'}
        </p>
      </div>
    );
  }

  return (
    <div style={panelStyle}>
      <div style={headerStyle}>
        <h3 style={headingStyle}>Startup Data Packs</h3>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {totalFailures > 0 && (
            <span style={{ ...countPill, background: '#fee2e2', color: '#991b1b', borderColor: '#fecaca' }}>
              {totalFailures} failure{totalFailures !== 1 ? 's' : ''}
            </span>
          )}
          <span style={countPill}>
            {ranCount} ran / {data.envs.length} env{data.envs.length !== 1 ? 's' : ''}
          </span>
        </div>
      </div>

      <div style={metaStyle}>
        <span title="Datapacks root path used by the WebApi at startup">
          Root: <code style={codeInline}>{data.rootPath}</code>
          {!data.rootExists && (
            <span style={{ color: '#92400e', marginLeft: 8 }}>(not found on disk)</span>
          )}
        </span>
        <span style={{ marginLeft: 'auto' }}>
          Completed {new Date(data.completedAtUtc).toLocaleString()} ({formatElapsed(data.elapsed)})
        </span>
      </div>

      {data.envs.length === 0 ? (
        <p style={{ margin: '8px 0 0', color: '#94a3b8', fontSize: 13 }}>
          No environment folders found under the data-packs root.
        </p>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {data.envs.map(env => <EnvRow key={env.envKey} env={env} />)}
        </div>
      )}
    </div>
  );
}

function EnvRow({ env }: { env: DataPackEnvReport }) {
  const [expanded, setExpanded] = useState(env.failures > 0 || env.status === 'ConnectionFailed');
  const status = ENV_STATUS[env.status] ?? { label: env.status, bg: '#f1f5f9', fg: '#475569', dot: '#94a3b8' };
  const hasScripts = env.scripts.length > 0;

  return (
    <div style={{
      background: '#fff',
      border: env.failures > 0 ? '1px solid #fecaca' : '1px solid #e2e8f0',
      borderRadius: 8,
    }}>
      <button
        type="button"
        onClick={() => hasScripts && setExpanded(v => !v)}
        style={{
          width: '100%', textAlign: 'left',
          background: 'transparent', border: 'none',
          padding: '10px 12px', cursor: hasScripts ? 'pointer' : 'default',
          display: 'flex', alignItems: 'center', gap: 12,
        }}
      >
        <div style={{
          width: 10, height: 10, borderRadius: '50%',
          background: status.dot, flexShrink: 0,
        }} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontWeight: 600, color: '#0f172a', fontSize: 14 }}>{env.envKey}</span>
            <span style={{
              fontSize: 11, fontWeight: 600,
              background: status.bg, color: status.fg,
              padding: '2px 8px', borderRadius: 10,
            }}>
              {status.label}
            </span>
          </div>
          {(env.skipReason || env.error) && (
            <div style={{ fontSize: 12, color: env.error ? '#991b1b' : '#64748b', marginTop: 4 }}>
              {env.error || env.skipReason}
            </div>
          )}
        </div>
        <div style={{ textAlign: 'right', fontSize: 12, color: '#64748b', whiteSpace: 'nowrap' }}>
          {env.status === 'Ran' ? (
            <>
              <div>
                <span style={{ color: env.failures > 0 ? '#991b1b' : '#065f46', fontWeight: 600 }}>
                  {env.scriptsExecuted}/{env.scriptsTotal} scripts
                </span>
                {env.batchesExecuted > 0 && (
                  <span style={{ color: '#94a3b8' }}> ({env.batchesExecuted} batches)</span>
                )}
              </div>
              {env.failures > 0 && (
                <div style={{ color: '#991b1b', fontWeight: 600 }}>{env.failures} failed</div>
              )}
            </>
          ) : env.scriptsTotal > 0 ? (
            <div>{env.scriptsTotal} script{env.scriptsTotal !== 1 ? 's' : ''} on disk</div>
          ) : null}
          {hasScripts && (
            <div style={{ color: '#94a3b8', marginTop: 2 }}>{expanded ? '\u25BC' : '\u25B6'}</div>
          )}
        </div>
      </button>

      {expanded && hasScripts && (
        <div style={{ borderTop: '1px solid #e2e8f0', padding: '6px 12px 10px' }}>
          {env.scripts.map((s, i) => <ScriptRow key={i} script={s} />)}
        </div>
      )}
    </div>
  );
}

function ScriptRow({ script }: { script: DataPackScriptReport }) {
  const status = SCRIPT_STATUS[script.status] ?? SCRIPT_STATUS.Skipped;
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: 10,
      padding: '6px 0',
      borderBottom: '1px dashed #f1f5f9',
      fontSize: 12,
    }}>
      <span style={{
        display: 'inline-block', minWidth: 18, textAlign: 'center',
        color: status.fg, fontWeight: 700,
      }}>
        {status.icon}
      </span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ color: '#0f172a', fontFamily: 'ui-monospace,monospace', wordBreak: 'break-all' }}>
          {script.relativePath}
        </div>
        {script.error && (
          <div style={{ color: '#991b1b', marginTop: 3, fontFamily: 'ui-monospace,monospace' }}>
            {script.error}
          </div>
        )}
      </div>
      <div style={{ textAlign: 'right', color: '#94a3b8', whiteSpace: 'nowrap' }}>
        {script.batchCount > 0 && <span>{script.batchCount} batch{script.batchCount !== 1 ? 'es' : ''}</span>}
        {script.elapsedMs > 0 && <span style={{ marginLeft: 6 }}>{script.elapsedMs}ms</span>}
      </div>
    </div>
  );
}

function formatElapsed(iso: string): string {
  // Backend serialises TimeSpan as "00:00:01.4500000".
  const m = /^(\d+):(\d+):(\d+)\.?(\d*)$/.exec(iso);
  if (!m) return iso;
  const h = parseInt(m[1], 10);
  const mi = parseInt(m[2], 10);
  const s = parseInt(m[3], 10);
  const ms = m[4] ? Math.round(parseInt(m[4].slice(0, 3).padEnd(3, '0'), 10)) : 0;
  if (h > 0) return `${h}h ${mi}m ${s}s`;
  if (mi > 0) return `${mi}m ${s}s`;
  if (s > 0) return ms > 0 ? `${s}.${String(ms).padStart(3, '0').slice(0, 1)}s` : `${s}s`;
  return `${ms}ms`;
}

const panelStyle: React.CSSProperties = {
  background: '#f8fafc', borderRadius: 10, padding: 16,
  border: '1px solid #e2e8f0', marginBottom: 20,
};

const headerStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8,
};

const headingStyle: React.CSSProperties = {
  margin: 0, fontSize: 14, fontWeight: 700, color: '#0f172a',
  textTransform: 'uppercase', letterSpacing: 0.5,
};

const countPill: React.CSSProperties = {
  fontSize: 12, color: '#475569', background: '#fff',
  padding: '2px 10px', borderRadius: 10, border: '1px solid #e2e8f0', fontWeight: 500,
};

const metaStyle: React.CSSProperties = {
  display: 'flex', flexWrap: 'wrap', gap: 12,
  fontSize: 12, color: '#64748b', marginBottom: 12,
};

const codeInline: React.CSSProperties = {
  fontFamily: 'ui-monospace,SFMono-Regular,Menlo,monospace', fontSize: 11,
  background: '#fff', padding: '1px 6px', borderRadius: 4,
  border: '1px solid #e2e8f0', color: '#334155',
};

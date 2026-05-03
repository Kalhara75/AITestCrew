import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchAuthHealth } from '../api/authHealth';
import { createAuthRefresh, startAuthRefresh } from '../api/authRefreshes';
import type { AuthHealthEntry, AuthHealthSurfaceEntry, AuthHealthStatus, AuthSurface } from '../types';

const surfaceLabel: Record<string, string> = {
  WebBlazor: 'Brave Cloud (Blazor)',
  WebMvc: 'Bravo Web (MVC)',
};

const statusCopy: Record<AuthHealthStatus, { headline: string; tone: 'amber' | 'red' }> = {
  Missing:      { headline: 'Never recorded',  tone: 'red'   },
  Stale:        { headline: 'Expired',         tone: 'red'   },
  ExpiringSoon: { headline: 'Expiring soon',   tone: 'amber' },
  Fresh:        { headline: 'Fresh',           tone: 'amber' }, // never rendered
};

/**
 * Pre-flight auth-health panel. One tile per environment, with a separate
 * "Refresh" button per surface (Blazor / MVC) inside the tile. Mirrors how
 * users think about it: "is auth healthy for THIS customer?". Envs with
 * AuthHealthEnabled = false in config never appear here.
 */
export function AuthHealthPanel() {
  const qc = useQueryClient();
  const { data: tiles, error } = useQuery({
    queryKey: ['authHealth'],
    queryFn: fetchAuthHealth,
    refetchInterval: 30_000,
    retry: false,
  });

  if (error) return null;
  const rows = tiles ?? [];
  if (rows.length === 0) return null;

  const refresh = async (envKey: string, surface: AuthSurface) => {
    const created = await createAuthRefresh(envKey, surface);
    await startAuthRefresh(created.id);
    qc.invalidateQueries({ queryKey: ['authHealth'] });
    qc.invalidateQueries({ queryKey: ['authRefreshes', 'active'] });
    qc.invalidateQueries({ queryKey: ['queue'] });
  };

  return (
    <div style={panelStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
        <span style={{ fontSize: 16 }}>⚠️</span>
        <span style={{ fontSize: 14, fontWeight: 700, color: '#92400e' }}>
          Auth state needs refreshing
        </span>
        <span style={countPill}>{rows.length}</span>
        <span style={hintStyle}>
          Refresh per surface to avoid runs failing on a login redirect
        </span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {rows.map(t => (
          <EnvTile
            key={t.envKey}
            tile={t}
            onRefresh={surface => refresh(t.envKey, surface)}
          />
        ))}
      </div>
    </div>
  );
}

function EnvTile({
  tile, onRefresh,
}: {
  tile: AuthHealthEntry;
  onRefresh: (surface: AuthSurface) => Promise<void>;
}) {
  return (
    <div style={tileStyle}>
      <div style={tileHeader}>
        <span style={{ fontSize: 13, fontWeight: 700, color: '#78350f' }}>
          {tile.envDisplayName || tile.envKey}
        </span>
        <code style={codeStyle}>{tile.envKey}</code>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {tile.surfaces.map(s => (
          <SurfaceRow
            key={s.surface}
            surface={s}
            onRefresh={() => onRefresh(s.surface)}
          />
        ))}
      </div>
    </div>
  );
}

function SurfaceRow({
  surface, onRefresh,
}: {
  surface: AuthHealthSurfaceEntry;
  onRefresh: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const label = surfaceLabel[surface.surface] ?? surface.surface;
  const copy = statusCopy[surface.status];
  const tone = copy.tone === 'red' ? redTone : amberTone;

  const worst = [...surface.agentReports]
    .filter(a => a.fileExists && a.ageHours != null)
    .sort((a, b) => (b.ageHours ?? 0) - (a.ageHours ?? 0))[0];

  const detail = surface.status === 'Missing'
    ? 'No cached storage state — record once before running tests.'
    : worst
      ? `Last refreshed ${formatAge(worst.ageHours!)} ago on ${worst.agentName} · TTL ${surface.ttlHours}h`
      : `Cached state ${formatAge(surface.ageHours)} old · TTL ${surface.ttlHours}h`;

  const click = async () => {
    setBusy(true);
    setErrorMsg(null);
    try {
      await onRefresh();
    } catch (e) {
      setErrorMsg(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ ...surfaceRowStyle, borderColor: tone.border }}>
      <span style={{ ...statusPill, background: tone.pillBg, color: tone.pillFg }}>
        {copy.headline}
      </span>
      <div style={{ flex: 1, fontSize: 13, color: '#78350f' }}>
        <b>{label}</b>
        <div style={{ fontSize: 12, color: '#a16207', marginTop: 2 }}>{detail}</div>
        {errorMsg && (
          <div style={errorStyle}>
            <b>Refresh failed:</b> {errorMsg}
          </div>
        )}
      </div>
      <button
        onClick={click}
        disabled={busy}
        style={{ ...primaryBtn, background: tone.pillFg, borderColor: tone.pillFg }}
      >
        {busy ? 'Starting…' : 'Refresh'}
      </button>
    </div>
  );
}

function formatAge(hours: number) {
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 24) return `${hours.toFixed(1)}h`;
  return `${(hours / 24).toFixed(1)}d`;
}

const panelStyle: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 10,
  padding: 14, marginBottom: 20,
};

const tileStyle: React.CSSProperties = {
  background: '#fff', border: '1px solid #fde68a', borderRadius: 8,
  padding: '10px 12px',
};

const tileHeader: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8,
};

const surfaceRowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 12,
  padding: '6px 10px', background: '#fffbeb',
  border: '1px solid', borderRadius: 6,
};

const countPill: React.CSSProperties = {
  fontSize: 11, color: '#92400e', background: '#fef3c7',
  padding: '2px 8px', borderRadius: 10, fontWeight: 600,
};

const statusPill: React.CSSProperties = {
  fontSize: 11, padding: '2px 8px', borderRadius: 10, fontWeight: 600,
  whiteSpace: 'nowrap',
};

const hintStyle: React.CSSProperties = {
  fontSize: 12, color: '#a16207', marginLeft: 'auto',
};

const primaryBtn: React.CSSProperties = {
  color: '#fff', border: '1px solid', padding: '4px 14px',
  borderRadius: 6, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};

const amberTone = {
  border: '#fde68a', pillBg: '#fef3c7', pillFg: '#92400e',
};
const redTone = {
  border: '#fecaca', pillBg: '#fee2e2', pillFg: '#b91c1c',
};

const codeStyle: React.CSSProperties = {
  fontFamily: 'ui-monospace,monospace', fontSize: 11,
  background: '#fef3c7', color: '#92400e',
  padding: '1px 6px', borderRadius: 4,
};

const errorStyle: React.CSSProperties = {
  marginTop: 4, padding: '4px 8px',
  background: '#fee2e2', border: '1px solid #fecaca',
  color: '#7f1d1d', fontSize: 12, borderRadius: 4,
};

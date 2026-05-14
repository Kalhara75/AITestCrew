import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchBackupStatus, triggerBackup } from '../api/backupApi';
import type { BackupStatus } from '../api/backupApi';

type Tone = 'green' | 'amber' | 'red' | 'grey';

function getTone(status: BackupStatus | undefined, intervalMinutes = 30): Tone {
  if (!status || !status.enabled) return 'grey';
  const now = Date.now();
  const amber = 90 * 60 * 1000;
  const red = 2 * intervalMinutes * 60 * 1000;
  const lastErrMs = status.lastErrorAt ? now - new Date(status.lastErrorAt).getTime() : null;
  const lastOkMs  = status.lastSuccessAt ? now - new Date(status.lastSuccessAt).getTime() : null;
  if (lastErrMs !== null && lastErrMs < 60 * 60 * 1000 &&
      (lastOkMs === null || (status.lastErrorAt! > (status.lastSuccessAt ?? '')))) {
    return 'red';
  }
  if (lastOkMs === null) return 'amber';
  if (lastOkMs < amber) return 'green';
  if (lastOkMs < red)   return 'amber';
  return 'red';
}

const TONE: Record<Tone, { dot: string; label: string; bg: string; border: string }> = {
  green: { dot: '#10b981', label: 'Healthy',   bg: '#ecfdf5', border: '#6ee7b7' },
  amber: { dot: '#f59e0b', label: 'Warning',   bg: '#fffbeb', border: '#fcd34d' },
  red:   { dot: '#ef4444', label: 'Unhealthy', bg: '#fee2e2', border: '#fca5a5' },
  grey:  { dot: '#94a3b8', label: 'Disabled',  bg: '#f8fafc', border: '#e2e8f0' },
};

export function BackupHealthPanel() {
  const qc = useQueryClient();
  const [modalOpen, setModalOpen] = useState(false);
  const [triggering, setTriggering] = useState(false);
  const [triggerError, setTriggerError] = useState<string | null>(null);

  const { data: status, error } = useQuery({
    queryKey: ['backupStatus'],
    queryFn: fetchBackupStatus,
    refetchInterval: 60_000,
    retry: false,
  });

  if (error || (status && !status.enabled)) return null;

  const tone = getTone(status);
  const s = TONE[tone];

  const handleTrigger = async () => {
    setTriggering(true); setTriggerError(null);
    try { await triggerBackup(); qc.invalidateQueries({ queryKey: ['backupStatus'] }); }
    catch (err: unknown) { setTriggerError(err instanceof Error ? err.message : String(err)); }
    finally { setTriggering(false); }
  };

  return (
    <>
      <div style={{ ...panelBase, background: s.bg, border: `1px solid ${s.border}`, marginBottom: 16 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <div style={{ width: 10, height: 10, borderRadius: '50%', background: s.dot, flexShrink: 0 }} />
          <span style={{ fontWeight: 700, fontSize: 14, color: '#0f172a' }}>Database Backup</span>
          <span style={{ fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 10, background: s.dot + '22', color: '#0f172a' }}>
            {s.label}
          </span>
          {status && (
            <span style={{ fontSize: 12, color: '#64748b', marginLeft: 8 }}>
              {status.lastSuccessAt
                ? `Last backup ${formatAge(status.lastSuccessAt)} ago (${formatSize(status.lastSuccessSizeBytes)})`
                : 'No successful backup yet'}
            </span>
          )}
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
            <button type='button' onClick={() => setModalOpen(true)} style={btnStyle}>Details</button>
            <button type='button' onClick={handleTrigger} disabled={triggering}
              style={{ ...btnStyle, background: '#2563eb', color: '#fff', borderColor: '#2563eb' }}>
              {triggering ? 'Running...' : 'Backup Now'}
            </button>
          </div>
        </div>
        {triggerError && <p style={{ margin: '8px 0 0', fontSize: 12, color: '#991b1b' }}>{triggerError}</p>}
      </div>
      {modalOpen && status && <BackupModal status={status} onClose={() => setModalOpen(false)} />}
    </>
  );
}

function BackupModal({ status, onClose }: { status: BackupStatus; onClose: () => void }) {
  return (
    <div style={overlay} onClick={onClose}>
      <div style={modal} onClick={e => e.stopPropagation()}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
          <h3 style={{ margin: 0, fontSize: 16, fontWeight: 700 }}>Database Backup Status</h3>
          <button type='button' onClick={onClose}
            style={{ background: 'none', border: 'none', fontSize: 20, cursor: 'pointer', color: '#64748b' }}>
            &times;
          </button>
        </div>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}><tbody>
          <Row label='Enabled'        value={status.enabled ? 'Yes' : 'No'} />
          <Row label='Last Success'   value={status.lastSuccessAt ? new Date(status.lastSuccessAt).toLocaleString() : 'Never'} />
          <Row label='Last Size'      value={status.lastSuccessSizeBytes > 0 ? formatSize(status.lastSuccessSizeBytes) : '--'} />
          <Row label='Last Error'     value={status.lastError ?? 'None'} />
          <Row label='Last Error At'  value={status.lastErrorAt ? new Date(status.lastErrorAt).toLocaleString() : '--'} />
          <Row label='Next Scheduled' value={status.nextScheduledAt ? new Date(status.nextScheduledAt).toLocaleString() : '--'} />
          <Row label='Backups on Disk' value={String(status.totalBackupsOnDisk)} />
          <Row label='Oldest Backup'  value={status.oldestBackupAt ? new Date(status.oldestBackupAt).toLocaleString() : '--'} />
        </tbody></table>
        <p style={{ margin: '16px 0 0', fontSize: 11, color: '#94a3b8' }}>
          Backup files are in the host&apos;s <code>./docker-backups</code> directory.
          See <code>docs/ops/backup-restore.md</code> for the restore procedure.
        </p>
      </div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <tr style={{ borderBottom: '1px solid #f1f5f9' }}>
      <td style={{ padding: '6px 8px', color: '#64748b', fontWeight: 500, whiteSpace: 'nowrap' }}>{label}</td>
      <td style={{ padding: '6px 8px', color: '#0f172a', wordBreak: 'break-all' }}>{value}</td>
    </tr>
  );
}

function formatAge(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const m = Math.floor(ms / 60000);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  return `${Math.floor(h / 24)}d`;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

const panelBase: React.CSSProperties = { borderRadius: 10, padding: '12px 16px' };

const btnStyle: React.CSSProperties = {
  padding: '4px 12px', borderRadius: 6, border: '1px solid #e2e8f0',
  background: '#fff', fontSize: 12, cursor: 'pointer', fontWeight: 500,
};

const overlay: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.3)',
  display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};

const modal: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 24,
  minWidth: 440, maxWidth: 560, boxShadow: '0 8px 32px rgba(0,0,0,0.15)',
};

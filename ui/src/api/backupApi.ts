import { apiFetch } from './client';

export interface BackupStatus {
  enabled: boolean;
  lastSuccessAt: string | null;
  lastSuccessSizeBytes: number;
  lastErrorAt: string | null;
  lastError: string | null;
  nextScheduledAt: string | null;
  totalBackupsOnDisk: number;
  oldestBackupAt: string | null;
}

export interface BackupResult {
  path: string;
  sizeBytes: number;
  durationMs: number;
}

export const fetchBackupStatus = () =>
  apiFetch<BackupStatus>('/admin/backup/status');

export const fetchBackupList = () =>
  apiFetch<{ paths: string[] }>('/admin/backup/list');

export const triggerBackup = () =>
  apiFetch<BackupResult>('/admin/backup', { method: 'POST' });

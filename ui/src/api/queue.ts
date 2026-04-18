import { apiFetch } from './client';
import type { QueueEntry } from '../types';

export const fetchQueue = () => apiFetch<QueueEntry[]>('/queue');

export const cancelQueuedJob = (jobId: string) =>
  apiFetch<void>(`/queue/${encodeURIComponent(jobId)}`, { method: 'DELETE' });

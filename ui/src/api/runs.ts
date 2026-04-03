import { apiFetch } from './client';
import type { TriggerRunRequest, TriggerRunResponse, RunStatusResponse } from '../types';

export const triggerRun = (request: TriggerRunRequest) =>
  apiFetch<TriggerRunResponse>('/runs', {
    method: 'POST',
    body: JSON.stringify(request),
  });

export const fetchRunStatus = (runId: string) =>
  apiFetch<RunStatusResponse>(`/runs/${runId}/status`);

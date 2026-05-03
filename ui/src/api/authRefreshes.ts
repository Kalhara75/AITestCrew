import { apiFetch } from './client';
import type { AuthRefreshRequest, AuthSurface } from '../types';

export const fetchActiveAuthRefreshes = () =>
  apiFetch<AuthRefreshRequest[]>('/auth-refreshes/active');

export const startAuthRefresh = (id: string) =>
  apiFetch<{ id: string; status: string; queueEntryId?: string }>(
    `/auth-refreshes/${encodeURIComponent(id)}/start`,
    { method: 'POST', body: '{}' }
  );

export const cancelAuthRefresh = (id: string) =>
  apiFetch<void>(
    `/auth-refreshes/${encodeURIComponent(id)}/cancel`,
    { method: 'POST', body: '{}' }
  );

// Proactive refresh: create a Pending row at (env, surface) scope. Dedupe-by-scope
// is enforced server-side, so calling this when one already exists returns the
// existing active row instead of inserting a duplicate.
export const createAuthRefresh = (envKey: string, surface: AuthSurface) =>
  apiFetch<AuthRefreshRequest>('/auth-refreshes', {
    method: 'POST',
    body: JSON.stringify({ environmentKey: envKey, surface, status: 'Pending' }),
  });

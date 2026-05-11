import { apiFetch } from './client';

export interface ApiDryRunRequest {
  envKey?: string | null;
  stackKey?: string | null;
  moduleKey?: string | null;
  method: string;
  endpoint: string;
  headers?: Record<string, string>;
  queryParams?: Record<string, string>;
  body?: unknown;
  parameters?: Record<string, string>;
}

export interface ApiDryRunResponse {
  status: number;
  reasonPhrase: string;
  headers: Record<string, string>;
  body: string;
  bodyTruncated: boolean;
}

/**
 * Calls `POST /api/api-step/dry-run`. Executes a single API call server-side
 * (with injected auth) and returns the HTTP response for live inspection.
 */
export const dryRunApiStep = (req: ApiDryRunRequest) =>
  apiFetch<ApiDryRunResponse>('/api-step/dry-run', {
    method: 'POST',
    body: JSON.stringify(req),
  });

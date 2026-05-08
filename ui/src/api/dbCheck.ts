import { apiFetch } from './client';

export interface DryRunColumn {
  name: string;
  sqlType: string;
}

export interface DryRunResponse {
  columns: DryRunColumn[];
  rows: Array<Record<string, string | null>>;
  totalRowCount: number;
}

export interface DryRunRequest {
  envKey?: string | null;
  connectionKey?: string;
  sql: string;
  parameters?: Record<string, string>;
}

/**
 * Calls `POST /api/db-check/dry-run`. Substitutes `parameters` into `sql`
 * server-side and returns columns + first 5 rows + total row count.
 */
export const dryRunDbCheck = (req: DryRunRequest) =>
  apiFetch<DryRunResponse>('/db-check/dry-run', {
    method: 'POST',
    body: JSON.stringify(req),
  });

/** Lists configured DB connection keys for the editor dropdown. */
export const getDbConnections = (envKey?: string | null) => {
  const qs = envKey ? `?envKey=${encodeURIComponent(envKey)}` : '';
  return apiFetch<{ keys: string[] }>(`/db-check/connections${qs}`);
};

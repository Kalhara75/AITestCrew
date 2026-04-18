import { apiFetch } from './client';
import type { AgentSummary } from '../types';

export const fetchAgents = () => apiFetch<AgentSummary[]>('/agents');

export const deregisterAgent = (id: string) =>
  apiFetch<void>(`/agents/${encodeURIComponent(id)}`, { method: 'DELETE' });

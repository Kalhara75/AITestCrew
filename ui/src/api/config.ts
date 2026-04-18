import { apiFetch } from './client';
import type { ApiStacksResponse, EnvironmentsResponse } from '../types';

export const fetchApiStacks = () =>
  apiFetch<ApiStacksResponse>('/config/api-stacks');

export const fetchEnvironments = () =>
  apiFetch<EnvironmentsResponse>('/config/environments');

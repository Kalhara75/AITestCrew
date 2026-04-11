import { apiFetch } from './client';
import type { ApiStacksResponse } from '../types';

export const fetchApiStacks = () =>
  apiFetch<ApiStacksResponse>('/config/api-stacks');

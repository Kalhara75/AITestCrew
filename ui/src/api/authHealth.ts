import { apiFetch } from './client';
import type { AuthHealthEntry } from '../types';

export const fetchAuthHealth = () => apiFetch<AuthHealthEntry[]>('/auth-health');

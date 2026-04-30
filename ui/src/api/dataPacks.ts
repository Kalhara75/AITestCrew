import { apiFetch } from './client';
import type { DataPackStartupReport } from '../types';

export const fetchDataPackReport = () =>
  apiFetch<DataPackStartupReport | null>('/data-packs/startup-report');

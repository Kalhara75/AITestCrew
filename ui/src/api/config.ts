import { apiFetch } from './client';
import type { ApiStacksResponse, AseXmlVerificationConfigResponse, EnvironmentsResponse } from '../types';

export const fetchApiStacks = () =>
  apiFetch<ApiStacksResponse>('/config/api-stacks');

export const fetchEnvironments = () =>
  apiFetch<EnvironmentsResponse>('/config/environments');

export const fetchAseXmlVerificationConfig = () =>
  apiFetch<AseXmlVerificationConfigResponse>('/config/asexml-verification');

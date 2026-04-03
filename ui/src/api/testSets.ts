import { apiFetch } from './client';
import type { TestSetListItem, TestSetDetail, RunSummary, ExecutionRun } from '../types';

export const fetchTestSets = () =>
  apiFetch<TestSetListItem[]>('/testsets');

export const fetchTestSet = (id: string) =>
  apiFetch<TestSetDetail>(`/testsets/${id}`);

export const fetchRuns = (testSetId: string) =>
  apiFetch<RunSummary[]>(`/testsets/${testSetId}/runs`);

export const fetchRun = (testSetId: string, runId: string) =>
  apiFetch<ExecutionRun>(`/testsets/${testSetId}/runs/${runId}`);

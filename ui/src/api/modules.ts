import { apiFetch } from './client';
import type {
  Module, TestSetListItem, TestSetDetail, RunSummary, ExecutionRun,
  MoveObjectiveRequest, TestObjective, AiPatchRequest, AiPatchPreview, AiPatchApplyRequest,
  TriggerModuleRunResponse, ModuleRunStatus, WebUiStep, VerificationStep, SqlTeardownStep,
} from '../types';

export const fetchModules = () =>
  apiFetch<Module[]>('/modules');

export const createModule = (name: string, description?: string) =>
  apiFetch<Module>('/modules', {
    method: 'POST',
    body: JSON.stringify({ name, description }),
  });

export const fetchModule = (moduleId: string) =>
  apiFetch<Module>(`/modules/${moduleId}`);

export const updateModule = (moduleId: string, name?: string, description?: string) =>
  apiFetch<Module>(`/modules/${moduleId}`, {
    method: 'PUT',
    body: JSON.stringify({ name, description }),
  });

export const deleteModule = (moduleId: string) =>
  apiFetch<void>(`/modules/${moduleId}`, { method: 'DELETE' });

export const fetchModuleTestSets = (moduleId: string) =>
  apiFetch<TestSetListItem[]>(`/modules/${moduleId}/testsets`);

export const createTestSet = (moduleId: string, name: string) =>
  apiFetch<TestSetDetail>(`/modules/${moduleId}/testsets`, {
    method: 'POST',
    body: JSON.stringify({ name }),
  });

export const fetchModuleTestSet = (moduleId: string, tsId: string) =>
  apiFetch<TestSetDetail>(`/modules/${moduleId}/testsets/${tsId}`);

export const deleteTestSet = (moduleId: string, tsId: string) =>
  apiFetch<void>(`/modules/${moduleId}/testsets/${tsId}`, { method: 'DELETE' });

export const fetchModuleRuns = (moduleId: string, tsId: string) =>
  apiFetch<RunSummary[]>(`/modules/${moduleId}/testsets/${tsId}/runs`);

export const fetchModuleRun = (moduleId: string, tsId: string, runId: string) =>
  apiFetch<ExecutionRun>(`/modules/${moduleId}/testsets/${tsId}/runs/${runId}`);

export const moveObjective = (moduleId: string, tsId: string, request: MoveObjectiveRequest) =>
  apiFetch<{ moved: boolean }>(`/modules/${moduleId}/testsets/${tsId}/move-objective`, {
    method: 'POST',
    body: JSON.stringify(request),
  });

export const updateObjective = (
  moduleId: string, tsId: string, objectiveId: string, objective: TestObjective
) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/objectives/${objectiveId}`,
    { method: 'PUT', body: JSON.stringify(objective) }
  );

export const deleteObjective = (
  moduleId: string, tsId: string, objectiveId: string
) =>
  apiFetch<void>(
    `/modules/${moduleId}/testsets/${tsId}/objectives/${objectiveId}`,
    { method: 'DELETE' }
  );

/** Removes one post-delivery UI verification from an aseXML delivery case. */
export const deleteVerification = (
  moduleId: string,
  tsId: string,
  objectiveId: string,
  deliveryIndex: number,
  verificationIndex: number
) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/objectives/${objectiveId}/deliveries/${deliveryIndex}/verifications/${verificationIndex}`,
    { method: 'DELETE' }
  );

/** Replaces a post-delivery UI verification (e.g. after editing its WebUi steps). */
export const updateVerification = (
  moduleId: string,
  tsId: string,
  objectiveId: string,
  deliveryIndex: number,
  verificationIndex: number,
  updated: VerificationStep
) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/objectives/${objectiveId}/deliveries/${deliveryIndex}/verifications/${verificationIndex}`,
    { method: 'PUT', body: JSON.stringify(updated) }
  );

export const previewAiPatch = (moduleId: string, tsId: string, request: AiPatchRequest) =>
  apiFetch<AiPatchPreview>(
    `/modules/${moduleId}/testsets/${tsId}/ai-patch`,
    { method: 'POST', body: JSON.stringify(request) }
  );

export const applyAiPatch = (moduleId: string, tsId: string, request: AiPatchApplyRequest) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/ai-patch/apply`,
    { method: 'POST', body: JSON.stringify(request) }
  );

export const updateSetupSteps = (moduleId: string, tsId: string, setupStartUrl: string, setupSteps: WebUiStep[]) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/setup-steps`,
    { method: 'PUT', body: JSON.stringify({ setupStartUrl, setupSteps }) }
  );

export const clearSetupSteps = (moduleId: string, tsId: string) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/setup-steps`,
    { method: 'DELETE' }
  );

export const updateTeardownSteps = (moduleId: string, tsId: string, teardownSteps: SqlTeardownStep[]) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/teardown-steps`,
    { method: 'PUT', body: JSON.stringify({ teardownSteps }) }
  );

export const clearTeardownSteps = (moduleId: string, tsId: string) =>
  apiFetch<TestSetDetail>(
    `/modules/${moduleId}/testsets/${tsId}/teardown-steps`,
    { method: 'DELETE' }
  );

export const triggerModuleRun = (moduleId: string) =>
  apiFetch<TriggerModuleRunResponse>(`/modules/${moduleId}/run`, { method: 'POST' });

export const fetchModuleRunStatus = (moduleId: string) =>
  apiFetch<ModuleRunStatus>(`/modules/${moduleId}/run/status`);


export interface Module {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  testSetCount: number;
  totalObjectives: number;
}

export interface TestSetListItem {
  id: string;
  name: string;
  moduleId: string;
  objective: string;
  objectives: string[];
  objectiveNames?: Record<string, string>;
  objectiveCount: number;
  createdAt: string;
  lastRunAt: string;
  runCount: number;
  lastRunStatus: string | null;
}

export interface ApiTestDefinition {
  method: string;
  endpoint: string;
  headers: Record<string, string>;
  queryParams: Record<string, string>;
  body: unknown;
  expectedStatus: number;
  expectedBodyContains: string[];
  expectedBodyNotContains: string[];
  isFuzzTest: boolean;
}

export interface WebUiStep {
  action: string;
  selector: string | null;
  value: string | null;
  timeoutMs: number;
}

export interface WebUiTestDefinition {
  description: string;
  startUrl: string;
  steps: WebUiStep[];
  takeScreenshotOnFailure: boolean;
}

export interface TestObjective {
  id: string;
  name: string;
  parentObjective: string;
  agentName: string;
  targetType: string;
  apiSteps: ApiTestDefinition[];
  webUiSteps: WebUiTestDefinition[];
  stepCount: number;
}

export interface TestSetDetail {
  id: string;
  name: string;
  moduleId: string;
  objective: string;
  objectives: string[];
  objectiveNames?: Record<string, string>;
  createdAt: string;
  lastRunAt: string;
  runCount: number;
  lastRunStatus: string | null;
  testObjectives: TestObjective[];
}

export interface MoveObjectiveRequest {
  objective: string;
  destinationModuleId: string;
  destinationTestSetId: string;
}

// Legacy — kept for AI patch endpoints which still use the flat test case format
export interface ApiTestCase {
  name: string;
  method: string;
  endpoint: string;
  headers: Record<string, string>;
  queryParams: Record<string, string>;
  body: unknown;
  expectedStatus: number;
  expectedBodyContains: string[];
  expectedBodyNotContains: string[];
  isFuzzTest: boolean;
}

export interface RunSummary {
  runId: string;
  mode: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  totalDuration: string;
  totalObjectives: number;
  passedObjectives: number;
  failedObjectives: number;
  errorObjectives: number;
}

export interface ExecutionRun {
  runId: string;
  testSetId: string;
  moduleId: string | null;
  objective: string;
  mode: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
  totalDuration: string;
  summary: string;
  totalObjectives: number;
  passedObjectives: number;
  failedObjectives: number;
  errorObjectives: number;
  objectiveResults: ObjectiveResult[];
}

export interface ObjectiveResult {
  objectiveId: string;
  objectiveName: string;
  agentName: string;
  status: string;
  summary: string;
  duration: string;
  completedAt: string;
  passedSteps: number;
  failedSteps: number;
  totalSteps: number;
  steps: StepResult[];
}

export interface StepResult {
  action: string;
  summary: string;
  status: string;
  detail: string | null;
  duration: string;
  timestamp: string;
}

export interface RunStatusResponse {
  runId: string;
  objective: string;
  mode: string;
  testSetId: string | null;
  status: string;
  startedAt: string;
  completedAt: string | null;
  error: string | null;
}

export interface ObjectivePatchEntry {
  objectiveId: string;
  testCase: ApiTestCase;
}

export interface AiPatchScope {
  objectiveId?: string;
}

export interface AiPatchRequest {
  instruction: string;
  scope?: AiPatchScope;
}

export interface AiPatchPreview {
  original: ObjectivePatchEntry[];
  patched: ObjectivePatchEntry[];
}

export interface AiPatchApplyRequest {
  patches: ObjectivePatchEntry[];
}

export interface TriggerRunRequest {
  objective?: string;
  objectiveName?: string;
  mode: string;
  testSetId?: string;
  moduleId?: string;
}

export interface TriggerRunResponse {
  runId: string;
  status: string;
  startedAt: string;
}

// Legacy types kept for backward compatibility
export interface WebUiTestCase {
  name: string;
  description: string;
  startUrl: string;
  steps: WebUiStep[];
  takeScreenshotOnFailure: boolean;
}

export interface TaskEntry {
  taskId: string;
  taskDescription: string;
  agentName: string;
  objective: string;
  testCases: ApiTestCase[];
  webUiTestCases: WebUiTestCase[];
}

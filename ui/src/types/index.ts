export interface Module {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  testSetCount: number;
  totalTestCases: number;
}

export interface TestSetListItem {
  id: string;
  name: string;
  moduleId: string;
  objective: string;
  objectives: string[];
  objectiveNames?: Record<string, string>;
  taskCount: number;
  testCaseCount: number;
  createdAt: string;
  lastRunAt: string;
  runCount: number;
  lastRunStatus: string | null;
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
  tasks: TaskEntry[];
}

export interface TaskEntry {
  taskId: string;
  taskDescription: string;
  agentName: string;
  objective: string;
  testCases: ApiTestCase[];
}

export interface MoveObjectiveRequest {
  objective: string;
  destinationModuleId: string;
  destinationTestSetId: string;
}

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
  totalTasks: number;
  passedTasks: number;
  failedTasks: number;
  errorTasks: number;
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
  totalTasks: number;
  passedTasks: number;
  failedTasks: number;
  errorTasks: number;
  taskResults: TaskResult[];
}

export interface TaskResult {
  taskId: string;
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

export interface TestCasePatchEntry {
  taskId: string;
  caseIndex: number;
  testCase: ApiTestCase;
}

export interface AiPatchScope {
  taskId?: string;
  caseIndex?: number;
}

export interface AiPatchRequest {
  instruction: string;
  scope?: AiPatchScope;
}

export interface AiPatchPreview {
  original: TestCasePatchEntry[];
  patched: TestCasePatchEntry[];
}

export interface AiPatchApplyRequest {
  patches: TestCasePatchEntry[];
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

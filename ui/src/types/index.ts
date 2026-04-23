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
  apiStackKey?: string | null;
  apiModule?: string | null;
  endpointCode?: string | null;
  environmentKey?: string | null;
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
  matchFirst?: boolean;
}

export interface WebUiTestDefinition {
  description: string;
  startUrl: string;
  steps: WebUiStep[];
  takeScreenshotOnFailure: boolean;
}

export interface DesktopUiStep {
  action: string;
  automationId: string | null;
  name: string | null;
  className: string | null;
  controlType: string | null;
  treePath: string | null;
  value: string | null;
  menuPath: string | null;
  windowTitle: string | null;
  timeoutMs: number;
}

export interface DesktopUiTestDefinition {
  description: string;
  steps: DesktopUiStep[];
  takeScreenshotOnFailure: boolean;
}

export interface AseXmlTestDefinition {
  description: string;
  templateId: string;
  transactionType: string;
  fieldValues: Record<string, string>;
  validateAgainstSchema: boolean;
}

export interface VerificationStep {
  description: string;
  target: string;           // UI_Web_MVC | UI_Web_Blazor | UI_Desktop_WinForms
  waitBeforeSeconds: number;
  webUi?: WebUiTestDefinition;
  desktopUi?: DesktopUiTestDefinition;
}

export interface AseXmlDeliveryTestDefinition {
  description: string;
  templateId: string;
  transactionType: string;
  fieldValues: Record<string, string>;
  endpointCode: string;
  validateAgainstSchema: boolean;
  postDeliveryVerifications: VerificationStep[];
}

export interface TestObjective {
  id: string;
  name: string;
  parentObjective: string;
  agentName: string;
  targetType: string;
  source: string;
  apiSteps: ApiTestDefinition[];
  webUiSteps: WebUiTestDefinition[];
  desktopUiSteps: DesktopUiTestDefinition[];
  aseXmlSteps: AseXmlTestDefinition[];
  aseXmlDeliverySteps: AseXmlDeliveryTestDefinition[];
  stepCount: number;
  allowedEnvironments?: string[];
  environmentParameters?: Record<string, Record<string, string>>;
}

export interface ObjectiveStatus {
  status: string;
  completedAt: string | null;
  runId: string;
}

export interface TestSetDetail {
  id: string;
  name: string;
  moduleId: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
  endpointCode?: string | null;
  environmentKey?: string | null;
  objective: string;
  objectives: string[];
  objectiveNames?: Record<string, string>;
  createdAt: string;
  lastRunAt: string;
  runCount: number;
  setupStartUrl: string;
  setupSteps: WebUiStep[];
  teardownSteps: SqlTeardownStep[];
  lastRunStatus: string | null;
  objectiveStatuses?: Record<string, ObjectiveStatus>;
  testObjectives: TestObjective[];
}

export interface SqlTeardownStep {
  name: string;
  sql: string;
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
  pendingVerifications?: {
    pendingId: string;
    deliveryObjectiveId: string;
    firstDueAt: string;
    deadlineAt: string;
    attemptCount: number;
    queueEntryId: string;
  }[];
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
  objectiveId?: string;
  apiStackKey?: string;
  apiModule?: string;
  verificationWaitOverride?: number;
  environmentKey?: string;
}

export interface EnvironmentInfo {
  key: string;
  displayName: string;
  isDefault: boolean;
  dataTeardownEnabled?: boolean;
}

export interface EnvironmentsResponse {
  environments: EnvironmentInfo[];
  defaultEnvironment: string | null;
}

export interface ApiModuleInfo {
  name: string;
  pathPrefix: string;
}

export interface ApiStackInfo {
  baseUrl: string;
  modules: Record<string, ApiModuleInfo>;
}

export interface ApiStacksResponse {
  stacks: Record<string, ApiStackInfo>;
  defaultStack: string | null;
  defaultModule: string | null;
}

export interface TriggerRunResponse {
  runId: string;
  status: string;
  startedAt: string;
}

// ── Module-level run types ──

export interface TestSetRunProgress {
  testSetId: string;
  testSetName: string;
  status: 'Pending' | 'Running' | 'Completed' | 'Failed';
  childRunId: string | null;
  error: string | null;
}

export interface ModuleRunStatus {
  moduleRunId: string;
  moduleId: string;
  moduleName: string;
  status: 'Running' | 'Completed' | 'CompletedWithFailures' | 'Failed';
  startedAt: string;
  completedAt: string | null;
  error: string | null;
  completedCount: number;
  totalCount: number;
  currentTestSetIds: string[];
  currentTestSetId: string | null;
  testSets: TestSetRunProgress[];
}

export interface TriggerModuleRunResponse {
  moduleRunId: string;
  moduleId: string;
  status: string;
  startedAt: string;
  totalTestSets: number;
}

export interface ActiveRunResponse {
  type: 'module' | 'testset' | null;
  moduleRun: ModuleRunStatus | null;
  run: RunStatusResponse | null;
}

// ── Distributed execution (Phase 4) ──

export interface AgentSummary {
  id: string;
  name: string;
  userId: string | null;
  ownerName: string | null;
  capabilities: string[];
  version: string | null;
  status: 'Online' | 'Offline' | 'Busy' | string;
  lastSeenAt: string;
  registeredAt: string;
  currentJob: {
    id: string;
    testSetId: string;
    objectiveId: string | null;
    targetType: string;
    status: string;
  } | null;
}

export interface QueueEntry {
  id: string;
  moduleId: string;
  testSetId: string;
  objectiveId: string | null;
  targetType: string;
  mode: string;
  status: 'Queued' | 'Claimed' | 'Running' | 'Completed' | 'Failed' | 'Cancelled' | string;
  claimedBy: string | null;
  requestedBy: string | null;
  claimedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  error: string | null;

  // Deferred-verification scheduling (v6): null on ordinary entries.
  notBeforeAt?: string | null;
  deadlineAt?: string | null;
  attemptCount?: number;
  parentQueueEntryId?: string | null;
  parentRunId?: string | null;
}

export interface PendingVerificationSummary {
  pendingId: string;
  deliveryObjectiveId: string;
  firstDueAt: string;
  deadlineAt: string;
  attemptCount: number;
  queueEntryId: string;
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

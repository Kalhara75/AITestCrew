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

export type ApiAssertionSource = 'Status' | 'Header' | 'Body' | 'BodyText';

export interface ApiAssertion {
  source: ApiAssertionSource;
  headerName?: string | null;
  jsonPath?: string | null;
  operator: string;
  expected?: string | null;
  expected2?: string | null;
  ignoreCase: boolean;
  toleranceSeconds?: number | null;
  toleranceDelta?: number | null;
}

export interface ApiCapture {
  source: ApiAssertionSource;
  headerName?: string | null;
  jsonPath?: string | null;
  as: string;
  required: boolean;
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
  apiAssertions?: ApiAssertion[];
  captures?: ApiCapture[];
  postSteps?: PostStep[];
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
  postSteps?: PostStep[];
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
  /** Captured click X relative to the process main window. Null on pre-existing recordings. */
  windowRelativeX: number | null;
  /** Captured click Y relative to the process main window. Null on pre-existing recordings. */
  windowRelativeY: number | null;
  /** Recorded pause (ms) before this step during recording — honoured at replay. */
  delayBeforeMs: number | null;
  /**
   * For assert-count: which UIA ControlType to count among the resolved
   * element's descendants (e.g. "DataItem", "ListItem", "TreeItem", "Button").
   * When null/empty, executor tries DataItem → ListItem → TreeItem.
   */
  itemControlType: string | null;
  /** For assert-text-ocr: width (px) of OCR region centred on click. Null = 200. */
  ocrRegionWidth: number | null;
  /** For assert-text-ocr: height (px) of OCR region centred on click. Null = 40. */
  ocrRegionHeight: number | null;
}

export interface DesktopUiTestDefinition {
  description: string;
  steps: DesktopUiStep[];
  takeScreenshotOnFailure: boolean;
  postSteps?: PostStep[];
}

export interface AseXmlTestDefinition {
  description: string;
  templateId: string;
  transactionType: string;
  fieldValues: Record<string, string>;
  validateAgainstSchema: boolean;
  postSteps?: PostStep[];
}

/**
 * Comparator operators on a `ColumnAssertion`. Mirrors the C# `AssertionOperator`
 * enum, serialised as the bare name (`"Equals"`, `"IsNull"`, etc.).
 */
export type AssertionOperator =
  | 'Equals' | 'NotEquals' | 'Contains' | 'NotContains'
  | 'StartsWith' | 'EndsWith' | 'Regex'
  | 'GreaterThan' | 'LessThan' | 'Between'
  | 'IsNull' | 'IsNotNull'
  | 'EqualsNumeric' | 'EqualsDate';

export interface ColumnAssertion {
  /** Column name from the SELECT result set. {{Token}} substituted. */
  column: string;
  /** Optional JSONPath inside the column's value (e.g. `$.OrderId`). {{Token}} substituted. */
  jsonPath?: string;
  operator: AssertionOperator;
  /** Expected value (string projection). {{Token}} substituted. */
  expected: string;
  /** Upper bound for `Between`. {{Token}} substituted. */
  expected2?: string;
  ignoreCase: boolean;
  toleranceSeconds?: number;
  toleranceDelta?: number;
}

export interface ColumnCapture {
  /** Column name. {{Token}} substituted. */
  column: string;
  /** Optional JSONPath inside the column's value. {{Token}} substituted. */
  jsonPath?: string;
  /** Token name to bind, e.g. "JobId" (no braces). NOT {{Token}} substituted. */
  as: string;
  /** When false, missing/null does NOT fail the step — token left undefined. */
  required: boolean;
}

// ── REQ-004 Azure Service Bus event-assert post-step ──────────────────

export type ServiceBusEntityType = 'Queue' | 'Topic';
export type BodyFormat = 'Auto' | 'Json' | 'Xml' | 'Text' | 'Binary';
export type ReceiveMode = 'PeekLock' | 'ReceiveAndDelete';
export type MatchMode =
  | 'AnyMessage'
  | 'AllMessages'
  | 'ExactlyOne'
  | 'ExactCount'
  | 'MinCount'
  | 'MaxCount'
  | 'CountRange';

export interface ServiceBusEntity {
  type: ServiceBusEntityType;
  name: string;                 // queue OR topic name; {{Token}} substituted
  subscriptionName?: string;    // required when type=Topic; {{Token}} substituted
}

export interface EventCriterion {
  /**
   * Field path. Either a system property (MessageId, CorrelationId,
   * Subject, ContentType, ReplyTo, To, SessionId, EnqueuedTimeUtc,
   * DeliveryCount, PartitionKey), an `ApplicationProperties.<name>`
   * lookup, `Body.<jsonpath>`, `BodyXml.<xpath>`, `BodyText`, or
   * `BodyLength`. {{Token}} substituted.
   */
  field: string;
  operator: AssertionOperator;
  /** Expected value (string projection). {{Token}} substituted. */
  expected: string;
  /** Upper bound for `Between`. {{Token}} substituted. */
  expected2?: string;
  ignoreCase: boolean;
  toleranceSeconds?: number;
  toleranceDelta?: number;
}

export interface EventCapture {
  /** Field path — same syntax as `EventCriterion.field`. {{Token}} substituted. */
  field: string;
  /** Token name to bind, e.g. "MessageId" (no braces). NOT {{Token}} substituted. */
  as: string;
  /** When false, missing/null does NOT fail the step — token left undefined. */
  required: boolean;
}

export interface EventAssertStepDefinition {
  name: string;
  connectionKey: string;
  entity: ServiceBusEntity;
  bodyFormat: BodyFormat;
  receiveMode: ReceiveMode;
  matchMode: MatchMode;
  expectedCount?: number;
  maxCount?: number;
  timeoutSeconds: number;
  maxMessages: number;
  drainBeforeParent: boolean;
  completeOnPass: boolean;
  correlationFilter?: string;
  sessionId?: string;
  criteria: EventCriterion[];
  captures: EventCapture[];
}

export interface DbCheckStepDefinition {
  name: string;
  /** Logical connection key (e.g. "BravoDb", "SdrReportingDb"). */
  connectionKey: string;
  sql: string;
  /** Mutually exclusive with columnAssertions. */
  expectedRowCount?: number;
  /**
   * Per-column assertions on the first row. Replaces the legacy
   * `expectedColumnValues` dict (the backend deserialises legacy JSON into a
   * list of `Equals` entries; this field is the canonical shape).
   */
  columnAssertions: ColumnAssertion[];
  /** Optional captures bound into the post-step run context as {{Token}}. */
  captures: ColumnCapture[];
  timeoutSeconds: number;
  /**
   * Legacy field — only present on payloads from very old backends. The
   * deserialiser shim normalises into `columnAssertions`; this stays optional
   * so the chat-action card and read-only block can render mid-flight conversions.
   * @deprecated Prefer `columnAssertions`.
   */
  expectedColumnValues?: Record<string, string>;
}

/**
 * A post-step attached to any parent step. Carriers are mutually exclusive
 * but the TS interface keeps them all optional so a single editor can bind
 * to whichever is populated. The `VerificationStep` alias below preserves
 * back-compat for existing components.
 */
export interface PostStep {
  description: string;
  target: string;            // UI_Web_MVC | UI_Web_Blazor | UI_Desktop_WinForms | API_REST | AseXml_Generate | AseXml_Deliver | Db_SqlServer | Event_AzureServiceBus
  waitBeforeSeconds: number;
  role?: string;             // "Verification" (default) | "Action"
  webUi?: WebUiTestDefinition;
  desktopUi?: DesktopUiTestDefinition;
  api?: ApiTestDefinition;
  aseXml?: AseXmlTestDefinition;
  aseXmlDeliver?: AseXmlDeliveryTestDefinition;
  dbCheck?: DbCheckStepDefinition;
  eventAssert?: EventAssertStepDefinition;
}

/** @deprecated Use PostStep. Kept as an alias so existing imports keep working. */
export type VerificationStep = PostStep;

export interface AseXmlDeliveryTestDefinition {
  description: string;
  templateId: string;
  transactionType: string;
  fieldValues: Record<string, string>;
  endpointCode: string;
  validateAgainstSchema: boolean;
  /**
   * Canonical field since Slice 2. Legacy persisted test sets still read
   * `postDeliveryVerifications` on the wire — the backend promotes it into
   * this list on deserialise so the UI only ever sees `postSteps`.
   */
  postSteps: PostStep[];
  /** @deprecated Present only on payloads deserialised from very old backends. Prefer `postSteps`. */
  postDeliveryVerifications?: PostStep[];
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
  apiAssertions?: ApiAssertion[];
  captures?: ApiCapture[];
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
  /**
   * Loose metadata bag forwarded by the agents — currently used by the DB
   * check agent to attach the failing first row (`dbCheckRow`), additional
   * sample rows (`dbCheckRows`), and captured tokens (`capturedTokens`) for
   * the run-detail UI to render structured diagnostics.
   */
  metadata?: Record<string, unknown> | null;
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

export interface VerifyStepFilter {
  parentKind: string;
  parentStepIndex: number;
  postStepIndex: number;
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
  verifyStepFilter?: VerifyStepFilter;
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

export interface AseXmlVerificationConfigResponse {
  deferVerifications: boolean;
  verificationDeferThresholdSeconds: number;
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

export interface DataPackScriptReport {
  phase: string;
  subfolder: string;
  relativePath: string;
  status: 'Success' | 'Failed' | 'Skipped' | string;
  batchCount: number;
  elapsedMs: number;
  error: string | null;
}

export interface DataPackEnvReport {
  envKey: string;
  status: 'Ran' | 'SkippedNotConfigured' | 'SkippedOptOut' | 'SkippedNoConnection' | 'ConnectionFailed' | string;
  skipReason: string | null;
  error: string | null;
  scriptsTotal: number;
  scriptsExecuted: number;
  batchesExecuted: number;
  failures: number;
  scripts: DataPackScriptReport[];
}

export interface DataPackStartupReport {
  completedAtUtc: string;
  rootPath: string;
  rootExists: boolean;
  elapsed: string;
  envs: DataPackEnvReport[];
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

  // Seamless-auth pause (v8): set when this entry is parked on an outstanding
  // auth-refresh; the janitor releases the entry when the refresh terminates.
  authRefreshId?: string | null;
}

export type AuthSurface = 'Api' | 'WebBlazor' | 'WebMvc';

export interface AuthRefreshRequest {
  id: string;
  environmentKey: string;
  surface: AuthSurface;
  apiStackKey: string | null;
  agentId: string | null;
  requestedByRunId: string | null;
  status: 'Pending' | 'InProgress' | 'Completed' | 'Failed' | 'Cancelled' | string;
  autoAttemptCount: number;
  lastAttemptAt: string | null;
  createdAt: string;
  completedAt: string | null;
  errorMessage: string | null;
}

export interface AuthHealthAgentReport {
  agentId: string;
  agentName: string;
  fileExists: boolean;
  ageHours: number | null;
}

export type AuthHealthStatus = 'Missing' | 'Stale' | 'ExpiringSoon' | 'Fresh';

export interface AuthHealthSurfaceEntry {
  surface: AuthSurface;
  status: AuthHealthStatus;
  ageHours: number;
  ttlHours: number;
  agentReports: AuthHealthAgentReport[];
}

export interface AuthHealthEntry {
  envKey: string;
  envDisplayName: string;
  surfaces: AuthHealthSurfaceEntry[];
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

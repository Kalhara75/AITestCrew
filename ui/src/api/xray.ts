import { apiFetch } from "./client";

export interface XrayImportRequest {
  ticketKey: string;
  moduleId: string;
  testSetId: string;
}

export interface XrayMappingRow {
  sourceFragment: string;
  kind: string;
  target?: string | null;
  postStepType?: string | null;
  confidence: number;
  rationale: string;
  suggestedReqTitle?: string | null;
  suggestedExtensionPoint?: string | null;
  definition?: unknown;
}

export interface ProposedObjective {
  slug: string;
  title: string;
  rationale: string;
  assignedFragments: string[];
  mappingRows: XrayMappingRow[];
  preconditions: string[];
  testDataNotes?: string | null;
}

export interface XrayImportPreview {
  ticketKey: string;
  ticketSummary: string;
  moduleId: string;
  testSetId: string;
  proposedObjectives: ProposedObjective[];
  reviewCarefullyFlag: boolean;
  draftGapReqTitles: string[];
}

export interface XrayImportConfirmRequest {
  preview: XrayImportPreview;
  acceptedObjectiveSlugs: string[];
  collapseToSingle: boolean;
  titleOverrides: Record<string, string>;
  mergeRequests: Array<{ slugToMerge: string; mergeIntoSlug: string }>;
}

export interface XrayImportResult {
  persistedObjectiveIds: string[];
  gapReqPaths: string[];
  placeholderStepDescriptions: string[];
}

export interface CapabilityRegistryDto {
  stepTypes: string[];
  postStepTypes: string[];
  assertionPrimitives: string[];
  unsupportedExamples: string[];
}

/** POST /api/xray/import -- fetch + decompose + map, returns preview for QA review */
export const previewXrayImport = (req: XrayImportRequest) =>
  apiFetch<XrayImportPreview>("/xray/import", {
    method: "POST",
    body: JSON.stringify(req),
  });

/** POST /api/xray/import/confirm -- persist accepted objectives and write gap REQs */
export const confirmXrayImport = (req: XrayImportConfirmRequest) =>
  apiFetch<XrayImportResult>("/xray/import/confirm", {
    method: "POST",
    body: JSON.stringify(req),
  });

/** GET /api/capabilities -- AITestCrew capability registry (structured) */
export const getCapabilities = () =>
  apiFetch<CapabilityRegistryDto>("/capabilities");

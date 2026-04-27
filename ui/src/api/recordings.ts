import { apiFetch } from './client';

export type RecordingKind = 'Record' | 'RecordSetup' | 'RecordVerification' | 'AuthSetup';

export interface StartRecordingRequest {
  kind: RecordingKind;
  target: 'UI_Web_MVC' | 'UI_Web_Blazor' | 'UI_Desktop_WinForms';
  agentId?: string;
  moduleId?: string;
  testSetId?: string;
  caseName?: string;
  objectiveId?: string;
  verificationName?: string;
  waitBeforeSeconds?: number;
  deliveryStepIndex?: number;       // legacy — aseXML delivery parents only
  parentKind?: 'Api' | 'WebUi' | 'DesktopUi' | 'AseXml' | 'AseXmlDeliver';
  parentStepIndex?: number;
  environmentKey?: string;
}

export interface StartRecordingResponse {
  jobId: string;
  status: string;
  jobKind: string;
  targetType: string;
}

export const startRecording = (req: StartRecordingRequest) =>
  apiFetch<StartRecordingResponse>('/recordings', {
    method: 'POST',
    body: JSON.stringify(req),
  });

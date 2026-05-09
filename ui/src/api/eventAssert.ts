import { apiFetch } from './client';
import type { ServiceBusEntity } from '../types';

export interface PeekRequest {
  envKey?: string | null;
  connectionKey: string;
  entity: ServiceBusEntity;
  max?: number;
  correlationFilter?: string | null;
}

export interface PeekBody {
  format: string;     // "Json" | "Xml" | "Text" | "Binary"
  preview: string;    // truncated to 2 KB server-side
  length: number;
}

export interface PeekMessage {
  messageId?: string | null;
  correlationId?: string | null;
  subject?: string | null;
  contentType?: string | null;
  enqueuedTimeUtc: string;
  deliveryCount: number;
  applicationProperties: Record<string, string>;
  body: PeekBody;
}

export interface PeekResponse {
  messages: PeekMessage[];
  totalPeeked: number;
}

/**
 * Calls `POST /api/event-assert/peek`. Returns up to N messages WITHOUT
 * consuming them — peek-mode only on the SDK side, so the editor's "Peek
 * messages" button can never accidentally drain a real test run.
 */
export const peekServiceBusMessages = (req: PeekRequest) =>
  apiFetch<PeekResponse>('/event-assert/peek', {
    method: 'POST',
    body: JSON.stringify(req),
  });

/** Lists configured Service Bus connection keys for the editor dropdown. */
export const getServiceBusConnections = (envKey?: string | null) => {
  const qs = envKey ? `?envKey=${encodeURIComponent(envKey)}` : '';
  return apiFetch<{ keys: string[] }>(`/event-assert/connections${qs}`);
};

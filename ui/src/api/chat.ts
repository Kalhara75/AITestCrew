import { apiFetch } from './client';

export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
}

export interface ChatRequestContext {
  moduleId?: string;
  testSetId?: string;
}

export interface ChatAction {
  kind: string;
  path?: string;
  title?: string;
  data?: unknown;
}

export interface ChatResponse {
  reply: string;
  actions: ChatAction[];
}

export const sendChat = (messages: ChatMessage[], context?: ChatRequestContext) =>
  apiFetch<ChatResponse>('/chat/message', {
    method: 'POST',
    body: JSON.stringify({ messages, context }),
  });

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
  summary?: string;
  data?: unknown;
}

export interface ChatResponse {
  reply: string;
  actions: ChatAction[];
  conversationId?: string;
}

export interface ConversationSummary {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
}

export interface PersistedChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  actions?: ChatAction[];
  createdAt: string;
}

export interface ConversationDetail {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
  messages: PersistedChatMessage[];
}

/**
 * Send one chat turn. When `conversationId` is supplied, the server loads
 * history from the DB and persists this turn into that thread. When omitted,
 * the server creates a new conversation and returns its id on the response.
 */
export const sendChat = (params: {
  message: string;
  conversationId?: string;
  context?: ChatRequestContext;
}) =>
  apiFetch<ChatResponse>('/chat/message', {
    method: 'POST',
    body: JSON.stringify({
      message: params.message,
      conversationId: params.conversationId,
      context: params.context,
    }),
  });

export const listConversations = () =>
  apiFetch<ConversationSummary[]>('/chat/conversations');

export const createConversation = (title?: string) =>
  apiFetch<ConversationSummary>('/chat/conversations', {
    method: 'POST',
    body: JSON.stringify({ title: title ?? null }),
  });

export const getConversation = (id: string) =>
  apiFetch<ConversationDetail>(`/chat/conversations/${encodeURIComponent(id)}`);

export const deleteConversation = (id: string) =>
  apiFetch<void>(`/chat/conversations/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  });

export const renameConversation = (id: string, title: string) =>
  apiFetch<ConversationSummary>(`/chat/conversations/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify({ title }),
  });

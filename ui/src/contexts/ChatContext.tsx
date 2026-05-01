import { createContext, useContext, useState, useCallback, useMemo, useEffect } from 'react';
import type { ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  sendChat,
  listConversations,
  getConversation,
  createConversation,
  deleteConversation as apiDeleteConversation,
  renameConversation as apiRenameConversation,
} from '../api/chat';
import type { ChatAction, ChatRequestContext, ConversationSummary } from '../api/chat';
import { useAuth } from './AuthContext';

export interface ChatMessageEntry {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  actions?: ChatAction[];
  pending?: boolean;
}

interface ChatContextValue {
  isOpen: boolean;
  toggle: () => void;
  open: () => void;
  close: () => void;

  /** True when the server is in SQLite + auth mode so conversations are persisted per-user. */
  persistenceEnabled: boolean;
  conversations: ConversationSummary[];
  isLoadingConversations: boolean;
  activeConversationId: string | null;
  selectConversation: (id: string | null) => void;
  newConversation: () => Promise<void>;
  deleteConversation: (id: string) => Promise<void>;
  renameConversation: (id: string, title: string) => Promise<void>;

  messages: ChatMessageEntry[];
  isLoadingMessages: boolean;

  isSending: boolean;
  error: string | null;
  send: (text: string, context?: ChatRequestContext) => Promise<void>;
  /** Start a fresh thread (alias of newConversation, kept for the existing "clear" button). */
  clear: () => Promise<void>;
}

const ChatContext = createContext<ChatContextValue | null>(null);

export function useChat() {
  const ctx = useContext(ChatContext);
  if (!ctx) throw new Error('useChat must be used within ChatProvider');
  return ctx;
}

function newId() {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) return crypto.randomUUID();
  return Math.random().toString(36).slice(2);
}

export function ChatProvider({ children }: { children: ReactNode }) {
  const { user, authRequired, isLoading: authLoading } = useAuth();
  const queryClient = useQueryClient();

  // Persistence is only available when the server is in SQLite + auth mode AND
  // a user is logged in. In file-storage mode the picker UI is hidden and the
  // drawer falls back to an in-memory transcript (lost on refresh — same as
  // legacy behaviour).
  const persistenceEnabled = authRequired && !!user;
  const userKey = user?.id ?? null;
  const canFetch = !authLoading && persistenceEnabled;

  const [isOpen, setIsOpen] = useState(false);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [optimisticUserMessage, setOptimisticUserMessage] = useState<ChatMessageEntry | null>(null);
  const [legacyMessages, setLegacyMessages] = useState<ChatMessageEntry[]>([]);
  const [isSending, setIsSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reset client state when the user changes (login/logout).
  useEffect(() => {
    setActiveConversationId(null);
    setOptimisticUserMessage(null);
    setLegacyMessages([]);
    setError(null);
  }, [userKey, persistenceEnabled]);

  const conversationsQuery = useQuery({
    queryKey: ['chat', 'conversations', userKey],
    queryFn: listConversations,
    enabled: canFetch,
    staleTime: 30_000,
    retry: false,
  });
  const conversations = canFetch ? (conversationsQuery.data ?? []) : [];

  // Auto-select the most recent conversation once the list loads.
  useEffect(() => {
    if (!activeConversationId && conversations.length > 0) {
      setActiveConversationId(conversations[0].id);
    }
  }, [conversations, activeConversationId]);

  const conversationQuery = useQuery({
    queryKey: ['chat', 'conversation', userKey, activeConversationId],
    queryFn: () => getConversation(activeConversationId!),
    enabled: canFetch && !!activeConversationId,
    retry: false,
  });

  const messages = useMemo<ChatMessageEntry[]>(() => {
    if (!persistenceEnabled) {
      const list = [...legacyMessages];
      if (optimisticUserMessage) list.push(optimisticUserMessage);
      return list;
    }
    const persisted: ChatMessageEntry[] = (conversationQuery.data?.messages ?? []).map(m => ({
      id: m.id,
      role: m.role,
      content: m.content,
      actions: m.actions ?? undefined,
    }));
    if (optimisticUserMessage && optimisticUserMessage.role === 'user') {
      const alreadyPersisted = persisted.some(
        m => m.role === 'user' && m.content === optimisticUserMessage.content,
      );
      if (!alreadyPersisted) persisted.push(optimisticUserMessage);
    }
    return persisted;
  }, [persistenceEnabled, legacyMessages, conversationQuery.data, optimisticUserMessage]);

  const toggle = useCallback(() => setIsOpen(v => !v), []);
  const open = useCallback(() => setIsOpen(true), []);
  const close = useCallback(() => setIsOpen(false), []);

  const selectConversation = useCallback((id: string | null) => {
    setActiveConversationId(id);
    setOptimisticUserMessage(null);
    setError(null);
  }, []);

  const newConversation = useCallback(async () => {
    setError(null);
    if (!persistenceEnabled) {
      setLegacyMessages([]);
      setActiveConversationId(null);
      setOptimisticUserMessage(null);
      return;
    }
    try {
      const created = await createConversation();
      setActiveConversationId(created.id);
      setOptimisticUserMessage(null);
      await queryClient.invalidateQueries({ queryKey: ['chat', 'conversations', userKey] });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create conversation');
    }
  }, [persistenceEnabled, queryClient, userKey]);

  const deleteConversation = useCallback(async (id: string) => {
    try {
      await apiDeleteConversation(id);
      if (activeConversationId === id) {
        setActiveConversationId(null);
        setOptimisticUserMessage(null);
      }
      await queryClient.invalidateQueries({ queryKey: ['chat', 'conversations', userKey] });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete conversation');
    }
  }, [activeConversationId, queryClient, userKey]);

  const renameConversation = useCallback(async (id: string, title: string) => {
    try {
      await apiRenameConversation(id, title);
      await queryClient.invalidateQueries({ queryKey: ['chat', 'conversations', userKey] });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to rename conversation');
    }
  }, [queryClient, userKey]);

  const send = useCallback(async (text: string, context?: ChatRequestContext) => {
    const trimmed = text.trim();
    if (!trimmed || isSending) return;

    const optimistic: ChatMessageEntry = {
      id: newId(),
      role: 'user',
      content: trimmed,
    };
    setOptimisticUserMessage(optimistic);
    setError(null);
    setIsSending(true);

    try {
      const res = await sendChat({
        message: trimmed,
        conversationId: activeConversationId ?? undefined,
        context,
      });
      if (persistenceEnabled) {
        // Server may have created a new conversation — capture its id.
        if (res.conversationId && res.conversationId !== activeConversationId) {
          setActiveConversationId(res.conversationId);
        }
        await queryClient.invalidateQueries({ queryKey: ['chat', 'conversations', userKey] });
        const targetId = res.conversationId ?? activeConversationId;
        if (targetId) {
          await queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', userKey, targetId] });
        }
        setOptimisticUserMessage(null);
      } else {
        // Stateless / file-mode fallback: keep an in-memory transcript.
        setLegacyMessages(prev => [
          ...prev,
          { id: optimistic.id, role: 'user', content: trimmed },
          { id: newId(), role: 'assistant', content: res.reply ?? '', actions: res.actions ?? [] },
        ]);
        setOptimisticUserMessage(null);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Chat request failed');
    } finally {
      setIsSending(false);
    }
  }, [isSending, activeConversationId, queryClient, userKey, persistenceEnabled]);

  const clear = useCallback(async () => {
    await newConversation();
  }, [newConversation]);

  const value = useMemo<ChatContextValue>(() => ({
    isOpen, toggle, open, close,
    persistenceEnabled,
    conversations,
    isLoadingConversations: conversationsQuery.isLoading,
    activeConversationId,
    selectConversation,
    newConversation,
    deleteConversation,
    renameConversation,
    messages,
    isLoadingMessages: persistenceEnabled && conversationQuery.isLoading && !!activeConversationId,
    isSending, error, send, clear,
  }), [
    isOpen, toggle, open, close,
    persistenceEnabled,
    conversations, conversationsQuery.isLoading,
    activeConversationId, selectConversation, newConversation, deleteConversation, renameConversation,
    messages, conversationQuery.isLoading,
    isSending, error, send, clear,
  ]);

  return <ChatContext.Provider value={value}>{children}</ChatContext.Provider>;
}

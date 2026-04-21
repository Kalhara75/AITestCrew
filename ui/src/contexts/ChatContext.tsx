import { createContext, useContext, useState, useCallback, useMemo } from 'react';
import type { ReactNode } from 'react';
import { sendChat } from '../api/chat';
import type { ChatAction, ChatRequestContext } from '../api/chat';

export interface ChatMessageEntry {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  actions?: ChatAction[];
}

interface ChatContextValue {
  isOpen: boolean;
  toggle: () => void;
  open: () => void;
  close: () => void;
  messages: ChatMessageEntry[];
  isSending: boolean;
  error: string | null;
  send: (text: string, context?: ChatRequestContext) => Promise<void>;
  clear: () => void;
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
  const [isOpen, setIsOpen] = useState(false);
  const [messages, setMessages] = useState<ChatMessageEntry[]>([]);
  const [isSending, setIsSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const toggle = useCallback(() => setIsOpen(v => !v), []);
  const open = useCallback(() => setIsOpen(true), []);
  const close = useCallback(() => setIsOpen(false), []);
  const clear = useCallback(() => {
    setMessages([]);
    setError(null);
  }, []);

  const send = useCallback(async (text: string, context?: ChatRequestContext) => {
    const trimmed = text.trim();
    if (!trimmed || isSending) return;

    const userEntry: ChatMessageEntry = { id: newId(), role: 'user', content: trimmed };
    const pendingHistory = [...messages, userEntry].map(m => ({ role: m.role, content: m.content }));

    setMessages(prev => [...prev, userEntry]);
    setError(null);
    setIsSending(true);

    try {
      const res = await sendChat(pendingHistory, context);
      setMessages(prev => [...prev, {
        id: newId(),
        role: 'assistant',
        content: res.reply || '',
        actions: res.actions ?? [],
      }]);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Chat request failed');
    } finally {
      setIsSending(false);
    }
  }, [messages, isSending]);

  const value = useMemo<ChatContextValue>(() => ({
    isOpen, toggle, open, close, messages, isSending, error, send, clear,
  }), [isOpen, toggle, open, close, messages, isSending, error, send, clear]);

  return <ChatContext.Provider value={value}>{children}</ChatContext.Provider>;
}

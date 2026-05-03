import { useEffect, useRef } from 'react';
import type { ChatMessageEntry } from '../../contexts/ChatContext';
import { tokens } from './tokens';
import { MessageBubble } from './MessageBubble';
import { TypingIndicator } from './TypingIndicator';
import { EmptyState } from './EmptyState';

interface Props {
  messages: ChatMessageEntry[];
  isSending: boolean;
  isLoadingMessages: boolean;
  error: string | null;
  onSuggestionPick: (text: string, autoSend?: boolean) => void;
}

export function MessageList({
  messages, isSending, isLoadingMessages, error, onSuggestionPick,
}: Props) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length, isSending]);

  return (
    <div
      ref={ref}
      role="log"
      aria-live="polite"
      aria-label="Assistant conversation"
      style={{
        flex: 1,
        overflowY: 'auto',
        padding: 16,
        display: 'flex',
        flexDirection: 'column',
        gap: 12,
        minWidth: 0,
        background: tokens.color.bg,
      }}
    >
      {isLoadingMessages && messages.length === 0 && (
        <div style={{ fontSize: tokens.font.size.sm, color: tokens.color.textFaint }}>
          Loading conversation…
        </div>
      )}
      {!isLoadingMessages && messages.length === 0 && (
        <EmptyState onPick={onSuggestionPick} />
      )}
      {messages.map(m => <MessageBubble key={m.id} message={m} />)}
      {isSending && <TypingIndicator />}
      {error && (
        <div style={{
          padding: '10px 12px',
          borderRadius: tokens.radius.lg,
          background: tokens.color.dangerBg,
          border: `1px solid ${tokens.color.dangerBorder}`,
          color: tokens.color.danger,
          fontSize: tokens.font.size.md,
        }}>
          {error}
        </div>
      )}
    </div>
  );
}

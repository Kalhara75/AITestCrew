import { useCallback, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { useChat } from '../contexts/ChatContext';
import type { ChatRequestContext } from '../api/chat';
import { ChatHeader } from '../components/chat/ChatHeader';
import { MessageList } from '../components/chat/MessageList';
import { Composer } from '../components/chat/Composer';
import { tokens } from '../components/chat/tokens';

function useReferrerContext(): ChatRequestContext | undefined {
  // The full-page route has no module/testset in its own URL. We could
  // pull the previous location off history state, but for simplicity we
  // omit context — users navigate here intentionally for long sessions.
  return undefined;
}

export function AssistantPage() {
  const {
    messages, isSending, error, send,
    persistenceEnabled,
    conversations, activeConversationId,
    selectConversation, newConversation, deleteConversation, renameConversation,
    isLoadingMessages, clear,
  } = useChat();

  // useLocation reference so the page re-renders if URL state changes
  // (matters if we later thread context through router state).
  useLocation();

  const [input, setInput] = useState('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const context = useReferrerContext();

  const activeConversation = conversations.find(c => c.id === activeConversationId);

  const handleSubmit = useCallback(async () => {
    const text = input;
    if (!text.trim()) return;
    setInput('');
    await send(text, context);
  }, [input, send, context]);

  const onSuggestionPick = useCallback((text: string, autoSend?: boolean) => {
    setInput(text);
    if (autoSend) {
      void (async () => {
        setInput('');
        await send(text, context);
      })();
    } else {
      requestAnimationFrame(() => textareaRef.current?.focus());
    }
  }, [send, context]);

  return (
    <section
      aria-label="AI assistant"
      style={{
        maxWidth: 960,
        margin: '0 auto',
        background: tokens.color.bg,
        border: `1px solid ${tokens.color.border}`,
        borderRadius: tokens.radius.lg,
        boxShadow: tokens.shadow.sm,
        overflow: 'hidden',
        display: 'flex',
        flexDirection: 'column',
        // Fill the viewport below the global 56px header (28px page padding
        // top+bottom = 56px combined; 56 header + 56 padding = 112).
        height: 'calc(100vh - 112px)',
        minHeight: 480,
      }}
    >
      <ChatHeader
        mode="page"
        persistenceEnabled={persistenceEnabled}
        activeConversation={activeConversation}
        conversations={conversations}
        hasMessages={messages.length > 0}
        onSelect={selectConversation}
        onNew={newConversation}
        onDelete={deleteConversation}
        onRename={renameConversation}
        onClear={clear}
      />

      <MessageList
        messages={messages}
        isSending={isSending}
        isLoadingMessages={isLoadingMessages}
        error={error}
        onSuggestionPick={onSuggestionPick}
      />

      <Composer
        value={input}
        onChange={setInput}
        onSubmit={handleSubmit}
        isSending={isSending}
        context={context}
        textareaRef={textareaRef}
      />
    </section>
  );
}

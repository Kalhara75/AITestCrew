import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useChat } from '../../contexts/ChatContext';
import type { ChatRequestContext } from '../../api/chat';
import { tokens } from './tokens';
import { transitions } from './motion';
import { ChatHeader } from './ChatHeader';
import { MessageList } from './MessageList';
import { Composer } from './Composer';
import { useDragResize } from './hooks/useDragResize';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts';

function useCurrentContext(): ChatRequestContext | undefined {
  const { pathname } = useLocation();
  const match = /^\/modules\/([^/]+)(?:\/testsets\/([^/]+))?/.exec(pathname);
  if (!match) return undefined;
  return { moduleId: match[1], testSetId: match[2] };
}

export function ChatDrawer() {
  const {
    isOpen, open, close, messages, isSending, error, send,
    persistenceEnabled,
    conversations, activeConversationId,
    selectConversation, newConversation, deleteConversation, renameConversation,
    isLoadingMessages, clear,
  } = useChat();

  const [input, setInput] = useState('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const navigate = useNavigate();
  const context = useCurrentContext();
  const activeConversation = conversations.find(c => c.id === activeConversationId);

  const { value: drawerWidth, setValue: setDrawerWidth, handleProps: dragHandleProps, isDragging } = useDragResize({
    axis: 'x',
    invert: true,
    min: tokens.layout.drawerMin,
    max: tokens.layout.drawerMax,
    defaultValue: tokens.layout.drawerDefault,
    storageKey: 'chat.drawerWidth',
  });

  // Focus the composer when the drawer transitions from closed → open.
  const wasOpen = useRef(isOpen);
  useEffect(() => {
    if (isOpen && !wasOpen.current) {
      // Wait one frame so the slide-in is mounted before focusing.
      requestAnimationFrame(() => textareaRef.current?.focus());
    }
    wasOpen.current = isOpen;
  }, [isOpen]);

  const handleSubmit = useCallback(async () => {
    const text = input;
    if (!text.trim()) return;
    setInput('');
    await send(text, context);
  }, [input, send, context]);

  const onSuggestionPick = useCallback((text: string, autoSend?: boolean) => {
    setInput(text);
    if (autoSend) {
      // Defer so React commits the input clear after we kick off send.
      void (async () => {
        setInput('');
        await send(text, context);
      })();
    } else {
      requestAnimationFrame(() => textareaRef.current?.focus());
    }
  }, [send, context]);

  const onSnapWide = useCallback(() => {
    setDrawerWidth(tokens.layout.drawerWide);
  }, [setDrawerWidth]);

  const onPopOut = useCallback(() => {
    navigate('/assistant');
    close();
  }, [navigate, close]);

  const shortcuts = useMemo(() => ({
    'Escape': () => { if (isOpen) close(); },
    'Mod+/':       (e: KeyboardEvent) => {
      e.preventDefault();
      open();
      requestAnimationFrame(() => textareaRef.current?.focus());
    },
    'Mod+Shift+K': (e: KeyboardEvent) => {
      e.preventDefault();
      void newConversation();
    },
  }), [isOpen, open, close, newConversation]);
  useKeyboardShortcuts(shortcuts);

  return (
    <aside
      aria-label="AI assistant"
      aria-hidden={!isOpen}
      style={{
        position: 'fixed',
        top: tokens.layout.headerHeight,
        right: 0,
        bottom: 0,
        width: drawerWidth,
        background: tokens.color.bg,
        borderLeft: `1px solid ${tokens.color.border}`,
        boxShadow: '-2px 0 8px rgba(15, 23, 42, 0.06)',
        display: 'flex',
        flexDirection: 'column',
        zIndex: 50,
        transform: isOpen ? 'translateX(0)' : 'translateX(100%)',
        transition: isDragging ? 'none' : `transform ${transitions.base}`,
        pointerEvents: isOpen ? 'auto' : 'none',
        minWidth: 0,
      }}
    >
      <DrawerResizeHandle dragProps={dragHandleProps} active={isDragging} onSnapWide={onSnapWide} />

      <ChatHeader
        mode="drawer"
        persistenceEnabled={persistenceEnabled}
        activeConversation={activeConversation}
        conversations={conversations}
        hasMessages={messages.length > 0}
        onSelect={selectConversation}
        onNew={newConversation}
        onDelete={deleteConversation}
        onRename={renameConversation}
        onClear={clear}
        onClose={close}
        onSnapWide={onSnapWide}
        onPopOut={onPopOut}
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
    </aside>
  );
}

function DrawerResizeHandle({
  dragProps, active, onSnapWide,
}: {
  dragProps: ReturnType<typeof useDragResize>['handleProps'];
  active: boolean;
  onSnapWide: () => void;
}) {
  return (
    <div
      {...dragProps}
      aria-label="Resize assistant width"
      onDoubleClick={onSnapWide}
      title="Drag to resize · double-click for wide mode"
      style={{
        ...dragProps.style,
        position: 'absolute',
        top: 0,
        bottom: 0,
        left: -3,
        width: 6,
        background: 'transparent',
        zIndex: 51,
      }}
    >
      <div style={{
        position: 'absolute',
        top: '50%',
        left: 2,
        transform: 'translateY(-50%)',
        width: 2,
        height: 32,
        borderRadius: 2,
        background: active ? tokens.color.accent : tokens.color.borderStrong,
        opacity: active ? 1 : 0,
        transition: 'opacity 120ms ease, background 120ms ease',
      }} />
    </div>
  );
}

import { useEffect, useMemo, useRef, useState } from 'react';
import type { ConversationSummary } from '../../api/chat';
import { tokens } from './tokens';
import { SearchIcon } from './icons';
import { formatRelativeTime } from './utils/formatRelativeTime';

interface PickerProps {
  conversations: ConversationSummary[];
  activeId: string | null | undefined;
  onSelect: (id: string) => void;
  onNew: () => void;
  onDelete: (id: string) => void;
  onRename: (id: string, title: string) => void;
  onClose: () => void;
}

export function ConversationPicker({
  conversations, activeId, onSelect, onNew, onDelete, onRename, onClose,
}: PickerProps) {
  const popoverRef = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState('');
  const showSearch = conversations.length > 5;

  useEffect(() => {
    function onDocClick(e: MouseEvent) {
      if (popoverRef.current && !popoverRef.current.contains(e.target as Node)) onClose();
    }
    document.addEventListener('mousedown', onDocClick);
    return () => document.removeEventListener('mousedown', onDocClick);
  }, [onClose]);

  const filtered = useMemo(() => {
    if (!query.trim()) return conversations;
    const q = query.toLowerCase();
    return conversations.filter(c => c.title.toLowerCase().includes(q));
  }, [conversations, query]);

  return (
    <div ref={popoverRef} style={{
      position: 'absolute',
      top: '100%',
      left: 12,
      right: 12,
      marginTop: 4,
      zIndex: 60,
      background: tokens.color.bg,
      border: `1px solid ${tokens.color.border}`,
      borderRadius: tokens.radius.lg,
      boxShadow: tokens.shadow.lg,
      maxHeight: 360,
      display: 'flex',
      flexDirection: 'column',
      overflow: 'hidden',
    }}>
      <button
        onClick={() => { onNew(); onClose(); }}
        style={{
          width: '100%',
          textAlign: 'left',
          padding: '10px 12px',
          fontSize: tokens.font.size.md,
          background: 'transparent',
          border: 'none',
          borderBottom: `1px solid ${tokens.color.surfaceAlt}`,
          color: tokens.color.accent,
          fontWeight: tokens.font.weight.semi,
          cursor: 'pointer',
        }}>
        + New chat
      </button>

      {showSearch && (
        <div style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '6px 10px',
          borderBottom: `1px solid ${tokens.color.surfaceAlt}`,
          color: tokens.color.textFaint,
        }}>
          <SearchIcon size={12} />
          <input
            autoFocus
            value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder="Search conversations…"
            style={{
              flex: 1,
              border: 'none',
              outline: 'none',
              fontSize: tokens.font.size.sm,
              padding: '2px 0',
              background: 'transparent',
            }}
          />
        </div>
      )}

      <div style={{ overflowY: 'auto' }}>
        {filtered.length === 0 ? (
          <div style={{ padding: 12, fontSize: tokens.font.size.sm, color: tokens.color.textFaint }}>
            {conversations.length === 0 ? 'No saved conversations yet.' : 'No matches.'}
          </div>
        ) : (
          filtered.map(c => (
            <ConversationRow
              key={c.id}
              conversation={c}
              isActive={c.id === activeId}
              onSelect={() => { onSelect(c.id); onClose(); }}
              onDelete={() => onDelete(c.id)}
              onRename={(title) => onRename(c.id, title)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function ConversationRow({
  conversation, isActive, onSelect, onDelete, onRename,
}: {
  conversation: ConversationSummary;
  isActive: boolean;
  onSelect: () => void;
  onDelete: () => void;
  onRename: (title: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(conversation.title);

  function commit() {
    const next = draft.trim();
    setEditing(false);
    if (next && next !== conversation.title) onRename(next);
    else setDraft(conversation.title);
  }

  return (
    <div style={{
      display: 'flex',
      alignItems: 'center',
      gap: 8,
      padding: '8px 12px',
      background: isActive ? tokens.color.surfaceAlt : 'transparent',
      borderBottom: `1px solid ${tokens.color.surface}`,
    }}>
      {editing ? (
        <input
          autoFocus
          value={draft}
          onChange={e => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={e => {
            if (e.key === 'Enter') {
              e.preventDefault();
              commit();
            } else if (e.key === 'Escape') {
              e.stopPropagation();
              setDraft(conversation.title);
              setEditing(false);
            }
          }}
          style={{
            flex: 1,
            fontSize: tokens.font.size.md,
            padding: '3px 6px',
            border: `1px solid ${tokens.color.borderStrong}`,
            borderRadius: tokens.radius.sm,
            outline: 'none',
          }}
        />
      ) : (
        <button
          onClick={onSelect}
          onDoubleClick={() => setEditing(true)}
          title={conversation.title + ' — double-click to rename'}
          style={{
            flex: 1,
            textAlign: 'left',
            background: 'transparent',
            border: 'none',
            padding: 0,
            cursor: 'pointer',
            fontSize: tokens.font.size.md,
            color: tokens.color.text,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {conversation.title}
          <span style={{
            display: 'block',
            fontSize: tokens.font.size.xs,
            color: tokens.color.textFaint,
            fontWeight: tokens.font.weight.regular,
          }}>
            {formatRelativeTime(conversation.updatedAt)} · {conversation.messageCount} msg
          </span>
        </button>
      )}
      <button
        onClick={(e) => { e.stopPropagation(); onDelete(); }}
        title="Delete conversation"
        aria-label={`Delete conversation ${conversation.title}`}
        style={{
          background: 'transparent',
          border: 'none',
          color: tokens.color.textFaint,
          fontSize: 14,
          cursor: 'pointer',
          padding: '2px 6px',
          borderRadius: tokens.radius.sm,
        }}
      >
        ×
      </button>
    </div>
  );
}

import { useState } from 'react';
import type { ConversationSummary } from '../../api/chat';
import { tokens } from './tokens';
import { iconBtnStyle, ghostBtnStyle } from './styles';
import {
  ChevronDownIcon, CloseIcon, ExpandIcon, SidebarWideIcon,
} from './icons';
import { ConversationPicker } from './ConversationPicker';

export type ChatHeaderMode = 'drawer' | 'page';

interface Props {
  mode?: ChatHeaderMode;
  persistenceEnabled: boolean;
  activeConversation: ConversationSummary | undefined;
  conversations: ConversationSummary[];
  hasMessages: boolean;

  onSelect: (id: string | null) => void;
  onNew: () => Promise<void>;
  onDelete: (id: string) => Promise<void>;
  onRename: (id: string, title: string) => Promise<void>;
  onClear: () => Promise<void>;

  onClose?: () => void;
  onSnapWide?: () => void;
  onPopOut?: () => void;
}

export function ChatHeader({
  mode = 'drawer',
  persistenceEnabled, activeConversation, conversations, hasMessages,
  onSelect, onNew, onDelete, onRename, onClear,
  onClose, onSnapWide, onPopOut,
}: Props) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const title = activeConversation?.title || 'Assistant';

  return (
    <header style={{
      display: 'flex',
      alignItems: 'center',
      gap: 10,
      padding: '12px 16px',
      borderBottom: `1px solid ${tokens.color.border}`,
      position: 'relative',
      background: tokens.color.bg,
    }}>
      {persistenceEnabled ? (
        <button
          onClick={() => setPickerOpen(v => !v)}
          aria-haspopup="listbox"
          aria-expanded={pickerOpen}
          title="Switch conversation"
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
            background: 'transparent',
            border: 'none',
            padding: 0,
            cursor: 'pointer',
            fontSize: tokens.font.size.lg,
            fontWeight: tokens.font.weight.bold,
            color: tokens.color.text,
            letterSpacing: 0.3,
            maxWidth: 240,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{title}</span>
          <span style={{ color: tokens.color.textFaint, display: 'inline-flex' }}>
            <ChevronDownIcon size={12} />
          </span>
        </button>
      ) : (
        <strong style={{
          fontSize: tokens.font.size.lg,
          color: tokens.color.text,
          letterSpacing: 0.3,
        }}>
          Assistant
        </strong>
      )}

      <span style={{
        fontSize: tokens.font.size.xs,
        color: tokens.color.textFaint,
        fontWeight: tokens.font.weight.medium,
      }}>
        read-only preview
      </span>

      <div style={{ marginLeft: 'auto', display: 'flex', gap: 6, alignItems: 'center' }}>
        {persistenceEnabled ? (
          <button
            onClick={() => { void onNew(); setPickerOpen(false); }}
            style={iconBtnStyle}
            title="Start a new conversation (Ctrl+Shift+K)"
            aria-label="New conversation"
          >
            + new
          </button>
        ) : (
          hasMessages && (
            <button
              onClick={() => { void onClear(); }}
              style={iconBtnStyle}
              title="Clear conversation"
              aria-label="Clear conversation"
            >
              clear
            </button>
          )
        )}

        {mode === 'drawer' && onSnapWide && (
          <button
            onClick={onSnapWide}
            style={ghostBtnStyle}
            title="Wide mode"
            aria-label="Snap drawer to wide mode"
          >
            <SidebarWideIcon size={14} />
          </button>
        )}

        {mode === 'drawer' && onPopOut && (
          <button
            onClick={onPopOut}
            style={ghostBtnStyle}
            title="Open in full page"
            aria-label="Open assistant in full page"
          >
            <ExpandIcon size={14} />
          </button>
        )}

        {mode === 'drawer' && onClose && (
          <button
            onClick={onClose}
            style={ghostBtnStyle}
            title="Close (Esc)"
            aria-label="Close assistant"
          >
            <CloseIcon size={14} />
          </button>
        )}
      </div>

      {pickerOpen && persistenceEnabled && (
        <ConversationPicker
          conversations={conversations}
          activeId={activeConversation?.id}
          onSelect={onSelect}
          onNew={() => { void onNew(); }}
          onDelete={(id) => { void onDelete(id); }}
          onRename={(id, t) => { void onRename(id, t); }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </header>
  );
}

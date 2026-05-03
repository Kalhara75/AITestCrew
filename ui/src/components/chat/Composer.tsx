import { useCallback, useRef } from 'react';
import type { KeyboardEvent } from 'react';
import type { ChatRequestContext } from '../../api/chat';
import { tokens } from './tokens';
import { primaryBtnStyle } from './styles';
import { useDragResize } from './hooks/useDragResize';
import { useAutoGrowTextarea } from './hooks/useAutoGrowTextarea';
import { ComposerToolbar } from './ComposerToolbar';

interface Props {
  value: string;
  onChange: (next: string) => void;
  onSubmit: () => void;
  isSending: boolean;
  context: ChatRequestContext | undefined;
  // Imperative focus from outside (Ctrl+/)
  textareaRef?: React.RefObject<HTMLTextAreaElement | null>;
}

const MIN_HEIGHT = tokens.layout.composerMinPx;
const MAX_HEIGHT = tokens.layout.composerMaxPx;
const DEFAULT_HEIGHT = tokens.layout.composerDefaultPx;

export function Composer({ value, onChange, onSubmit, isSending, context, textareaRef }: Props) {
  const internalRef = useRef<HTMLTextAreaElement>(null);
  const ref = textareaRef ?? internalRef;

  const { value: composerHeight, handleProps: dragProps, isDragging } = useDragResize({
    axis: 'y',
    invert: true,
    min: MIN_HEIGHT,
    max: MAX_HEIGHT,
    defaultValue: DEFAULT_HEIGHT,
    storageKey: 'chat.composerHeight',
  });

  // Auto-grow within the available container height (capped to composerHeight).
  useAutoGrowTextarea(ref, {
    value,
    minHeight: 56,
    maxHeight: Math.max(56, composerHeight - 28), // leave room for footer row
  });

  const onKeyDown = useCallback((e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (!isSending && value.trim().length > 0) onSubmit();
    }
  }, [isSending, value, onSubmit]);

  const insertAtCursor = useCallback((text: string, opts?: { selectInside?: boolean }) => {
    const el = ref.current;
    if (!el) {
      onChange(value + text);
      return;
    }
    const start = el.selectionStart ?? value.length;
    const end = el.selectionEnd ?? value.length;
    const next = value.slice(0, start) + text + value.slice(end);
    onChange(next);
    queueMicrotask(() => {
      const target = ref.current;
      if (!target) return;
      target.focus();
      if (opts?.selectInside) {
        // For ```\n|\n``` style insertions, place cursor on the empty middle line.
        const middle = start + text.indexOf('\n\n') + 1;
        target.setSelectionRange(middle, middle);
      } else {
        const pos = start + text.length;
        target.setSelectionRange(pos, pos);
      }
    });
  }, [ref, value, onChange]);

  const onPasteSql = useCallback(async () => {
    try {
      const clip = await navigator.clipboard.readText();
      const trimmed = clip.replace(/\s+$/, '');
      insertAtCursor(`\n\`\`\`sql\n${trimmed}\n\`\`\`\n`);
    } catch {
      insertAtCursor('\n```sql\n\n```\n', { selectInside: true });
    }
  }, [insertAtCursor]);

  const charCount = value.length;
  const lineCount = value === '' ? 0 : value.split('\n').length;
  const showCounter = charCount > 200 || lineCount > 3;

  return (
    <div
      style={{
        position: 'relative',
        borderTop: `1px solid ${tokens.color.border}`,
        background: tokens.color.bg,
        height: composerHeight,
        minHeight: MIN_HEIGHT,
        display: 'flex',
        flexDirection: 'column',
        flexShrink: 0,
      }}
    >
      <DragHandle dragProps={dragProps} active={isDragging} />

      <div style={{
        flex: 1,
        display: 'flex',
        flexDirection: 'column',
        padding: 12,
        gap: 6,
        minHeight: 0,
      }}>
        <textarea
          ref={ref}
          value={value}
          onChange={e => onChange(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Ask about modules, test sets, environments, agents…"
          style={{
            flex: 1,
            width: '100%',
            resize: 'none',
            border: `1px solid ${tokens.color.border}`,
            borderRadius: tokens.radius.lg,
            padding: '8px 10px',
            fontSize: tokens.font.size.md,
            fontFamily: tokens.font.sans,
            lineHeight: tokens.font.lineHeight.body,
            outline: 'none',
            boxSizing: 'border-box',
            color: tokens.color.text,
            background: tokens.color.bg,
            minHeight: 56,
          }}
        />

        <ComposerToolbar
          context={context}
          onInsert={insertAtCursor}
          onPasteSql={onPasteSql}
          onClear={() => onChange('')}
          hasInput={value.length > 0}
        />

        <div style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          fontSize: tokens.font.size.xs,
          color: tokens.color.textFaint,
        }}>
          <span>Enter to send · Shift+Enter for newline</span>
          {showCounter && (
            <span aria-live="polite">
              · {charCount} chars · {lineCount} line{lineCount === 1 ? '' : 's'}
            </span>
          )}
          <button
            onClick={onSubmit}
            disabled={isSending || value.trim().length === 0}
            style={{
              ...primaryBtnStyle,
              marginLeft: 'auto',
              cursor: isSending || value.trim().length === 0 ? 'not-allowed' : 'pointer',
              opacity:  isSending || value.trim().length === 0 ? 0.5 : 1,
            }}
          >
            Send
          </button>
        </div>
      </div>
    </div>
  );
}

function DragHandle({
  dragProps, active,
}: {
  dragProps: ReturnType<typeof useDragResize>['handleProps'];
  active: boolean;
}) {
  return (
    <div
      {...dragProps}
      aria-label="Resize composer height"
      style={{
        ...dragProps.style,
        position: 'absolute',
        top: -3,
        left: 0,
        right: 0,
        height: 6,
        background: 'transparent',
      }}
    >
      <div style={{
        position: 'absolute',
        top: 2,
        left: '50%',
        transform: 'translateX(-50%)',
        width: 36,
        height: 2,
        borderRadius: 2,
        background: active ? tokens.color.accent : tokens.color.border,
        opacity: active ? 1 : 0,
        transition: 'opacity 120ms ease, background 120ms ease',
      }}
        className="chat-composer-grip"
      />
    </div>
  );
}

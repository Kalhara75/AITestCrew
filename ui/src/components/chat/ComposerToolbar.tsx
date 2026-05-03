import type { ChatRequestContext } from '../../api/chat';
import { tokens } from './tokens';
import { CodeIcon, SqlIcon, TrashIcon } from './icons';

interface Props {
  context: ChatRequestContext | undefined;
  onInsert: (text: string, opts?: { selectInside?: boolean }) => void;
  onPasteSql: () => Promise<void>;
  onClear: () => void;
  hasInput: boolean;
}

export function ComposerToolbar({ context, onInsert, onPasteSql, onClear, hasInput }: Props) {
  return (
    <div style={{
      display: 'flex',
      alignItems: 'center',
      gap: 6,
      flexWrap: 'wrap',
    }}>
      <ToolbarBtn
        title="Insert code block"
        onClick={() => onInsert('\n```\n\n```\n', { selectInside: true })}
      >
        <CodeIcon size={12} /> code
      </ToolbarBtn>
      <ToolbarBtn
        title="Paste clipboard wrapped as SQL"
        onClick={() => { void onPasteSql(); }}
      >
        <SqlIcon size={12} /> sql
      </ToolbarBtn>
      <ToolbarBtn
        title="Clear input"
        onClick={onClear}
        disabled={!hasInput}
      >
        <TrashIcon size={12} /> clear
      </ToolbarBtn>

      {context && (context.moduleId || context.testSetId) && (
        <span
          title="The assistant sees this page context with each message"
          style={{
            marginLeft: 'auto',
            display: 'inline-flex',
            alignItems: 'center',
            gap: 4,
            padding: '2px 8px',
            background: tokens.color.accentBg,
            color: tokens.color.accent,
            border: `1px solid ${tokens.color.accentBorder}`,
            borderRadius: tokens.radius.pill,
            fontSize: tokens.font.size.xs,
            fontWeight: tokens.font.weight.medium,
          }}
        >
          {context.moduleId && <>module <code>{context.moduleId}</code></>}
          {context.moduleId && context.testSetId && <span>·</span>}
          {context.testSetId && <>test set <code>{context.testSetId}</code></>}
        </span>
      )}
    </div>
  );
}

function ToolbarBtn({
  children, title, onClick, disabled,
}: {
  children: React.ReactNode;
  title: string;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      aria-label={title}
      disabled={disabled}
      style={{
        background: 'transparent',
        border: `1px solid ${tokens.color.border}`,
        color: disabled ? tokens.color.textFaint : tokens.color.textMuted,
        fontSize: tokens.font.size.xs,
        padding: '3px 8px',
        borderRadius: tokens.radius.sm,
        cursor: disabled ? 'not-allowed' : 'pointer',
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
      }}
    >
      {children}
    </button>
  );
}

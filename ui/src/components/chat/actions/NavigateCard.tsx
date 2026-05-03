import { useNavigate } from 'react-router-dom';
import { useChat } from '../../../contexts/ChatContext';
import { tokens } from '../tokens';
import { ArrowUpRightIcon } from '../icons';

export function NavigateCard({ path }: { path: string }) {
  const navigate = useNavigate();
  const { close } = useChat();
  return (
    <button
      onClick={() => { navigate(path); close(); }}
      style={{
        textAlign: 'left',
        background: tokens.color.accentBg,
        border: `1px solid ${tokens.color.accentBorder}`,
        borderLeft: `4px solid ${tokens.color.navTint}`,
        color: tokens.color.accent,
        padding: '8px 12px',
        borderRadius: tokens.radius.lg,
        fontSize: tokens.font.size.md,
        cursor: 'pointer',
        fontWeight: tokens.font.weight.medium,
        display: 'inline-flex',
        alignItems: 'center',
        gap: 8,
      }}
    >
      <ArrowUpRightIcon />
      <span>
        Open <code style={{ fontFamily: tokens.font.mono, fontSize: tokens.font.size.sm }}>{path}</code>
      </span>
    </button>
  );
}

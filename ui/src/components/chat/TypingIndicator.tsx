import { tokens } from './tokens';

export function TypingIndicator() {
  return (
    <div
      role="status"
      aria-live="polite"
      aria-label="Assistant is thinking"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        paddingLeft: 4,
        color: tokens.color.textFaint,
        fontSize: tokens.font.size.xs,
      }}
    >
      <Dot delay={0} />
      <Dot delay={0.18} />
      <Dot delay={0.36} />
      <span style={{ marginLeft: 4 }}>thinking</span>
    </div>
  );
}

function Dot({ delay }: { delay: number }) {
  return (
    <span
      className="chat-typing-dot"
      style={{
        width: 6,
        height: 6,
        borderRadius: '50%',
        background: tokens.color.textFaint,
        animationDelay: `${delay}s`,
        display: 'inline-block',
      }}
    />
  );
}

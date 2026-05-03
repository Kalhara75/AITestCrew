import type { ChatMessageEntry } from '../../contexts/ChatContext';
import { tokens } from './tokens';
import { bubbleUserStyle, bubbleAssistantStyle } from './styles';
import { Markdown } from './markdown';
import { ActionCard } from './actions';

export function MessageBubble({ message }: { message: ChatMessageEntry }) {
  const isUser = message.role === 'user';
  return (
    <div
      className="chat-fade-in"
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 6,
        alignItems: isUser ? 'flex-end' : 'flex-start',
        minWidth: 0,
      }}
    >
      <div style={{
        ...(isUser ? bubbleUserStyle : bubbleAssistantStyle),
        maxWidth: '88%',
        minWidth: 0,
      }}>
        {isUser
          ? <span style={{ whiteSpace: 'pre-wrap' }}>{message.content}</span>
          : message.content
            ? <Markdown>{message.content}</Markdown>
            : <span style={{ color: tokens.color.textFaint }}>(empty reply)</span>}
      </div>

      {!isUser && message.actions && message.actions.length > 0 && (
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 6,
          width: '88%',
          minWidth: 0,
        }}>
          {message.actions.map((a, i) => <ActionCard key={i} action={a} />)}
        </div>
      )}
    </div>
  );
}

import type { ChatAction } from '../../../api/chat';
import { NavigateCard } from './NavigateCard';
import { ConfirmRunCard } from './ConfirmRunCard';
import { ConfirmCreateCard } from './ConfirmCreateCard';
import { ConfirmRecordCard } from './ConfirmRecordCard';
import { ConfirmCreatePostStepCard } from './ConfirmCreatePostStepCard';
import { DataCard } from '../DataView';

export function ActionCard({ action }: { action: ChatAction }) {
  if (action.kind === 'navigate' && action.path)
    return <NavigateCard path={action.path} />;
  if (action.kind === 'showData')
    return <DataCard title={action.title} data={action.data} />;
  if (action.kind === 'confirmRun')
    return <ConfirmRunCard summary={action.summary} data={action.data} />;
  if (action.kind === 'confirmCreate')
    return <ConfirmCreateCard summary={action.summary} data={action.data} />;
  if (action.kind === 'confirmRecord')
    return <ConfirmRecordCard summary={action.summary} data={action.data} />;
  if (action.kind === 'confirmCreatePostStep')
    return <ConfirmCreatePostStepCard summary={action.summary} data={action.data} />;
  return null;
}

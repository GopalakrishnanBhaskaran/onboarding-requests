import { createContext, useContext } from 'react';
import type { ActionItem } from './types';

/** What waits on the signed-in person (computed by the server), shared by the navigation count and the pages. */
export interface ActionQueue {
  items: ActionItem[] | null;
  refresh: () => void;
}

export const QueueContext = createContext<ActionQueue>({ items: null, refresh: () => undefined });

export function useActionQueue(): ActionQueue {
  return useContext(QueueContext);
}

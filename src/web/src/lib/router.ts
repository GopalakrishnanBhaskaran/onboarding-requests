import { useEffect, useState } from 'react';

/** Minimal hash router: #/requests, #/requests/new, #/requests/:id, #/requests/:id/edit, #/queue, #/sheet, #/contacts, #/escalations, #/uploads, #/operator. */
export type Route =
  | { page: 'requests' }
  | { page: 'new' }
  | { page: 'request'; requestId: string }
  | { page: 'edit'; requestId: string }
  | { page: 'queue' }
  | { page: 'sheet' }
  | { page: 'contacts' }
  | { page: 'escalations' }
  | { page: 'uploads' }
  | { page: 'operator' };

export function parse(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent);
  switch (parts[0]) {
    case 'queue':
      return { page: 'queue' };
    case 'sheet':
      return { page: 'sheet' };
    case 'contacts':
      return { page: 'contacts' };
    case 'escalations':
      return { page: 'escalations' };
    case 'uploads':
      return { page: 'uploads' };
    case 'operator':
      return { page: 'operator' };
    case 'requests':
      if (parts[1] === 'new') return { page: 'new' };
      if (parts[1] && parts[2] === 'edit') return { page: 'edit', requestId: parts[1] };
      if (parts[1]) return { page: 'request', requestId: parts[1] };
      return { page: 'requests' };
    default:
      return { page: 'requests' };
  }
}

export function href(route: Route): string {
  switch (route.page) {
    case 'new':
      return '#/requests/new';
    case 'request':
      return `#/requests/${encodeURIComponent(route.requestId)}`;
    case 'edit':
      return `#/requests/${encodeURIComponent(route.requestId)}/edit`;
    case 'requests':
      return '#/requests';
    default:
      return `#/${route.page}`;
  }
}

export function navigate(route: Route): void {
  window.location.hash = href(route);
}

export function useRoute(): Route {
  const [route, setRoute] = useState(() => parse(window.location.hash));
  useEffect(() => {
    const onChange = () => setRoute(parse(window.location.hash));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return route;
}

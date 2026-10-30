import type { UploadHistory, UploadKind, UploadView } from './types';

/** F4: the two uploads, as the page names them (decision 5: the I2 upload is the people inventory). */
export const UPLOADS: { kind: UploadKind; button: string; title: string; key: string }[] = [
  { kind: 'mapping', button: 'Upload PMO mapping sheet', title: 'PMO mapping sheet', key: 'TenantId + Domain + ResourceGroupType + Environment + EntraGroupName + AccessLevel' },
  { kind: 'inventory', button: 'Upload people inventory', title: 'People inventory', key: 'EnterpriseId' },
];

export const ACCEPT = '.xlsx,.csv';

/** Only .xlsx and .csv are sent; anything else (macro-enabled .xlsm/.xlsb, .xls) is refused before upload, and again by the API. */
export function acceptedFile(name: string): boolean {
  return /\.(xlsx|csv)$/i.test(name.trim());
}

/** "3 added, 1 updated, 10 unchanged, 0 retired; 2 missing from the file (left as they are)". */
export function summary(view: UploadView): string {
  const c = view.upload.counts;
  const missingLeft = view.missing - c.retired;
  return `${c.added} added, ${c.updated} updated, ${c.unchanged} unchanged, ${c.retired} retired`
    + (missingLeft > 0 ? `; ${missingLeft} missing from the file (left as they are)` : '');
}

/** Apply only a file that validated against the current version, and only when it changes something. */
export function canApply(view: UploadView | null, current: number): boolean {
  if (!view || view.upload.mode !== 'validate' || view.upload.status !== 'Valid' || view.baseVersion !== current) return false;
  const c = view.upload.counts;
  return c.added + c.updated + c.retired > 0;
}

/** One-click rollback: only the current version, while it is applied. */
export function rollbackTarget(history: UploadHistory): number | null {
  const head = history.versions.find((v) => v.number === history.current);
  return head && head.state === 'Applied' ? head.number : null;
}

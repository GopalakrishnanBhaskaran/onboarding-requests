import { describe, expect, it } from 'vitest';
import { acceptedFile, canApply, rollbackTarget, summary } from './lib/uploads';
import type { UploadHistory, UploadView } from './lib/types';

const view = (o: Partial<UploadView['upload']> & { missing?: number; baseVersion?: number } = {}): UploadView => ({
  upload: {
    uploadId: 'upl-1', mode: o.mode ?? 'validate', status: o.status ?? 'Valid', version: null,
    counts: o.counts ?? { added: 2, updated: 1, unchanged: 3, retired: 0 }, errors: [],
  },
  baseVersion: o.baseVersion ?? 1, missing: o.missing ?? 0, diff: [],
});

describe('F4 Data uploads page', () => {
  it('accepts only .xlsx and .csv (macro-enabled and binary formats never leave the browser)', () => {
    expect(acceptedFile('mapping.xlsx')).toBe(true);
    expect(acceptedFile('People.CSV')).toBe(true);
    for (const name of ['mapping.xlsm', 'mapping.xlsb', 'mapping.xls', 'mapping.xltm', 'mapping.txt', 'mapping.xlsx.exe']) expect(acceptedFile(name)).toBe(false);
  });

  it('applies only a valid file checked against the current version that changes something', () => {
    expect(canApply(view(), 1)).toBe(true);
    expect(canApply(view(), 2)).toBe(false); // someone applied meanwhile: validate again
    expect(canApply(view({ status: 'Invalid' }), 1)).toBe(false);
    expect(canApply(view({ counts: { added: 0, updated: 0, unchanged: 5, retired: 0 } }), 1)).toBe(false);
    expect(canApply(null, 1)).toBe(false);
  });

  it('says what is missing from the file and left as it is', () => {
    expect(summary(view({ missing: 3, counts: { added: 1, updated: 0, unchanged: 2, retired: 1 } })))
      .toBe('1 added, 0 updated, 2 unchanged, 1 retired; 2 missing from the file (left as they are)');
  });

  it('rolls back only the current applied version', () => {
    const v = (n: number, state: 'Applied' | 'RolledBack') => ({
      version: `v${n}`, number: n, state, uploadedAt: '', counts: { added: 0, updated: 0, unchanged: 0, retired: 0, missing: 0 }, retireMissing: false, channel: 'App',
    });
    const h = (current: number, versions: ReturnType<typeof v>[]): UploadHistory => ({ kind: 'Mapping', current, columns: [], versions });
    expect(rollbackTarget(h(2, [v(2, 'Applied'), v(1, 'Applied')]))).toBe(2);
    expect(rollbackTarget(h(2, [v(2, 'RolledBack')]))).toBeNull();
    expect(rollbackTarget(h(0, []))).toBeNull();
  });
});

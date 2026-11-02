import { describe, expect, it } from 'vitest';
import { correctionFor, missingTasks, privilegedJobRows, returnedSiblings } from './lib/resubmit';
import type { IntakeFields, MappingRow, StatusLine } from './lib/types';

const line = (over: Partial<StatusLine>): StatusLine => ({
  requestId: 'r1', projectId: 'P', requester: 'alice@contoso.example', status: 'Returned', currentStage: 'Returned',
  waitingOn: 'Requester', waitingOnLabel: 'Requester', done: ['Submitted', 'AI review'], next: 'Requester corrects', timeInStageSeconds: 60,
  timeInStage: '1m', adoLink: null, domainId: null, resource: 'SAMPLE PEST Finance (fin)', accessLevel: 'Data Engineer',
  updatedAt: '2026-11-02T10:00:00Z', bundleId: 'b1', ...over,
});
const row = (over: Partial<MappingRow>): MappingRow => ({
  rowId: 'R', resource: 'SAMPLE PEST Central hub / platform (ph)', accessLevel: 'Data Engineer', entraGroup: 'sample-dataengineers',
  role: 'CONTRIBUTOR', privileged: true, ...over,
} as MappingRow);

describe('returnedSiblings', () => {
  it('lists the other returned requests of the same submission, by resource', () => {
    const lines = [
      line({ requestId: 'me', resource: 'SAMPLE PEST Central hub / platform (ph)' }),
      line({ requestId: 'r2', resource: 'SAMPLE PEST Supply (sup)' }),
      line({ requestId: 'r3', resource: 'SAMPLE PEST Acquisition (acq)' }),
      line({ requestId: 'done', status: 'AwaitingOwner' }),
      line({ requestId: 'other', bundleId: 'b2' }),
      line({ requestId: 'single', bundleId: null }),
    ];
    expect(returnedSiblings(lines, { id: 'me', bundleId: 'b1' }).map((x) => x.requestId)).toEqual(['r3', 'r2']);
  });

  it('has none for a request submitted on its own', () => {
    expect(returnedSiblings([line({ requestId: 'r2', bundleId: null })], { id: 'me', bundleId: null })).toEqual([]);
  });
});

describe('correctionFor', () => {
  it('shares joiner, justification and Owner but keeps the sibling’s own resource, level and environment', () => {
    const shared: IntakeFields = {
      enterpriseId: 'sample.joiner@company.example', firstName: 'Sample', lastName: 'Joiner', resource: 'SAMPLE ph', accessLevel: 'Data Engineer',
      environment: 'Dev', justification: 'SAMPLE: to build and run ingestion pipelines.', owner: 'sample.owner@company.example', guest: false,
    };
    const sibling: IntakeFields = { ...shared, resource: 'SAMPLE fin', accessLevel: 'Developer', environment: 'Test', justification: 'old', owner: 'old@x.example' };
    expect(correctionFor(sibling, shared)).toEqual({ ...shared, resource: 'SAMPLE fin', accessLevel: 'Developer', environment: 'Test' });
  });
});

describe('privileged job-role rows (same rule as the AI review)', () => {
  it('picks Privileged rows whose level is a job role, not an admin level', () => {
    const rows = [row({ rowId: 'A' }), row({ rowId: 'B', accessLevel: 'Admin' }), row({ rowId: 'C', privileged: false })];
    expect(privilegedJobRows(rows).map((r) => r.rowId)).toEqual(['A']);
  });

  it('asks for tasks until the justification names one', () => {
    expect(missingTasks('Data Engineer on Databricks PEST Dev, PEST Finance (fin) (Dev).')).toBe(true);
    expect(missingTasks('Why: to build and run ingestion pipelines in Databricks.')).toBe(false);
    expect(missingTasks('Why: support the platform on call.')).toBe(false);
  });
});

import { describe, expect, it } from 'vitest';
import { ALL_ENVIRONMENTS, environmentsFor, levelsByResource, levelsComplete, plan, settleLevels, unionOptions, unresolvedFor } from './lib/access';
import type { ResolveResult, ResolvedAccess } from './lib/types';

const g = (groupName: string, accessLevel: string, sources: string[], status: ResolvedAccess['status'] = 'Auto', groupObjectId = ''): ResolvedAccess =>
  ({ groupObjectId, groupName, accessLevel, sources, status });
const res = (environment: string, access: ResolvedAccess[], selectedResources = ['PH']): ResolveResult =>
  ({ tenantId: 'SAMPLE', environment, selectedResources, resolvedAccess: access, mappingQualityIssues: [], mappingVersion: 'v1' });

describe('request form: an access level per resource instead of Entra groups (7 Nov 2026)', () => {
  const dev = res('Dev', [
    g('grp-dev-ph-de', 'Data Engineer', ['PH'], 'Auto', 'oid-1'), g('grp-dev-ph-reader', 'Support Reader', ['PH']),
    g('grp-dev-ph-de-2', 'data engineer', ['PH'], 'NeedsHumanApproval'), g('grp-dev-ph-admin', 'Admin', ['PH'], 'Unresolved'),
    g('grp-dev-fin-view', 'Viewer', ['FIN']),
  ], ['PH', 'FIN']);

  it('offers each level once per resource, whatever its spelling, never an unresolved one', () => {
    expect(levelsByResource([dev], ['PH', 'FIN'])).toEqual([
      { resource: 'PH', levels: ['Data Engineer', 'Support Reader'] },
      { resource: 'FIN', levels: ['Viewer'] },
    ]);
  });

  it('preselects a resource\'s only level and keeps a choice that is still offered', () => {
    const offered = levelsByResource([dev], ['PH', 'FIN']);
    expect(settleLevels({}, offered)).toEqual({ FIN: 'Viewer' });
    expect(settleLevels({ PH: 'support reader', FIN: 'Gone' }, offered)).toEqual({ PH: 'Support Reader', FIN: 'Viewer' });
    expect(levelsComplete({ FIN: 'Viewer' }, offered)).toBe(false); // PH still to choose
    expect(levelsComplete({ PH: 'Data Engineer', FIN: 'Viewer' }, offered)).toBe(true);
  });

  it('requests every requestable group of the chosen levels and nothing else (two resources, two levels)', () => {
    const p = plan([dev], { PH: 'Data Engineer', FIN: 'Viewer' });
    expect(p).toHaveLength(1);
    expect(p[0].groups).toEqual(['oid-1', 'grp-dev-ph-de-2', 'grp-dev-fin-view']); // object id when known; never Admin or the reader
    expect(unresolvedFor([dev], { PH: 'Admin' }).map((a) => a.groupName)).toEqual(['grp-dev-ph-admin']);
    expect(plan([dev], {})).toEqual([]);
  });
});

describe('request form: all environments (7 Nov 2026)', () => {
  it('stands for every offered environment', () => {
    expect(environmentsFor(ALL_ENVIRONMENTS, ['Dev', 'Test', 'PreProd', 'Prod'])).toEqual(['Dev', 'Test', 'PreProd', 'Prod']);
    expect(environmentsFor('Dev', ['Dev', 'Test'])).toEqual(['Dev']);
    expect(environmentsFor('', ['Dev'])).toEqual([]);
  });

  it('makes one submission per environment and leaves out an environment without that level', () => {
    const r = [res('Dev', [g('grp-dev-de', 'Data Engineer', ['PH'])]), res('Test', [g('grp-tst-reader', 'Support Reader', ['PH'])]),
      res('Prod', [g('grp-prd-de', 'Data Engineer', ['PH'])], ['PH', 'ACQ'])];
    const p = plan(r, { PH: 'Data Engineer' });
    expect(p.map((x) => [x.environment, x.groups, x.selectedResources])).toEqual([['Dev', ['grp-dev-de'], ['PH']], ['Prod', ['grp-prd-de'], ['PH', 'ACQ']]]);
  });

  it('lists each resource once across environments', () => {
    expect(unionOptions([['PH', 'ACQ'], ['PH', 'FIN'], []])).toEqual(['PH', 'ACQ', 'FIN']);
  });
});

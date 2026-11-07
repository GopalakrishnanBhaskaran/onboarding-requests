import type { ResolveResult, ResolvedAccess } from './types';

/**
 * Request form (7 Nov 2026, Gokul): the requester picks an access level per resource, not Entra groups. The groups come
 * from the mapping sheet (resolved by the server for the chosen resources and environment); the form requests every
 * requestable group of the chosen level, one request each. "All environments" does the same in every environment the
 * project offers, one submission per environment.
 */
export const ALL_ENVIRONMENTS = '__all__';

/** The environments a choice stands for: every offered one for "All environments", else the one chosen. */
export function environmentsFor(choice: string, offered: string[]): string[] {
  if (!choice) return [];
  return choice === ALL_ENVIRONMENTS ? [...offered] : [choice];
}

/** How the bundle submit names a resolved group: the object id, else the group name. */
export const groupKey = (a: ResolvedAccess) => a.groupObjectId || a.groupName;

/** A group the requester may ask for (unresolved groups are never granted and are escalated by the server). */
export const requestable = (a: ResolvedAccess) => a.status !== 'Unresolved';

const norm = (s: string | undefined) => (s ?? '').trim().toLowerCase();

/** The chosen level per resource (resource name as the server lists it in a group's sources). */
export type LevelChoice = Record<string, string>;

/** Per chosen resource, the access levels its requestable groups offer (each level once, first-seen spelling). */
export function levelsByResource(results: ResolveResult[], resources: string[]): { resource: string; levels: string[] }[] {
  return resources.map((resource) => {
    const seen = new Map<string, string>();
    for (const r of results) for (const a of r.resolvedAccess)
      if (requestable(a) && a.sources.includes(resource) && !seen.has(norm(a.accessLevel))) seen.set(norm(a.accessLevel), a.accessLevel);
    return { resource, levels: [...seen.values()] };
  });
}

/** Keep a choice that is still offered; preselect a resource's only level; otherwise the requester chooses. */
export function settleLevels(current: LevelChoice, offered: { resource: string; levels: string[] }[]): LevelChoice {
  const next: LevelChoice = {};
  for (const { resource, levels } of offered) {
    const kept = levels.find((l) => norm(l) === norm(current[resource]));
    if (kept) next[resource] = kept;
    else if (levels.length === 1) next[resource] = levels[0];
  }
  return next;
}

/** Every resource that has a requestable level has one chosen (a resource with none cannot be requested here). */
export function levelsComplete(choice: LevelChoice, offered: { resource: string; levels: string[] }[]): boolean {
  const open = offered.filter((o) => o.levels.length > 0);
  return open.length > 0 && open.every((o) => Boolean(choice[o.resource]));
}

const wanted = (a: ResolvedAccess, choice: LevelChoice) => a.sources.some((s) => choice[s] && norm(choice[s]) === norm(a.accessLevel));

/** One submission per environment: the requestable groups of the chosen levels (environments with none are left out). */
export function plan(results: ResolveResult[], choice: LevelChoice): { environment: string; selectedResources: string[]; groups: string[]; access: ResolvedAccess[] }[] {
  return results
    .map((r) => {
      const access = r.resolvedAccess.filter((a) => requestable(a) && wanted(a, choice));
      return { environment: r.environment, selectedResources: r.selectedResources, groups: [...new Set(access.map(groupKey))], access };
    })
    .filter((x) => x.groups.length > 0);
}

/** Unresolved groups of the chosen levels (shown as a warning: the server escalates them). */
export function unresolvedFor(results: ResolveResult[], choice: LevelChoice): ResolvedAccess[] {
  return results.flatMap((r) => r.resolvedAccess.filter((a) => !requestable(a) && wanted(a, choice)));
}

/** Resource options across environments: each one once, in first-seen order. */
export function unionOptions(perEnvironment: string[][]): string[] {
  return [...new Set(perEnvironment.flat())];
}

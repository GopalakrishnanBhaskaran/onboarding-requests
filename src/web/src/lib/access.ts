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

/** Several resources: a resource can be left out ("Not needed"), so nothing is requested that was not chosen. */
export const NOT_NEEDED = '__none__';

/** Per chosen resource, the access levels its requestable groups offer (each level once, first-seen spelling). */
export function levelsByResource(results: ResolveResult[], resources: string[]): { resource: string; levels: string[] }[] {
  return resources.map((resource) => {
    const seen = new Map<string, string>();
    for (const r of results) for (const a of r.resolvedAccess)
      if (requestable(a) && a.sources.includes(resource) && !seen.has(norm(a.accessLevel))) seen.set(norm(a.accessLevel), a.accessLevel);
    return { resource, levels: [...seen.values()] };
  });
}

/**
 * Keep a choice that is still offered (or "Not needed"). Preselect only one resource's only level: with several resources
 * nothing is preselected, so no access is requested that the requester did not pick (review 7 Nov 2026, finding 8).
 */
export function settleLevels(current: LevelChoice, offered: { resource: string; levels: string[] }[]): LevelChoice {
  const next: LevelChoice = {};
  for (const { resource, levels } of offered) {
    const kept = current[resource] === NOT_NEEDED && offered.length > 1 ? NOT_NEEDED : levels.find((l) => norm(l) === norm(current[resource]));
    if (kept) next[resource] = kept;
  }
  if (offered.length === 1 && offered[0].levels.length === 1 && !next[offered[0].resource]) next[offered[0].resource] = offered[0].levels[0];
  return next;
}

/** Every resource with a requestable level has a level or "Not needed", and at least one has a level. */
export function levelsComplete(choice: LevelChoice, offered: { resource: string; levels: string[] }[]): boolean {
  const open = offered.filter((o) => o.levels.length > 0);
  return open.length > 0 && open.every((o) => Boolean(choice[o.resource])) && open.some((o) => choice[o.resource] && choice[o.resource] !== NOT_NEEDED);
}

/** The levels actually requested (without "Not needed"). */
export function chosenLevels(choice: LevelChoice): string[] {
  return [...new Set(Object.values(choice).filter((l) => l && l !== NOT_NEEDED))];
}

const wanted = (a: ResolvedAccess, choice: LevelChoice) =>
  a.sources.some((s) => choice[s] && choice[s] !== NOT_NEEDED && norm(choice[s]) === norm(a.accessLevel));

/**
 * One submission per environment: the requestable groups of the chosen levels (environments with none are left out).
 * A group is requested once: a mapping row with no environment is offered in every environment, and asking for it in each
 * made the review return all but the first as duplicates (7 Nov 2026). It goes with the first environment it appears in.
 */
export function plan(results: ResolveResult[], choice: LevelChoice): { environment: string; selectedResources: string[]; groups: string[]; access: ResolvedAccess[] }[] {
  const taken = new Set<string>();
  return results
    .map((r) => {
      const access = r.resolvedAccess.filter((a) => requestable(a) && wanted(a, choice) && !taken.has(groupKey(a)));
      const unique = access.filter((a, i) => access.findIndex((b) => groupKey(b) === groupKey(a)) === i);
      unique.forEach((a) => taken.add(groupKey(a)));
      return { environment: r.environment, selectedResources: r.selectedResources, groups: unique.map(groupKey), access: unique };
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

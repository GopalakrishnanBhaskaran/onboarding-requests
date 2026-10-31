/**
 * F1 Resource* selection (v1.1). Options come from the server (the signed-in user's tenant only); this module keeps the
 * two modes consistent: a dropdown (one choice) and checkboxes (several), with "All Domains + PH + ACQ".
 * Selecting All ticks every item; unticking any item clears All. Both modes send the same selection to the API.
 */
export const ALL = 'All Domains + PH + ACQ';

export type SelectionMode = 'dropdown' | 'checkbox';

/** The real options (without All), in server order. */
export function realOptions(options: string[]): string[] {
  return options.filter((o) => o !== ALL);
}

/** Checkbox mode: the selection after ticking or unticking one option. */
export function toggle(selected: string[], option: string, checked: boolean, options: string[]): string[] {
  const real = realOptions(options);
  if (option === ALL) return checked ? [...real, ALL] : [];
  const next = checked ? [...new Set([...selected.filter((s) => s !== ALL), option])] : selected.filter((s) => s !== option && s !== ALL);
  const ordered = real.filter((o) => next.includes(o));
  // Every item ticked one by one counts as All too.
  return ordered.length === real.length && real.length > 0 ? [...ordered, ALL] : ordered;
}

/** Dropdown mode: one value (All stands for every option); "" = nothing chosen yet. */
export function fromDropdown(value: string, options: string[]): string[] {
  if (!value) return [];
  return value === ALL ? [...realOptions(options), ALL] : [value];
}

/** What the API gets: the chosen options (All expanded, so dropdown and checkboxes send exactly the same list). */
export function forApi(selected: string[], options: string[]): string[] {
  return realOptions(options).filter((o) => selected.includes(o));
}

/** Leaving checkbox mode for the dropdown keeps a single choice, or All; several items become "nothing chosen". */
export function toDropdown(selected: string[]): string {
  if (selected.includes(ALL)) return ALL;
  return selected.length === 1 ? selected[0] : '';
}

/** Submit is blocked until at least one resource is chosen and at least one requestable group is ticked. */
export function canSubmit(selected: string[], groups: string[]): boolean {
  return selected.some((s) => s !== ALL) && groups.length > 0;
}

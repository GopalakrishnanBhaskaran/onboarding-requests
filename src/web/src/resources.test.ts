import { describe, expect, it } from 'vitest';
import { ALL, canSubmit, forApi, fromDropdown, toDropdown, toggle } from './lib/resources';

const options = ['Finance', 'HR', 'PH', 'ACQ', ALL];

describe('F1 Resource* selection', () => {
  it('selecting All ticks every item', () => {
    expect(toggle([], ALL, true, options)).toEqual(['Finance', 'HR', 'PH', 'ACQ', ALL]);
  });

  it('All then untick one clears All and keeps the rest', () => {
    const all = toggle([], ALL, true, options);
    const next = toggle(all, 'HR', false, options);
    expect(next).toEqual(['Finance', 'PH', 'ACQ']);
    expect(next).not.toContain(ALL);
    expect(forApi(next, options)).toEqual(['Finance', 'PH', 'ACQ']);
  });

  it('ticking every item one by one counts as All', () => {
    let s: string[] = [];
    for (const o of ['ACQ', 'Finance', 'PH', 'HR']) s = toggle(s, o, true, options);
    expect(s).toEqual(['Finance', 'HR', 'PH', 'ACQ', ALL]);
  });

  it('unticking All clears everything', () => {
    expect(toggle(toggle([], ALL, true, options), ALL, false, options)).toEqual([]);
  });

  it('dropdown and checkbox send the same selection to the API', () => {
    expect(forApi(fromDropdown('HR', options), options)).toEqual(forApi(toggle([], 'HR', true, options), options));
    expect(forApi(fromDropdown(ALL, options), options)).toEqual(forApi(toggle([], ALL, true, options), options));
    expect(toDropdown(toggle([], ALL, true, options))).toBe(ALL);
    expect(toDropdown(['Finance', 'HR'])).toBe('');
  });

  it('blocks submit until a resource and a group are chosen', () => {
    expect(canSubmit([], ['g'])).toBe(false);
    expect(canSubmit(['Finance'], [])).toBe(false);
    expect(canSubmit([ALL], ['g'])).toBe(false); // only the All marker, no real option
    expect(canSubmit(['Finance'], ['g'])).toBe(true);
    expect(fromDropdown('', options)).toEqual([]); // "Choose…"
  });
});

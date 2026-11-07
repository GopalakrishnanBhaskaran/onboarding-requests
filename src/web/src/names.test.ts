import { describe, expect, it } from 'vitest';
import { nameError } from './lib/names';

describe('joiner names (same rule as the API)', () => {
  it('accepts letters with spaces, dots, hyphens and apostrophes, in any script', () => {
    for (const n of ['Dfsa', 'Anne-Marie', "O'Neil", 'J. R.', 'José', 'Gopalakrishnan  Baskaran', '']) expect(nameError(n)).toBeUndefined();
  });
  it('refuses digits and other signs, and a sign first', () => {
    for (const n of ['Test01', 'live_test', 'Doe, Jane', '-Anne', 'Jane@x']) expect(nameError(n)).toMatch(/Letters, spaces/);
  });
});

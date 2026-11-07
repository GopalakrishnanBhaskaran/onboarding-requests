/** Same rule as the API (RequestService NameFormat, after spaces are collapsed): a letter first, then letters, spaces, dots, hyphens, apostrophes. */
const NAME = /^\p{L}[\p{L} .'-]{0,99}$/u;

/** The form says what is wrong before submitting (7 Nov 2026: with All environments the API refused it once per environment). */
export function nameError(v: string): string | undefined {
  const t = v.trim().replace(/ +/g, ' '); // the API collapses spaces only
  return t && !NAME.test(t) ? 'Letters, spaces, dots, hyphens and apostrophes only (no digits or other signs), e.g. Anne-Marie O\'Neil.' : undefined;
}

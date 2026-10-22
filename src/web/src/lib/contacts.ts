// Contact inventory lists as typed on the page. The server applies the same rule and is the authority (MailAddresses.cs).

/** Most addresses one team list may hold (ACS sends one mail to at most 50 recipients). */
export const MAX_PER_LIST = 50;

const ADDRESS = /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;

/** The plain address, or null: "Name <a@b.com>", "mailto:" and the guest sign-in forms count as the mailbox. */
export function normalizeMail(text: string): string | null {
  let t = text.trim();
  if (!t) return null;
  const lt = t.lastIndexOf('<');
  if (lt >= 0 && t.endsWith('>')) t = t.slice(lt + 1, -1).trim();
  if (t.toLowerCase().startsWith('mailto:')) t = t.slice(7);
  t = t.replace(/^["' ]+|["' ]+$/g, '').toLowerCase();
  const ext = t.indexOf('#ext#@');
  if (ext > 0) {
    const local = t.slice(0, ext);
    const u = local.lastIndexOf('_');
    if (u > 0) t = `${local.slice(0, u)}@${local.slice(u + 1)}`;
  } else {
    const hash = t.indexOf('#');
    if (hash > 0 && ['live.com', 'gmail.com', 'mail', 'sts'].includes(t.slice(0, hash)) && t.slice(hash + 1).includes('@')) t = t.slice(hash + 1);
  }
  return t.length <= 254 && ADDRESS.test(t) ? t : null;
}

/** Semicolons (or new lines) separate entries; commas only between bare addresses ("Doe, Jane <jane@x.com>" is one entry). */
export function parseList(text: string): { valid: string[]; invalid: string[] } {
  const valid: string[] = [];
  const invalid: string[] = [];
  for (const raw of text.split(/[;\n\r]/).map((x) => x.trim()).filter(Boolean)) {
    const parts = raw.includes('<') || !raw.includes(',') ? [raw] : raw.split(',').map((x) => x.trim()).filter(Boolean);
    for (const part of parts) {
      const a = normalizeMail(part);
      if (a) {
        if (!valid.includes(a)) valid.push(a);
      } else invalid.push(part);
    }
  }
  return { valid, invalid };
}

export const joinList = (list: string[] | null | undefined) => (list ?? []).join('; ');

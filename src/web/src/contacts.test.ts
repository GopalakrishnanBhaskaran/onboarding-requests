import { describe, expect, it } from 'vitest';
import { normalizeMail, parseList } from './lib/contacts';

// Same cases as the server's MailAddresses tests: the page warns with the rule the API enforces.
describe('contact inventory lists', () => {
  it('normalises an address or refuses it', () => {
    expect(normalizeMail('Jane.Doe@Contoso.com')).toBe('jane.doe@contoso.com');
    expect(normalizeMail('  Doe, Jane <jane.doe@contoso.com> ')).toBe('jane.doe@contoso.com');
    expect(normalizeMail('mailto:sec@contoso.com')).toBe('sec@contoso.com');
    expect(normalizeMail('owner_gmail.com#EXT#@tenant.onmicrosoft.com')).toBe('owner@gmail.com');
    expect(normalizeMail('live.com#owner@outlook.com')).toBe('owner@outlook.com');
    for (const bad of ['sample-user-alice', 'two@@contoso.com', 'nodot@contoso', 'a b@contoso.com', '']) expect(normalizeMail(bad)).toBeNull();
  });

  it('splits on semicolons and keeps an Outlook display name whole', () => {
    const { valid, invalid } = parseList('Doe, Jane <jane@contoso.com>; sec@contoso.com;; SEC@contoso.com\nops@contoso.com, ops2@contoso.com; not-an-address');
    expect(valid).toEqual(['jane@contoso.com', 'sec@contoso.com', 'ops@contoso.com', 'ops2@contoso.com']);
    expect(invalid).toEqual(['not-an-address']);
  });
});

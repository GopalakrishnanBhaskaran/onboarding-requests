import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';

// Runs against tools/run_local.sh: in-memory SAMPLE data, no Entra (checks show "not checked (local development)").
const BANNER = 'The AI agent reviews, routes, verifies and reports. It never approves, creates or assigns';
const DECISION = /approve|reject/i;

/** SWA CLI emulator reads the mock client principal from this cookie (same shape SWA injects in Azure). */
async function signIn(context: BrowserContext, userId: string) {
  const principal = { identityProvider: 'aad', userId, userDetails: `${userId}@sample.invalid`, userRoles: ['anonymous', 'authenticated'] };
  await context.addCookies([{
    name: 'StaticWebAppsAuthCookie',
    value: Buffer.from(JSON.stringify(principal)).toString('base64'),
    domain: 'localhost',
    path: '/',
  }]);
}

async function as(browser: Browser, userId: string): Promise<{ ctx: BrowserContext; page: Page }> {
  const ctx = await browser.newContext();
  await signIn(ctx, userId);
  return { ctx, page: await ctx.newPage() };
}

/** Unique per run: the in-memory store lives as long as the local API, and the review flags duplicate requests. */
const enterpriseId = (tag: string) => `sample.e2e.${tag}.${Date.now().toString(36)}@company.example`;

async function submit(page: Page, fields: {
  enterpriseId: string; resource: string; accessLevel: string; owner: string; justification: string; environment?: string; tenant?: string;
}) {
  await page.goto('/#/requests/new');
  // The tenant is chosen on the form itself (one project per tenant).
  if (fields.tenant) await page.getByRole('combobox', { name: 'Tenant', exact: true }).selectOption(fields.tenant);
  await expect(page.getByRole('heading', { name: 'New onboarding request' })).toBeVisible();
  // The flow and who does what are stated upfront.
  await expect(page.getByText('3. Owner approval')).toBeVisible();
  await expect(page.getByText('4. Security approval')).toBeVisible();
  await expect(page.getByText(/Never approves, creates or assigns/).first()).toBeVisible();
  await page.getByLabel('Enterprise ID').fill(fields.enterpriseId);
  await page.getByLabel('First name').fill('Sample');
  await page.getByLabel('Last name').fill('Joiner');
  // Environment, resource and access level are picked from the mapping sheet of this tenant.
  if (fields.environment) await page.getByLabel('Environment').selectOption(fields.environment);
  await page.getByLabel('Resource').selectOption(fields.resource);
  await page.getByLabel('Access level').selectOption(fields.accessLevel);
  // A ready template, prefilled with the choices; submitting it unfilled is refused.
  await page.getByRole('button', { name: 'Insert into justification' }).click();
  await expect(page.getByRole('textbox', { name: 'Justification' })).toHaveValue(new RegExp(`Access needed: ${fields.accessLevel} on `));
  await expect(page.getByText('Replace every [ … ] of the template with your own details.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Submit request' })).toBeDisabled();
  await page.getByRole('textbox', { name: 'Justification' }).fill(fields.justification);
  await page.getByLabel('Owner (sign-in name)').fill(fields.owner);
  await page.getByRole('button', { name: 'Submit request' }).click();
  await expect(page.getByRole('heading', { level: 1, name: /^Request [0-9a-f]{8}/ })).toBeVisible();
  return page.url().split('/requests/')[1];
}

/** Every cell of the table keeps its text inside its own column: long sign-in names wrap instead of running into the next cell. */
async function expectNoOverflow(page: Page, table: string) {
  const t = page.getByRole('table', { name: table });
  await expect(t).toBeVisible();
  const spilled = await t.locator('td, th').evaluateAll((cells) => cells
    .filter((c) => c.scrollWidth > c.clientWidth + 1)
    .map((c) => `${c.textContent?.trim().slice(0, 60)} (${c.scrollWidth}px in ${c.clientWidth}px)`));
  expect(spilled).toEqual([]);
}

async function expectStage(page: Page, stage: string) {
  await expect(page.locator('section[aria-labelledby="status-h"]').getByText(stage, { exact: true })).toBeVisible({ timeout: 20_000 });
}

async function open(page: Page, requestId: string) {
  await page.goto(`/#/requests/${requestId}`);
  await expect(page.getByRole('heading', { level: 1, name: /^Request [0-9a-f]{8}/ })).toBeVisible();
}

/** The decision needs the approver to confirm they read what is being decided. */
async function decide(page: Page, button: string) {
  await page.getByLabel(/I have read the request/).check();
  await page.getByRole('button', { name: button }).click();
}

test('unauthenticated visitor is sent to Entra sign-in; the signed-out page is public', async ({ page }) => {
  const res = await page.request.get('/api/me', { maxRedirects: 0 });
  expect(res.status()).toBe(302);
  expect(res.headers()['location']).toContain('/.auth/login/aad');
  const out = await page.request.get('/signed-out.html', { maxRedirects: 0 });
  expect(out.status()).toBe(200);
});

test('full flow: requester → AI review → named Owner → Security → ID team → role team → completed', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  const id = await submit(alice.page, {
    enterpriseId: enterpriseId('ok'),
    resource: 'SAMPLE Databricks Engineering',
    accessLevel: 'User',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: joins the data engineering team to build and run ingestion pipelines in the workspace.',
  });
  await expect(alice.page.getByText(BANNER)).toBeVisible();
  await expectStage(alice.page, 'Awaiting Owner');
  await expect(alice.page.getByText('AI summary for the approvers')).toBeVisible();
  await expect(alice.page.getByRole('button', { name: DECISION })).toHaveCount(0);

  // Another Owner in the same project is not the named Owner: no decision controls, and the API refuses.
  const otto = await as(browser, 'sample-user-otto');
  await open(otto.page, id);
  await expect(otto.page.getByRole('button', { name: DECISION })).toHaveCount(0);
  // … and is told why, instead of a blank page.
  await expect(otto.page.getByText(/Waiting for the Owner named on the request: sample-user-olivia@sample.invalid. You are signed in as sample-user-otto/)).toBeVisible();

  // A user of another project cannot see the request at all.
  const bob = await as(browser, 'sample-user-bob');
  expect((await bob.page.request.get(`/api/projects/SAMPLE-ALPHA/requests/${id}`)).status()).toBe(404);

  const olivia = await as(browser, 'sample-user-olivia');
  await open(olivia.page, id);
  await expect(olivia.page.getByRole('button', { name: 'Approve business need' })).toBeDisabled();
  await decide(olivia.page, 'Approve business need');
  await expectStage(olivia.page, 'Awaiting Security');

  const sam = await as(browser, 'sample-user-sam');
  await open(sam.page, id);
  await expect(sam.page.getByText(/Approved by sample-user-olivia@sample.invalid/).first()).toBeVisible();
  await decide(sam.page, 'Approve risk and access level');
  await expectStage(sam.page, 'With ID Team');

  const ivan = await as(browser, 'sample-user-ivan');
  await open(ivan.page, id);
  await expect(ivan.page.locator('input[type="password"]')).toHaveCount(0);
  await ivan.page.getByRole('button', { name: 'Pick up task' }).click();
  await ivan.page.getByLabel('The user is a member of the group').check();
  await ivan.page.getByRole('button', { name: 'Record completion' }).click();
  await expectStage(ivan.page, 'With Role Team');

  const rose = await as(browser, 'sample-user-rose');
  await open(rose.page, id);
  await rose.page.getByRole('button', { name: 'Pick up task' }).click();
  await rose.page.getByRole('button', { name: 'Record completion' }).click();
  await expectStage(rose.page, 'Completed');

  await rose.page.getByRole('tab', { name: /Audit trail/ }).click();
  await rose.page.getByRole('button', { name: 'Show full trail' }).click();
  await expect(rose.page.getByText(/OwnerApproved|Owner approved/i).first()).toBeVisible();

  // The request list: the requester's sign-in name and the stage badge each stay in their own column, on a laptop and a narrow window.
  for (const width of [1280, 900]) {
    await alice.page.setViewportSize({ width, height: 800 });
    await alice.page.goto('/#/requests');
    await expect(alice.page.getByRole('link', { name: id.slice(0, 8) })).toBeVisible();
    await expectNoOverflow(alice.page, 'Request status');
  }

  for (const u of [alice, otto, bob, olivia, sam, ivan, rose]) await u.ctx.close();
});

test('a request the AI review finds gaps in is returned with the reason and can be corrected', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  await submit(alice.page, {
    enterpriseId: enterpriseId('ret'),
    resource: 'SAMPLE Databricks Engineering',
    accessLevel: 'User',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: views dashboards.',
  });
  await expectStage(alice.page, 'Returned');
  await expect(alice.page.getByText('Why it was returned')).toBeVisible();
  await expect(alice.page.getByRole('link', { name: 'Correct and resubmit' })).toBeVisible();
  await alice.ctx.close();
});

test('the named Owner rejects with remarks and the request stops', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  const id = await submit(alice.page, {
    enterpriseId: enterpriseId('rej'),
    resource: 'SAMPLE Databricks Engineering',
    accessLevel: 'User',
    owner: 'sample-user-otto@sample.invalid',
    justification: 'SAMPLE: joins the data engineering team to build and run ingestion pipelines in the workspace.',
  });
  await expectStage(alice.page, 'Awaiting Owner');

  const otto = await as(browser, 'sample-user-otto');
  await open(otto.page, id);
  await otto.page.getByLabel('Remarks').fill('SAMPLE: no business need this quarter.');
  await otto.page.getByRole('button', { name: 'Reject' }).click();
  await expectStage(otto.page, 'Rejected');
  await expect(otto.page.getByText(/no business need this quarter/).first()).toBeVisible();

  await alice.ctx.close();
  await otto.ctx.close();
});

test('tenant project: environment, the Azure roles and risk flags are on the decision page; Prod is returned', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  await alice.page.goto('/');
  await expect(alice.page.getByText('Tenant:').first()).toBeVisible();
  const id = await submit(alice.page, {
    tenant: 'SAMPLE-PEST',
    enterpriseId: enterpriseId('pest'),
    environment: 'Dev',
    resource: 'SAMPLE PEST Platform (ph)',
    accessLevel: 'Support Reader',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: provides application support for the PEST platform and reads pipeline data for support triage of failed loads.',
  });
  await expectStage(alice.page, 'Awaiting Owner');

  const olivia = await as(browser, 'sample-user-olivia');
  await olivia.page.goto('/');
  await olivia.page.getByRole('combobox', { name: 'Switch tenant' }).click();
  await olivia.page.getByRole('option', { name: /SAMPLE PEST data platform/ }).click();
  await open(olivia.page, id);
  const panel = olivia.page.locator('section[aria-labelledby="decision-h"]');
  await expect(panel.getByText('SAMPLE PEST', { exact: true })).toBeVisible();
  await expect(panel.getByText('Dev', { exact: true })).toBeVisible();
  await expect(panel.getByText(/sample-azr-pes-rg-data-dev-ph-weu-support-reader \(to be created\)/)).toBeVisible();
  await expect(panel.getByRole('table', { name: 'Azure roles in this decision' }).getByText('Reader', { exact: true })).toBeVisible();
  await decide(olivia.page, 'Approve business need');
  await expectStage(olivia.page, 'Awaiting Security');

  const prod = await submit(alice.page, {
    enterpriseId: enterpriseId('prd'),
    environment: 'Prod',
    resource: 'SAMPLE PEST Platform (ph)',
    accessLevel: 'Support Reader',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: provides application support for the PEST platform and reads pipeline data for support triage of failed loads.',
  });
  expect(prod).toBeTruthy();
  await expectStage(alice.page, 'Returned');
  await expect(alice.page.getByText(/flagged red/).first()).toBeVisible();
  for (const u of [alice, olivia]) await u.ctx.close();
});

test('PMO sees the mapping sheet and the response document', async ({ browser }) => {
  const paul = await as(browser, 'sample-user-paul');
  await paul.page.goto('/#/sheet');
  await expect(paul.page.getByRole('heading', { name: 'PMO-to-Entra group mapping sheet' })).toBeVisible();
  await expect(paul.page.getByText('A-DBW-ENG-USER v1')).toBeVisible();
  await expect(paul.page.getByRole('heading', { name: 'Response document', exact: true })).toBeVisible();
  // Long Azure scopes and user ids wrap inside their columns.
  await expectNoOverflow(paul.page, 'Mapping sheet');
  await expectNoOverflow(paul.page, 'Response document');

  // PMO corrects an existing row in place (e.g. the group's object id in this tenant): the version goes up.
  const row = paul.page.getByRole('row').filter({ hasText: 'A-SALES-READ v' });
  await row.getByRole('button', { name: 'Edit' }).click();
  await expect(paul.page.getByRole('heading', { name: /Edit row A-SALES-READ v\d+ \(PMO\)/ })).toBeVisible();
  await paul.page.getByLabel('Group object id').fill('not-a-guid');
  await expect(paul.page.getByRole('button', { name: 'Save row' })).toBeDisabled();
  await paul.page.getByLabel('Group object id').fill('11111111-2222-4333-8444-555555555555');
  await paul.page.getByRole('button', { name: 'Save row' }).click();
  await expect(paul.page.getByRole('row').filter({ hasText: 'A-SALES-READ v' }).getByText('11111111-2222-4333-8444-555555555555')).toBeVisible();
  await expect(paul.page.getByRole('heading', { name: /Edit row/ })).toHaveCount(0);
  await paul.ctx.close();
});

test('contact inventory: PMO edits the team mail lists (semicolon-separated); others read them', async ({ browser }) => {
  const paul = await as(browser, 'sample-user-paul');
  await paul.page.goto('/#/contacts');
  await expect(paul.page.getByRole('heading', { name: 'Contact inventory' })).toBeVisible();
  const security = paul.page.getByRole('textbox', { name: 'Security', exact: true });
  await expect(security).toHaveValue(/@/);
  const team = `sec.${Date.now().toString(36)}@company.example`; // unique per run: the local store outlives a run
  // A wrong entry is named and nothing can be saved.
  await security.fill(`${team}; not-an-address`);
  await expect(paul.page.getByText('Not a mail address: not-an-address')).toBeVisible();
  await expect(paul.page.getByRole('button', { name: 'Save contacts' })).toBeDisabled();
  // An Outlook paste works: "Name <address>; …".
  await security.fill(`Security Team <${team}>; sec2@company.example`);
  await paul.page.getByRole('button', { name: 'Save contacts' }).click();
  await expect(paul.page.getByText(/Saved as version \d+\. Security and PMO were mailed what changed/)).toBeVisible();
  await expect(security).toHaveValue(`${team}; sec2@company.example`);
  await paul.ctx.close();

  const alice = await as(browser, 'sample-user-alice');
  await alice.page.goto('/#/contacts');
  await expect(alice.page.getByRole('textbox', { name: /^Security \(\d+\)$/ })).toHaveValue(`${team}; sec2@company.example`);
  await expect(alice.page.getByRole('button', { name: 'Save contacts' })).toHaveCount(0);
  await alice.ctx.close();
});

test('unregistered user sees their id and no data', async ({ browser }) => {
  const { ctx, page } = await as(browser, 'sample-user-nobody');
  await page.goto('/');
  await expect(page.getByText('sample-user-nobody').first()).toBeVisible();
  await expect(page.getByText(/not registered for any project/)).toBeVisible();
  // The sign-in name is enough to register someone (or the user id).
  await expect(page.locator('code', { hasText: 'sample-user-nobody@sample.invalid' })).toBeVisible();
  await ctx.close();
});

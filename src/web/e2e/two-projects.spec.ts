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
  enterpriseId: string; resource: string; group: string; accessLevel: string; owner: string; justification: string; environment?: string; tenant?: string;
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
  // v1.1 F1: environment and Resource* come from the mapping sheet of this tenant; the resolved groups are ticked.
  if (fields.environment) await page.getByLabel('Environment').selectOption(fields.environment);
  await expect(page.getByRole('button', { name: 'Submit request' })).toBeDisabled(); // no resource chosen yet
  await page.getByRole('combobox', { name: /^Resource/ }).selectOption(fields.resource);
  const table = page.getByRole('table', { name: 'Resolved access' });
  await expect(table).toBeVisible();
  await table.getByRole('checkbox', { name: fields.group, exact: true }).check();
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
    resource: 'Engineering',
    group: 'SAMPLE-GRP-ALPHA-DBW-ENG-USER',
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
    resource: 'Engineering',
    group: 'SAMPLE-GRP-ALPHA-DBW-ENG-USER',
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
    resource: 'Engineering',
    group: 'SAMPLE-GRP-ALPHA-DBW-ENG-USER',
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

test('tenant project: environment, the Azure roles and risk flags are on the decision page; red Prod rows are not offered', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  await alice.page.goto('/');
  await expect(alice.page.getByText('Tenant:').first()).toBeVisible();
  const id = await submit(alice.page, {
    tenant: 'SAMPLE-PEST',
    enterpriseId: enterpriseId('pest'),
    environment: 'Dev',
    resource: 'PH',
    group: 'sample-azr-pes-rg-data-dev-ph-weu-support-reader',
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

  // v1.1 F1: the only Prod row is flagged red (never acted on), so Prod offers nothing to request.
  await alice.page.goto('/#/requests/new');
  await alice.page.getByLabel('Environment').selectOption('Prod');
  await expect(alice.page.getByText(/has no resources for Prod that can be requested/)).toBeVisible();
  await expect(alice.page.getByRole('button', { name: 'Submit request' })).toBeDisabled();
  for (const u of [alice, olivia]) await u.ctx.close();
});

test('Resource* checkboxes: All ticks every resource, unticking one clears All, two groups become two requests', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  const page = alice.page;
  await page.goto('/#/requests/new');
  await page.getByRole('switch', { name: 'Choose several resources' }).check();
  const group = page.getByRole('group', { name: 'Resources' });
  await group.getByRole('checkbox', { name: 'All Domains + PH + ACQ' }).check();
  for (const name of ['Engineering', 'Finance', 'Sales']) await expect(group.getByRole('checkbox', { name, exact: true })).toBeChecked();
  await group.getByRole('checkbox', { name: 'Sales', exact: true }).uncheck();
  await expect(group.getByRole('checkbox', { name: 'All Domains + PH + ACQ' })).not.toBeChecked();
  const table = page.getByRole('table', { name: 'Resolved access' });
  await expect(table.getByText('SAMPLE-GRP-ALPHA-SALES-READ')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Submit request' })).toBeDisabled(); // no group ticked yet
  await table.getByRole('checkbox', { name: 'SAMPLE-GRP-ALPHA-DBW-ENG-USER', exact: true }).check();
  await table.getByRole('checkbox', { name: 'SAMPLE-GRP-ALPHA-FIN-VIEW', exact: true }).check();
  await page.getByLabel('Enterprise ID').fill(enterpriseId('bundle'));
  await page.getByLabel('First name').fill('Sample');
  await page.getByLabel('Last name').fill('Joiner');
  await page.getByRole('textbox', { name: 'Justification' }).fill('SAMPLE: joins the data engineering team and builds the finance reports from the curated data.');
  await page.getByLabel('Owner (sign-in name)').fill('sample-user-olivia@sample.invalid');
  await page.getByRole('button', { name: 'Submit request' }).click();
  await expect(page).toHaveURL(/#\/requests$/);
  await expect(page.getByText('SAMPLE Finance Reports').first()).toBeVisible();
  await expect(page.getByText('SAMPLE Databricks Engineering').first()).toBeVisible();
  await alice.ctx.close();
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
  await expect(paul.page.getByRole('heading', { name: 'Team contacts' })).toBeVisible();
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

test('Data uploads: the DataMaintainer checks a mapping file, sees the diff, applies it and rolls it back', async ({ browser }) => {
  const tenant = '00000000-0000-4000-8000-0000000000a1'; // tools/run_local.sh AGENT_TENANT_ID
  const group = `SAMPLE-E2E-UPLOAD-${Date.now().toString(36).toUpperCase()}`; // the local store outlives a run
  const header = 'TenantId,Domain,ResourceGroupType,Environment,AccessLevel,EntraGroupName,EntraGroupObjectId,Owner,EffectiveFrom,EffectiveTo';
  const csv = `${header}\r\n${tenant},Finance,Domain,Dev,Read,${group},,sample-user-olivia@sample.invalid,2026-01-01,\r\n`;
  const dana = await as(browser, 'sample-user-dana');
  await dana.page.goto('/#/uploads');
  await expect(dana.page.getByRole('heading', { name: 'Data uploads' })).toBeVisible();
  const mapping = dana.page.getByRole('region', { name: 'PMO mapping sheet' });

  // A macro-enabled workbook never leaves the browser.
  await mapping.getByLabel('Upload PMO mapping sheet file').setInputFiles({ name: 'mapping.xlsm', mimeType: 'application/vnd.ms-excel.sheet.macroEnabled.12', buffer: Buffer.from('PK') });
  await expect(mapping.getByText(/macro-enabled or binary Excel files are refused/)).toBeVisible();

  // A wrong column is named and nothing can be applied.
  await mapping.getByLabel('Upload PMO mapping sheet file').setInputFiles({ name: 'mapping.csv', mimeType: 'text/csv', buffer: Buffer.from(csv.replace('EntraGroupName', 'Entra Group Name')) });
  await expect(mapping.getByText('Invalid', { exact: true })).toBeVisible();
  await expect(mapping.getByRole('table', { name: 'Validation errors' })).toContainText('Rename it to "EntraGroupName"');
  await expect(mapping.getByRole('button', { name: /^Apply as v/ })).toHaveCount(0);

  await mapping.getByLabel('Upload PMO mapping sheet file').setInputFiles({ name: 'mapping.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await expect(mapping.getByText('Valid', { exact: true })).toBeVisible();
  await expect(mapping.getByText(/1 added, 0 updated/)).toBeVisible();
  await expect(mapping.getByRole('table', { name: 'Changes against the current version' })).toContainText(group);
  await mapping.getByRole('button', { name: /^Apply as v\d+$/ }).click();
  await expect(mapping.getByText(/^Applied: v\d+\.$/)).toBeVisible();
  await expect(mapping.getByRole('table', { name: 'PMO mapping sheet versions' })).toContainText('mapping.csv');

  await mapping.getByRole('button', { name: /^Roll back v\d+…$/ }).click();
  await mapping.getByRole('button', { name: /^Roll back v\d+$/ }).click();
  await expect(mapping.getByText(/^Applied: v\d+ \(rollback of v\d+\)\.$/)).toBeVisible();
  await expect(mapping.getByRole('table', { name: 'PMO mapping sheet versions' })).toContainText('rolled back');
  await dana.ctx.close();

  // PMO reads the page; only the DataMaintainer uploads.
  const paul = await as(browser, 'sample-user-paul');
  await paul.page.goto('/#/uploads');
  await expect(paul.page.getByText('Read only: uploads are made by the DataMaintainer.')).toBeVisible();
  await expect(paul.page.getByRole('button', { name: 'Upload PMO mapping sheet' })).toHaveCount(0);
  await paul.ctx.close();
});

test('Offboard user: PMO asks for a plan on the request page; the requester does not see the button', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  const tag = enterpriseId('off');
  const requestId = await submit(alice.page, {
    enterpriseId: tag,
    resource: 'Engineering',
    group: 'SAMPLE-GRP-ALPHA-DBW-ENG-USER',
    accessLevel: 'User',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: views dashboards.',
  });
  await expect(alice.page.getByRole('button', { name: 'Offboard user' })).toHaveCount(0);
  await alice.ctx.close();

  const upn = `${tag.split('@')[0]}@sample.invalid`; // the account in the tenant (domain-specific ID)
  const paul = await as(browser, 'sample-user-paul');
  await open(paul.page, requestId);
  const panel = paul.page.getByRole('region', { name: 'Offboarding' });
  await expect(panel.getByRole('heading', { name: `Offboarding — ${upn}` })).toBeVisible();
  await panel.getByRole('button', { name: 'Offboard user' }).click();
  // No worker runs locally: the plan stays with the AI agent, and nothing can be confirmed before it is ready.
  await expect(panel.getByText('Planning (the AI agent reads Entra and Azure)')).toBeVisible();
  await expect(panel.getByText(/works on it within about 5 minutes/)).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Confirm offboarding' })).toHaveCount(0);
  await expect(panel.getByRole('button', { name: 'Offboard user' })).toHaveCount(0); // a second click cannot start another run
  await paul.ctx.close();
});

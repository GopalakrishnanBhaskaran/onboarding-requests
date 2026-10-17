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
const enterpriseId = (tag: string) => `sample.e2e.${tag}.${Date.now().toString(36)}`;

async function submit(page: Page, fields: { enterpriseId: string; resource: string; accessLevel: string; owner: string; justification: string }) {
  await page.goto('/#/requests/new');
  await expect(page.getByRole('heading', { name: 'New onboarding request' })).toBeVisible();
  // The flow and who does what are stated upfront.
  await expect(page.getByText('3. Owner approval')).toBeVisible();
  await expect(page.getByText('4. Security approval')).toBeVisible();
  await expect(page.getByText(/Never approves, creates or assigns/).first()).toBeVisible();
  await page.getByLabel('Enterprise ID').fill(fields.enterpriseId);
  await page.getByLabel('First name').fill('Sample');
  await page.getByLabel('Last name').fill('Joiner');
  await page.getByLabel('Resource').fill(fields.resource);
  await page.getByLabel('Access level').fill(fields.accessLevel);
  await page.getByLabel('Justification').fill(fields.justification);
  await page.getByLabel('Owner (sign-in name)').fill(fields.owner);
  await page.getByRole('button', { name: 'Submit request' }).click();
  await expect(page.getByRole('heading', { level: 1, name: /^Request [0-9a-f]{8}/ })).toBeVisible();
  return page.url().split('/requests/')[1];
}

async function expectStage(page: Page, stage: string) {
  await expect(page.locator('section[aria-labelledby="status-h"]').getByText(stage, { exact: true })).toBeVisible({ timeout: 20_000 });
}

async function open(page: Page, requestId: string) {
  await page.goto(`/#/requests/${requestId}`);
  await expect(page.getByRole('heading', { level: 1, name: /^Request [0-9a-f]{8}/ })).toBeVisible();
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

  // A user of another project cannot see the request at all.
  const bob = await as(browser, 'sample-user-bob');
  expect((await bob.page.request.get(`/api/projects/SAMPLE-ALPHA/requests/${id}`)).status()).toBe(404);

  const olivia = await as(browser, 'sample-user-olivia');
  await open(olivia.page, id);
  await olivia.page.getByRole('button', { name: 'Approve business need' }).click();
  await expectStage(olivia.page, 'Awaiting Security');

  const sam = await as(browser, 'sample-user-sam');
  await open(sam.page, id);
  await sam.page.getByRole('button', { name: 'Approve risk and access level' }).click();
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

  for (const u of [alice, otto, bob, olivia, sam, ivan, rose]) await u.ctx.close();
});

test('request that does not match the mapping sheet is returned with the reason and can be corrected', async ({ browser }) => {
  const alice = await as(browser, 'sample-user-alice');
  await submit(alice.page, {
    enterpriseId: enterpriseId('ret'),
    resource: 'SAMPLE Unknown Resource',
    accessLevel: 'User',
    owner: 'sample-user-olivia@sample.invalid',
    justification: 'SAMPLE: needs access to a resource that is not in the mapping sheet.',
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

test('PMO sees the mapping sheet and the response document', async ({ browser }) => {
  const paul = await as(browser, 'sample-user-paul');
  await paul.page.goto('/#/sheet');
  await expect(paul.page.getByRole('heading', { name: 'PMO-to-Entra group mapping sheet' })).toBeVisible();
  await expect(paul.page.getByText('A-DBW-ENG-USER v1')).toBeVisible();
  await expect(paul.page.getByRole('heading', { name: 'Response document', exact: true })).toBeVisible();
  await paul.ctx.close();
});

test('unregistered user sees their id and no data', async ({ browser }) => {
  const { ctx, page } = await as(browser, 'sample-user-nobody');
  await page.goto('/');
  await expect(page.getByText('sample-user-nobody').first()).toBeVisible();
  await expect(page.getByText(/not registered for any pilot project/)).toBeVisible();
  await ctx.close();
});

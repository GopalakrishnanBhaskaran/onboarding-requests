// Playwright evidence capture (headed). One persistent browser profile per sub-agent.
// The OWNER signs in in the opened window; this script never types credentials and waits at any sign-in/MFA page.
// Before each screenshot, GUIDs, public IPs and e-mail addresses on the page are masked in the DOM.
// Usage: node pw_capture.mjs <agent A-E> <name> <url> [<name> <url> ...]
//   e.g. node pw_capture.mjs A storage-networking "https://portal.azure.com/#@/resource/<id>/networking"
import { chromium } from 'playwright';
import path from 'node:path';
import fs from 'node:fs';

const [agent, ...pairs] = process.argv.slice(2);
if (!agent || pairs.length < 2 || pairs.length % 2) {
  console.error('usage: node pw_capture.mjs <agent> <name> <url> [<name> <url> ...]');
  process.exit(2);
}
const here = path.dirname(new URL(import.meta.url).pathname);
const repo = path.resolve(here, '..', '..');
const profile = path.join(repo, '.local', 'fabric-poc', `pw-${agent}`);
const outDir = path.join(repo, 'fabric-adls-poc', 'evidence', 'screenshots');
fs.mkdirSync(profile, { recursive: true });
fs.mkdirSync(outDir, { recursive: true });

const isSignIn = (u) => /login\.microsoftonline\.com|login\.live\.com|login\.microsoft\.com|aadcdn|mysignins/.test(u);

const ctx = await chromium.launchPersistentContext(profile, {
  headless: false, channel: process.env.PW_CHANNEL || undefined, viewport: { width: 1440, height: 900 },
});
const page = ctx.pages()[0] || (await ctx.newPage());
for (let i = 0; i < pairs.length; i += 2) {
  const [name, url] = [pairs[i], pairs[i + 1]];
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
  if (isSignIn(page.url())) {
    console.log(`SIGN-IN NEEDED: please sign in yourself in the open browser window (agent ${agent}). Waiting up to 10 minutes...`);
    const t0 = Date.now();
    while (isSignIn(page.url()) && Date.now() - t0 < 600000) await page.waitForTimeout(3000);
    if (isSignIn(page.url())) { console.log('still at sign-in: stopping, nothing captured'); await ctx.close(); process.exit(3); }
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
  }
  await page.waitForTimeout(Number(process.env.PW_SETTLE_MS || 12000)); // portal blades render late
  await page.evaluate(() => {
    const guid = /\b([0-9a-f]{4})[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
    const mail = /[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})/g;
    const ip = /\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b/g;
    const priv = (a, b) => a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || a === 127;
    const fix = (s) => s.replace(guid, '$1****-****').replace(mail, '***@$1')
      .replace(ip, (m, a, b) => (priv(+a, +b) ? m : `${a}.${b}.x.x`));
    const walk = (root) => {
      const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let n = w.nextNode(); n; n = w.nextNode()) n.nodeValue = fix(n.nodeValue);
      root.querySelectorAll('input,textarea').forEach((el) => { if (el.value) el.value = fix(el.value); });
      root.querySelectorAll('iframe').forEach((f) => { try { walk(f.contentDocument.body); } catch { /* cross-origin */ } });
    };
    walk(document.body);
  });
  const file = path.join(outDir, `${agent}-${name}.png`);
  await page.screenshot({ path: file, fullPage: false });
  console.log(`saved ${path.relative(repo, file)}`);
}
await ctx.close();

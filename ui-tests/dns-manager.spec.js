const { test, expect } = require('@playwright/test');

const zoneName = process.env.DNSGOBETWEEN_TEST_ZONE || 'ashurtech.net';
const basicUser = process.env.DNSGOBETWEEN_BASIC_USER || '';
const basicPassword = process.env.DNSGOBETWEEN_BASIC_PASSWORD || '';
const runWriteTests = /^(1|true|yes)$/i.test(process.env.DNSGOBETWEEN_RUN_WRITE_TESTS || '');
const testHost = process.env.DNSGOBETWEEN_TEST_HOST || `smoketest-${Date.now().toString().slice(-6)}`;
const testIp = process.env.DNSGOBETWEEN_TEST_IP || '192.168.1.250';

const authHeader = basicUser && basicPassword
  ? {
      Authorization: `Basic ${Buffer.from(`${basicUser}:${basicPassword}`, 'utf8').toString('base64')}`,
    }
  : {};

test.use({
  extraHTTPHeaders: authHeader,
});

async function gotoManager(page) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: /DNS Record Manager/i })).toBeVisible();
}

async function getZoneNode(page, zone) {
  const zoneNode = page.locator('.zone-node').filter({ hasText: zone }).first();
  await expect(zoneNode).toBeVisible();
  return zoneNode;
}

async function expandZone(page, zone) {
  const zoneNode = await getZoneNode(page, zone);
  const hostNodes = zoneNode.locator('.host-node');
  if (await hostNodes.count() > 0) {
    return zoneNode;
  }

  await zoneNode.locator('.zone-toggle').click();
  await expect(page.locator('.alert-danger')).toHaveCount(0);
  await expect(zoneNode.locator('.host-node').first()).toBeVisible();
  return zoneNode;
}

async function expandHost(zoneNode, hostName) {
  const hostNode = zoneNode.locator('.host-node').filter({ hasText: hostName }).first();
  await expect(hostNode).toBeVisible();
  const recordNodes = hostNode.locator('.record-node');
  if (await recordNodes.count() === 0) {
    await hostNode.locator('.host-toggle').click();
  }
  await expect(hostNode.locator('.record-node').first()).toBeVisible();
  return hostNode;
}

test.describe('DNS Manager browser smoke tests', () => {
  test('loads the page and expands a zone with records', async ({ page }) => {
    await gotoManager(page);

    const zoneNode = await expandZone(page, zoneName);
    const firstHost = zoneNode.locator('.host-node').first();
    await expect(firstHost).toBeVisible();

    await firstHost.locator('.host-toggle').click();
    await expect(firstHost.locator('.record-node').first()).toBeVisible();
  });

  test('shows the expected delete confirmation content for a record', async ({ page }) => {
    await gotoManager(page);

    const zoneNode = await expandZone(page, zoneName);
    const firstHost = zoneNode.locator('.host-node').first();
    await firstHost.locator('.host-toggle').click();

    const firstRecord = firstHost.locator('.record-node').first();
    await expect(firstRecord).toBeVisible();
    await firstRecord.locator('.btn-delete').click();

    await expect(page.getByRole('heading', { name: /Confirm Delete/i })).toBeVisible();
    await expect(page.getByText('Are you sure you want to delete this record?')).toBeVisible();
    await expect(page.locator('.confirm-table')).toContainText(zoneName);

    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('heading', { name: /Confirm Delete/i })).toHaveCount(0);
  });

  test('can add and delete a temporary A record through the browser', async ({ page }) => {
    test.skip(!runWriteTests, 'Set DNSGOBETWEEN_RUN_WRITE_TESTS=true to enable destructive browser tests.');
    test.skip(!(basicUser && basicPassword), 'Set DNSGOBETWEEN_BASIC_USER and DNSGOBETWEEN_BASIC_PASSWORD for authenticated browser write tests.');

    await gotoManager(page);

    const zoneNode = await expandZone(page, zoneName);
    await page.getByRole('button', { name: new RegExp(`\\+ Add new record to ${zoneName.replace(/[.*+?^${}()|[\\]\\]/g, '\\$&')}`) }).click();

    await expect(page.getByRole('heading', { name: /Add DNS Record/i })).toBeVisible();
    await page.getByLabel('Host Name').fill(testHost);
    await page.getByLabel('Type').selectOption('A');
    await page.getByLabel('Data').fill(testIp);
    await page.getByLabel('TTL (seconds)').fill('300');
    await page.getByRole('button', { name: 'Add Record' }).click();

    await expect(page.getByRole('heading', { name: /Add DNS Record/i })).toHaveCount(0);
    await expect(page.locator('.alert-danger')).toHaveCount(0);

    const createdHostNode = await expandHost(zoneNode, testHost);
    await expect(createdHostNode.locator('.record-node').filter({ hasText: testIp }).first()).toBeVisible();

    await createdHostNode.locator('.record-node').filter({ hasText: testIp }).first().locator('.btn-delete').click();
    await expect(page.getByRole('heading', { name: /Confirm Delete/i })).toBeVisible();
    await page.getByRole('button', { name: 'Delete' }).click();

    await expect(page.getByRole('heading', { name: /Confirm Delete/i })).toHaveCount(0);
    await expect(page.locator('.alert-danger')).toHaveCount(0);
    await expect(zoneNode.locator('.host-node').filter({ hasText: testHost })).toHaveCount(0);
  });
});

import { test, expect } from '@playwright/test';

// Configure Basic Auth credentials for the test context.
// These should match an account in the DnsAdmins or Domain Admins group.
test.use({
  httpCredentials: {
    username: process.env.TEST_USER || 'admin',
    password: process.env.TEST_PASSWORD || 'password'
  }
});

test('Health status page renders provider cards successfully', async ({ page }) => {
  // Navigate to the health page
  await page.goto('/health-status');

  // Verify the page loaded correctly
  await expect(page).toHaveTitle(/System Health/);
  await expect(page.locator('h2')).toContainText('System Health');

  // Ensure the loading state resolves
  await expect(page.getByText('Loading health status...')).toBeHidden();

  // Verify that at least one health card is rendered
  const cards = page.locator('.card');
  await expect(cards.first()).toBeVisible();

  // Verify the Windows DNS card is present with expected fields
  const windowsDnsCard = page.locator('.card', { hasText: 'Windows DNS Server (PowerShell)' });
  await expect(windowsDnsCard.locator('dt', { hasText: 'Status' })).toBeVisible();
  await expect(windowsDnsCard.locator('dt', { hasText: 'Latency' })).toBeVisible();
});
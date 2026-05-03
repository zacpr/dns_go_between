const { defineConfig } = require('@playwright/test');

const headlessSetting = (process.env.DNSGOBETWEEN_HEADLESS || 'true').toLowerCase();
const headless = !['0', 'false', 'no'].includes(headlessSetting);

module.exports = defineConfig({
  testDir: './ui-tests',
  timeout: 60000,
  expect: {
    timeout: 15000,
  },
  reporter: [['list']],
  use: {
    baseURL: process.env.DNSGOBETWEEN_BASE_URL || 'http://localhost:6790',
    headless,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
});

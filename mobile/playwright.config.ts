import {defineConfig,devices} from '@playwright/test';
import {fileURLToPath} from 'node:url';

// Browser plugin is absent in the agent session; this is the repeatable Playwright fallback.
// qa.html and its synthetic media are dev-only and never part of the production Vite entry.
export default defineConfig({
  testDir:'./tests',testMatch:'**/*.pw.ts',fullyParallel:false,workers:1,timeout:90000,
  expect:{timeout:15000},reporter:[['list'],['json',{outputFile:fileURLToPath(new URL('../artifacts/community-mobile-qa/browser-results.json',import.meta.url))}]],
  outputDir:fileURLToPath(new URL('../artifacts/community-mobile-qa/playwright/',import.meta.url)),
  use:{baseURL:'http://127.0.0.1:5173',actionTimeout:15000,trace:'retain-on-failure',screenshot:'only-on-failure',video:'off'},
  projects:[
    {name:'android-chromium',use:{...devices['Pixel 7'],browserName:'chromium',viewport:{width:390,height:844}}},
    {name:'iphone-webkit',use:{...devices['iPhone 13'],browserName:'webkit',viewport:{width:390,height:844}}},
  ],
  webServer:{command:'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5173 --strictPort',url:'http://127.0.0.1:5173/qa.html',reuseExistingServer:!process.env.CI,timeout:30000},
});

// Screenshots every page of the MelodyBridge web UI, in both themes,
// and writes optimized WebP copies into docs/public/screens.
//
// Usage:
//   node scripts/take-screenshots.mjs [base-url] [--theme=light|dark|both] [--out=dir]
//
// Defaults: base http://127.0.0.1:3333, both themes, docs/public/screens.
// The app defaults to the dark theme; light is a stored choice
// (localStorage mb-theme=light), so each theme pass seeds that storage
// before navigating. On load the app auto-focuses the page heading,
// which leaves a focused-outline "selected text bar" in shots; every
// page is clicked at (10,10) first to move focus back to the body.
//
// Playwright comes from the npx cache, so no project dependency grows.
// Raw 2x PNGs land in .shots/ (gitignored); dark WebP overwrites the
// docs set, light WebP stays in .shots for side-by-side review.

import { createRequire } from 'node:module'
import { mkdirSync, existsSync } from 'node:fs'
import { execFileSync } from 'node:child_process'

const req = createRequire('/home/w/.npm/_npx/e41f203b7505f1fb/node_modules/playwright/package.json')
const { chromium } = req('playwright')

const args = process.argv.slice(2)
const baseUrl = args.find(a => !a.startsWith('--')) || 'http://127.0.0.1:3333'
const themeArg = (args.find(a => a.startsWith('--theme=')) || '--theme=both').split('=')[1]
const outArg = (args.find(a => a.startsWith('--out=')) || '--out=docs/public/screens').split('=')[1]
const themes = themeArg === 'both' ? ['light', 'dark'] : [themeArg]

// Every route worth showing. playlist-details needs a real playlist id;
// it is discovered from the rendered playlists page, never hardcoded,
// so the script works against any database.
const ROUTES = [
  ['/', 'home'],
  ['/playlists', 'playlists'],
  [null, 'playlist-details'],
  ['/library', 'library'],
  ['/plugins', 'plugins'],
  ['/sync-jobs', 'sync'],
  ['/settings', 'settings'],
  ['/logs', 'logs'],
  ['/advanced', 'advanced'],
]

const RAW_DIR = '.shots'

async function shootTheme(browser, theme) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 })
  const page = await ctx.newPage()

  // Seed the stored choice once; every later navigation keeps it.
  await page.goto(baseUrl + '/', { waitUntil: 'domcontentloaded' })
  await page.evaluate(t => {
    try { localStorage.setItem('mb-theme', t) } catch { /* private mode */ }
    document.documentElement.setAttribute('data-theme', t)
  }, theme)

  for (const [route, name] of ROUTES) {
    let path = route
    if (path === null) {
      path = await page.evaluate(() =>
        document.querySelector('a[href^="/playlists/"]')?.getAttribute('href') ?? null)
      if (!path) { console.log('SKIP', theme, name, '(no playlist on this instance)'); continue }
    }
    try {
      await page.goto(baseUrl + path, { waitUntil: 'networkidle', timeout: 30000 })
      await page.waitForTimeout(600)
      // init() re-runs after the storage seed on first paint; re-assert.
      const applied = await page.evaluate(t => {
        if (document.documentElement.getAttribute('data-theme') !== t)
          document.documentElement.setAttribute('data-theme', t)
        return document.documentElement.getAttribute('data-theme')
      }, theme)
      await page.mouse.click(10, 10) // clear the auto-focused heading
      await page.waitForTimeout(300)
      await page.evaluate(() => window.scrollTo(0, 0))
      await page.waitForTimeout(200)
      await page.screenshot({ path: `${RAW_DIR}/${theme}-${name}.png` })
      console.log('OK', theme, name)
    } catch (e) {
      console.log('FAIL', theme, name, String(e).split('\n')[0])
    }
  }
  await ctx.close()
}

console.log(`Shooting ${baseUrl} themes: ${themes.join(', ')}`)
mkdirSync(RAW_DIR, { recursive: true })
mkdirSync(outArg, { recursive: true })

const browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] })
for (const theme of themes) await shootTheme(browser, theme)
await browser.close()

// Optimize: 1600px WebP is a fraction of the raw 2x png size at the
// same clarity. Dark is the app's own default look, so it is the set
// the docs carry; light stays beside the raws for review. Every raw
// png present is converted, so an old light set survives a dark-only
// rerun and stays in review-ready webp form.
for (const theme of ['light', 'dark']) {
  for (const [, name] of ROUTES) {
    const raw = `${RAW_DIR}/${theme}-${name}.png`
    if (!existsSync(raw)) continue
    const target = theme === 'dark'
      ? `${outArg}/${name}.webp`
      : `${RAW_DIR}/${theme}-${name}.webp`
    execFileSync('magick', [raw, '-resize', '1600x', '-strip', '-quality', '88', target])
  }
}
console.log('Done. Docs copies in', outArg)

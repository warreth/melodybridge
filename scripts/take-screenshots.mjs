// Screenshots every page of the MelodyBridge web UI, in both themes,
// and writes optimized WebP copies into docs/public/screens.
//
// Usage:
//   node scripts/take-screenshots.mjs [base-url] [--theme=light|dark|both] [--out=dir] [--playlist=/playlists/<id>]
//
// Defaults: base http://127.0.0.1:3333, both themes, docs/public/screens.
// The app follows the browser's preferred scheme when nothing is stored,
// so each theme pass seeds localStorage mb-theme before navigating. On
// load the app auto-focuses the page heading, which leaves a
// focused-outline "selected text bar" in shots; every page is clicked at
// (10,10) first to move focus back to the body. --playlist picks the
// playlist for the playlist-details and playlist-tracks shots; without it
// the script reads the first playlist card on the playlists page and
// reuses that path for both shots.
//
// Playwright comes from the npx cache, so no project dependency grows.
// On a host where that cache sits elsewhere (or Playwright is installed
// in a scratch folder), point PLAYWRIGHT_MODULE at its package.json.
// CHROMIUM_PATH selects a specific Chromium build (for example a musl
// build on Alpine, where Playwright's own binary cannot run).
// Raw 2x PNGs land in .shots/ (gitignored); dark WebP overwrites the
// docs set, light WebP stays in .shots for side-by-side review.

import { createRequire } from 'node:module'
import { mkdirSync, existsSync } from 'node:fs'
import { execFileSync } from 'node:child_process'

const req = createRequire(
  process.env.PLAYWRIGHT_MODULE || '/home/w/.npm/_npx/e41f203b7505f1fb/node_modules/playwright/package.json')
const { chromium } = req('playwright')

const args = process.argv.slice(2)
const baseUrl = args.find(a => !a.startsWith('--')) || 'http://127.0.0.1:3333'
const themeArg = (args.find(a => a.startsWith('--theme=')) || '--theme=both').split('=')[1]
const outArg = (args.find(a => a.startsWith('--out=')) || '--out=docs/public/screens').split('=')[1]
const playlistPath = args.find(a => a.startsWith('--playlist='))?.split('=')[1] ?? null
const themes = themeArg === 'both' ? ['light', 'dark'] : [themeArg]

// Every route worth showing. playlist-details needs a real playlist id;
// it is discovered from the rendered playlists page (or given with
// --playlist), never hardcoded, so the script works against any
// database. playlist-tracks shows the same page scrolled to the track
// table, where the status pills and row actions live. An entry may carry
// an optional third element with a selector to scroll into view.
const ROUTES = [
  ['/', 'home'],
  ['/playlists', 'playlists'],
  [null, 'playlist-details'],
  [null, 'playlist-tracks', { scrollTo: 'table.track-table' }],
  ['/downloads', 'downloads'],
  ['/download', 'download'],
  ['/library', 'library'],
  ['/plugins', 'plugins'],
  ['/sync-jobs', 'sync'],
  ['/settings', 'settings'],
  ['/logs', 'logs'],
  ['/advanced', 'advanced'],
]

const RAW_DIR = '.shots'

// The playlist card links only exist on the playlists page (the sidebar
// links to /playlists itself, exactly), so the first null route discovers
// the path there and every later null route reuses it. --playlist skips
// the discovery entirely.
let cachedPlaylistPath = null

async function playlistPathFor(page) {
  if (playlistPath) return playlistPath
  if (cachedPlaylistPath) return cachedPlaylistPath
  await page.goto(baseUrl + '/playlists', { waitUntil: 'networkidle', timeout: 30000 })
  cachedPlaylistPath = await page.evaluate(() =>
    document.querySelector('a[href^="/playlists/"]')?.getAttribute('href') ?? null)
  return cachedPlaylistPath
}

async function shootTheme(browser, theme) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 })
  const page = await ctx.newPage()

  // Seed the stored choice once; every later navigation keeps it.
  await page.goto(baseUrl + '/', { waitUntil: 'domcontentloaded' })
  await page.evaluate(t => {
    try { localStorage.setItem('mb-theme', t) } catch { /* private mode */ }
    document.documentElement.setAttribute('data-theme', t)
  }, theme)

  for (const [route, name, opts] of ROUTES) {
    let path = route
    if (path === null) {
      path = await playlistPathFor(page)
      if (!path) { console.log('SKIP', theme, name, '(no playlist on this instance)'); continue }
    }
    try {
      await page.goto(baseUrl + path, { waitUntil: 'networkidle', timeout: 30000 })
      await page.waitForTimeout(600)
      // The Plugins page checks every provider after the first paint, so
      // its pills and the yt-dlp version land a moment later. Wait for
      // them, or the shot shows a page that is still checking.
      if (path === '/plugins') {
        await page.waitForFunction(
          () => !document.body.innerText.includes('checking…'),
          null, { timeout: 30000 }).catch(() => {})
      }
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
      if (opts?.scrollTo) {
        await page.evaluate(selector =>
          document.querySelector(selector)?.scrollIntoView({ block: 'start' }), opts.scrollTo)
        await page.waitForTimeout(250)
      }
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

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--no-sandbox', '--disable-dev-shm-usage'],
})
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
    convert(raw, target)
  }
}

// ImageMagick when present; ffmpeg otherwise (dev hosts without magick).
function convert(raw, target) {
  try {
    execFileSync('magick', [raw, '-resize', '1600x', '-strip', '-quality', '88', target])
  } catch {
    execFileSync('ffmpeg', ['-y', '-i', raw, '-vf', 'scale=1600:-1', '-quality', '88', target],
      { stdio: 'ignore' })
  }
}
console.log('Done. Docs copies in', outArg)

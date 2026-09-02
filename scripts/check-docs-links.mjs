#!/usr/bin/env node
/**
 * Checks every relative markdown and html link in docs/ and README.md
 * against the filesystem, so a moved or renamed page cannot go quietly
 * stale. External links are out of scope: VitePress already fails the
 * build on dead internal page links.
 *
 * Usage: node scripts/check-docs-links.mjs
 * Exits 1 and prints each broken link when anything fails.
 */
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { join, dirname, resolve } from 'node:path'

const roots = ['docs', 'README.md']
const broken = []

const isMarkdown = (f) => f.endsWith('.md') || f.endsWith('.html')
const stripAnchor = (l) => l.split('#')[0]

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === '.vitepress' || entry === 'dist') continue
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) walk(full, out)
    else if (isMarkdown(full) || full.endsWith('.yml')) out.push(full)
  }
  return out
}

const files = []
for (const root of roots) {
  if (statSync(root).isDirectory()) files.push(...walk(root))
  else files.push(root)
}

// [text](target) in markdown, href="target" in html
const linkPatterns = [
  /\[[^\]]*\]\(([^\s)]+)[^)]*\)/g,
  /href="([^"]+)"/g,
]

// Images under screenshots/ are paste-later placeholders in README (see the
// SCREENSHOT comments there); only check them once the directory exists.
const screenshotsExist = existsSync('screenshots')

for (const file of files) {
  const text = readFileSync(file, 'utf8')
  for (const pattern of linkPatterns) {
    for (const match of text.matchAll(pattern)) {
      const link = match[1]
      if (!link || link.startsWith('http') || link.startsWith('#') ||
          link.startsWith('mailto:') || link.startsWith('/')) continue
      if (!screenshotsExist && link.startsWith('screenshots/')) continue

      const target = stripAnchor(link)
      if (!target) continue
      const abs = resolve(dirname(file), target)
      if (!existsSync(abs)) {
        broken.push(`${file}: ${link}`)
      }
    }
  }
}

// Root-relative links inside docs point at built URLs; verify a source page exists.
// cleanUrls is on, so /quickstart maps to docs/quickstart.md.
for (const file of files.filter((f) => f.startsWith('docs'))) {
  const text = readFileSync(file, 'utf8')
  for (const match of text.matchAll(/\]\((\/[^)#\s]+)\)/g)) {
    const target = stripAnchor(match[1])
    const md = resolve('docs', '.' + target + '.md')
    const html = resolve('docs', '.' + target + '.html')
    if (!existsSync(md) && !existsSync(html)) {
      broken.push(`${file}: ${match[1]}`)
    }
  }
}

if (broken.length > 0) {
  console.error(`broken links (${broken.length}):`)
  for (const b of broken) console.error('  ' + b)
  process.exit(1)
}
console.log(`link check passed (${files.length} files scanned)`)

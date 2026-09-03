#!/usr/bin/env node
/**
 * Structure validation for the VitePress site in docs/.
 * Catches the mistakes a build would silently absorb:
 *   - a landing page that is not layout: home (or misses hero/actions/features)
 *   - ::: containers or code-groups left open or never closed
 *   - <Badge> used with the wrong props
 *   - mid-page --- rules that fragment pages into sidebar-less islands
 *
 * Usage: node scripts/check-docs-structure.mjs
 * Exits 1 and prints every violation.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'

const problems = []
const fail = (file, line, msg) => problems.push(`${file}:${line}: ${msg}`)

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === '.vitepress' || entry === 'dist' || entry === 'public') continue
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) walk(full, out)
    else if (entry.endsWith('.md')) out.push(full)
  }
  return out
}

const files = walk('docs')

// ---- 1. Landing page must be a real VitePress home layout ----
const index = files.find((f) => f === join('docs', 'index.md'))
if (!index) {
  fail('docs', 0, 'index.md is missing')
} else {
  const text = readFileSync(index, 'utf8')
  if (!/^---\r?\n[\s\S]*?^layout: home\s*$/m.test(text))
    fail(index, 1, 'frontmatter must set layout: home')
  if (!/^hero:\s*$/m.test(text))
    fail(index, 1, 'frontmatter must have a hero block')
  const heroName = text.match(/^\s+name:\s*MelodyBridge\s*$/m)
  if (!heroName) fail(index, 1, 'hero.name must be MelodyBridge')
  const actions = [...text.matchAll(/^\s+- theme: (brand|alt)\s*$/gm)]
  if (actions.length < 2)
    fail(index, 1, `hero needs at least 2 actions (brand + alt), found ${actions.length}`)
  const cards = [...text.matchAll(/^\s+- icon:/gm)]
  if (cards.length < 3)
    fail(index, 1, `features grid needs 3+ cards, found ${cards.length}`)
  const missingDetail = text.match(/^\s+- icon:[^\n]*\n(?!\s+title:)/m)
  if (missingDetail)
    fail(index, 1, 'every feature card needs icon, title and details')
  const bodyAfterFrontmatter = text.replace(/^---[\s\S]*?---\n/, '')
  if (bodyAfterFrontmatter.trim().length > 0)
    fail(index, 1, 'landing page must be frontmatter only: move body content to a page or CONTRIBUTING.md')
}

// ---- 2. Container and code-group balance, badges, --- rules ----
for (const file of files) {
  const text = readFileSync(file, 'utf8')
  const stripped = text.replace(/^---[\s\S]*?---\n/, '') // ignore frontmatter
  const lines = stripped.split('\n')

  let open = null
  let openLine = 0
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].trim()
    if (t.startsWith(':::') && t.length > 3) {
      if (open) { fail(file, i + 1, `container opened while '${open}' is still open`) }
      open = t.slice(3).trim()
      openLine = i + 1
    } else if (t === ':::') {
      if (!open) { fail(file, i + 1, 'container close without an open container') }
      else if (open === 'code-group' && !lines.slice(0, i).some((l, j) => j >= openLine && /```.*\[.+\]/.test(l)))
        fail(file, openLine, 'code-group needs named tabs like ```bash [Docker]')
      open = null
    }
  }
  if (open) fail(file, openLine, `container '${open}' never closed`)

  for (let i = 0; i < lines.length; i++) {
    if (/^---\s*$/.test(lines[i]))
      fail(file, i + 1, 'mid-page --- rule: use headings instead')
    const badge = lines[i].match(/<Badge([^>]*)\/>/)
    if (badge) {
      const attrs = badge[1]
      if (!/type="(tip|warning|danger|info)"/.test(attrs))
        fail(file, i + 1, `<Badge> needs type="tip|warning|danger|info", got: ${badge[0]}`)
      if (!/text="[^"]+"/.test(attrs))
        fail(file, i + 1, '<Badge> needs a non-empty text attribute')
    }
  }
}

if (problems.length > 0) {
  console.error(`structure check failed (${problems.length}):`)
  for (const p of problems) console.error('  ' + p)
  process.exit(1)
}
console.log(`structure check passed (${files.length} markdown files)`)

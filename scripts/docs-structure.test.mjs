// Prove the checker flags each violation class on synthetic files.
import { execSync } from 'node:child_process'
import fs from 'node:fs'
fs.mkdirSync('/tmp/mb-neg/docs', { recursive: true })
fs.mkdirSync('/tmp/mb-neg/scripts', { recursive: true })
fs.copyFileSync('/home/w/Documents/Github/melodybridge/scripts/check-docs-structure.mjs', '/tmp/mb-neg/scripts/check-docs-structure.mjs')
// Case A: wrong layout, unclosed container, badge without type, --- rule
fs.writeFileSync('/tmp/mb-neg/docs/index.md', '---\nlayout: doc\n---\n\n# Not home\n\n::: tip\nnever closed\n\n<Badge text="x"/>\n\n---\n')
let out = ''
try { out = execSync('node scripts/check-docs-structure.mjs 2>&1', { cwd: '/tmp/mb-neg' }).toString() } catch (e) { out = (e.stdout || '') + (e.stderr || '') }
const violations = out.split('\n').filter(l => l.trim().startsWith('docs/'))
console.log('CASE A violation count:', violations.length)
violations.forEach(v => console.log('  ' + v.trim()))
const expected = ['layout: home', 'hero block', 'never closed', 'Badge', '---']
const missing = expected.filter(e => !violations.some(v => v.includes(e)))
console.log('missing expected classes:', missing.length === 0 ? 'NONE - all 5 classes caught' : missing)
// Case B: valid landing page passes
fs.writeFileSync('/tmp/mb-neg/docs/index.md', '---\nlayout: home\n\nhero:\n  name: MelodyBridge\n  text: T\n  tagline: T\n  actions:\n    - theme: brand\n      text: Quick start\n      link: /quickstart\n    - theme: alt\n      text: GitHub\n      link: https://github.com/warreth/melodybridge\n\nfeatures:\n  - icon: A\n    title: A\n    details: A\n  - icon: B\n    title: B\n    details: B\n  - icon: C\n    title: C\n    details: C\n---\n')
let ok = ''
try { ok = execSync('node scripts/check-docs-structure.mjs 2>&1', { cwd: '/tmp/mb-neg' }).toString() } catch (e) { ok = (e.stdout || '') + (e.stderr || '') }
console.log('CASE B:', ok.trim())
if (missing.length !== 0 || !ok.includes('passed')) process.exit(1)
console.log('NEGATIVE TESTS PASS')
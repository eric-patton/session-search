#!/usr/bin/env node
// view.mjs - generate spec/view.html, a self-contained, interactive HTML view of the whole workspace.
//
// The per-feature feature.md / spec.md / plan.md / tasks.md are CANONICAL. view.html is GENERATED and
// must never be hand-edited - it is a pure function of the inputs (no timestamps), so `--check` is a
// clean equality test, exactly like assemble.mjs / dashboard.mjs.
//
// It reads what the human already authored (manifests + spec slices + the shared docs) and renders one
// self-contained page - inlined CSS + JS, zero external requests - so a non-technical stakeholder can
// open spec/view.html in a browser and see the product without reading a pile of markdown files.
//
// Usage:
//   node scripts/view.mjs [--root <specDir>] [--out <file>]   # regenerate and WRITE the HTML
//   node scripts/view.mjs --check [--root ...]                 # verify on-disk view.html is current
//
// Exit codes: 0 ok / up-to-date · 1 stale or missing (--check) · 2 hard error.
// Runtime: Node >=18, zero dependencies, cross-platform.

import { readFileSync, existsSync, writeFileSync, readdirSync, statSync } from 'node:fs'
import { join, basename, resolve } from 'node:path'
import { createHash } from 'node:crypto'

const args = process.argv.slice(2)
const check = args.includes('--check')
const rootIdx = args.indexOf('--root')
const root = rootIdx !== -1 && args[rootIdx + 1] ? args[rootIdx + 1] : 'spec'
const outIdx = args.indexOf('--out')
const outPath = outIdx !== -1 && args[outIdx + 1] ? args[outIdx + 1] : join(root, 'view.html')

const warn = (m) => process.stderr.write(`view: warn: ${m}\n`)
const fail = (m) => { process.stderr.write(`view: error: ${m}\n`); process.exit(2) }
const readUtf8 = (p) => readFileSync(p, 'utf8')
const readIf = (p) => (existsSync(p) ? readUtf8(p) : null)
const fileHash = (p) => 'sha256:' + createHash('sha256').update(readFileSync(p)).digest('hex').slice(0, 12)

// ---------------------------------------------------------------------------
// Manifest parsing - the same minimal reader dashboard.mjs uses (§2 manifest shape). Kept standalone
// so this script stays copyable on its own, matching the other scripts' zero-dependency discipline.
// ---------------------------------------------------------------------------

function frontmatterBlock(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---/)
  return m ? m[1] : null
}
function bodyAfterFrontmatter(text) {
  const m = text.match(/^---\r?\n[\s\S]*?\r?\n---\r?\n?([\s\S]*)$/)
  return m ? m[1] : text
}
function scalar(v) {
  const lead = v.replace(/^[ \t]+/, '')
  if (lead === '') return ''
  if (lead[0] === '"' || lead[0] === "'") {
    const q = lead[0]
    const end = lead.indexOf(q, 1)
    return end === -1 ? lead.slice(1) : lead.slice(1, end)
  }
  return lead.replace(/(^|\s)#.*$/, '').trim()
}
function splitTopLevelCommas(s) {
  const parts = []
  let cur = ''
  let q = null
  for (const ch of s) {
    if (q) { cur += ch; if (ch === q) q = null; continue }
    if (ch === '"' || ch === "'") { q = ch; cur += ch; continue }
    if (ch === ',') { parts.push(cur); cur = ''; continue }
    cur += ch
  }
  if (cur.trim() !== '') parts.push(cur)
  return parts
}
function parseInlineObject(line) {
  const inner = line.replace(/^\s*\{\s*/, '').replace(/\s*\}\s*$/, '')
  const obj = {}
  for (const pair of splitTopLevelCommas(inner)) {
    const idx = pair.indexOf(':')
    if (idx === -1) continue
    const key = pair.slice(0, idx).trim()
    if (key) obj[key] = scalar(pair.slice(idx + 1))
  }
  return obj
}
function parseManifest(text) {
  const fm = frontmatterBlock(text)
  if (fm == null) return null
  const out = {
    id: '', slug: '', title: '', owner: '', status: '', depth: '', sprint: '',
    external: null, readiness: {}, gate: {}, converge: null, depends_on: [],
    human_signoff: [], open_decisions: [], overrides: [], hypotheses: [], extends: [],
  }
  let section = null
  let listRef = null
  let item = null
  const closeItem = () => { if (item && listRef) { listRef.push(item); item = null } }
  for (const raw of fm.split(/\r?\n/)) {
    if (!raw.trim()) continue
    const indent = raw.length - raw.replace(/^[ \t]+/, '').length
    if (indent === 0) {
      closeItem(); listRef = null; section = null
      const m = raw.match(/^([A-Za-z_][\w-]*):[ \t]*(.*)$/)
      if (!m) continue
      const key = m[1]
      const val = scalar(m[2])
      if (val === '') {
        section = key
        if (key === 'human_signoff') listRef = out.human_signoff
        else if (key === 'open_decisions') listRef = out.open_decisions
        else if (key === 'overrides') listRef = out.overrides
        else if (key === 'hypotheses') listRef = out.hypotheses
        else if (key === 'extends') listRef = out.extends
        else if (key === 'external') out.external = {}
        else if (key === 'converge') out.converge = {}
      } else if (val === '[]') {
        // empty inline list - keep the seeded default
      } else if (key === 'depends_on' && val.startsWith('[')) {
        out.depends_on = val.replace(/^\[|\]$/g, '').split(',').map((s) => s.trim()).filter(Boolean)
      } else {
        out[key] = val
      }
    } else if (section === 'readiness' || section === 'gate' || section === 'external' || section === 'converge') {
      const mm = raw.match(/^[ \t]+([A-Za-z_][\w-]*):[ \t]*(.*)$/)
      if (mm && out[section] && typeof out[section] === 'object') out[section][mm[1]] = scalar(mm[2])
    } else if (section === 'depends_on') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) out.depends_on.push(scalar(dash[1]))
    } else if (section === 'human_signoff' || section === 'open_decisions' || section === 'overrides' || section === 'hypotheses' || section === 'extends') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) {
        closeItem()
        const rest = dash[1].trim()
        if (rest.startsWith('{')) {
          item = parseInlineObject(rest)
        } else {
          item = {}
          const kv = rest.match(/^([A-Za-z_][\w-]*):[ \t]*(.*)$/)
          if (kv) item[kv[1]] = scalar(kv[2])
        }
      } else if (item) {
        const kv = raw.match(/^[ \t]+([A-Za-z_][\w-]*):[ \t]*(.*)$/)
        if (kv) item[kv[1]] = scalar(kv[2])
      }
    }
  }
  closeItem()
  return out
}

// ---------------------------------------------------------------------------
// Minimal Markdown -> HTML renderer (block + inline). Zero dependencies. Handles the constructs the
// spec-flow artifacts actually use: headings, task/bullet/ordered lists (nested), GFM tables,
// fenced + inline code, blockquotes, rules, bold/italic/strike/links. Rendered once here in Node and
// embedded as HTML in the page, so the browser needs no markdown library.
// ---------------------------------------------------------------------------

const SENT = '' // private-use sentinel to shield inline code spans from the other inline passes

function escapeHtml(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}
// Neutralise a link/image URL: allow relative refs and http(s)/mailto/tel; drop javascript:/data:/vbscript:
// and any other scheme (stored markdown is trusted-ish, but a smuggled scheme is a cheap XSS to close).
function safeUrl(u) {
  const scheme = u.match(/^\s*([a-z][a-z0-9+.-]*):/i)
  if (scheme && !/^(https?|mailto|tel)$/i.test(scheme[1])) return '#'
  return u
}
function inline(raw) {
  const codes = []
  let s = raw.replace(/`([^`]+)`/g, (_, c) => { codes.push(c); return SENT + 'C' + (codes.length - 1) + SENT })
  s = escapeHtml(s)
  s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)/g, (_, a, u) => `<img alt="${a}" src="${safeUrl(u)}">`)
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)/g, (_, t, u) => {
    const ext = /^https?:/i.test(u)
    return `<a href="${safeUrl(u)}"${ext ? ' target="_blank" rel="noopener"' : ''}>${t}</a>`
  })
  s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
  s = s.replace(/__([^_]+)__/g, '<strong>$1</strong>')
  s = s.replace(/(^|[^*])\*([^*\s][^*]*?)\*(?!\*)/g, '$1<em>$2</em>')
  s = s.replace(/(^|[^\w])_([^_\s][^_]*?)_(?![\w])/g, '$1<em>$2</em>')
  s = s.replace(/~~([^~]+)~~/g, '<del>$1</del>')
  s = s.replace(new RegExp(SENT + 'C(\\d+)' + SENT, 'g'), (_, i) => `<code>${escapeHtml(codes[+i])}</code>`)
  return s
}
const TASK = { ' ': 'todo', 'x': 'done', 'X': 'done', '~': 'wip', '-': 'na', 'H': 'human' }
function renderListItemText(t) {
  const m = t.match(/^\[([ xX~\-H])\]\s+(.*)$/)
  if (m) {
    const kind = TASK[m[1]] || 'todo'
    const box = kind === 'done' ? '&#10003;' : kind === 'wip' ? '&#9646;' : kind === 'na' ? '&#8722;' : kind === 'human' ? '&#9873;' : '&#9744;'
    return `<span class="task task-${kind}"><span class="box">${box}</span>${inline(m[2])}</span>`
  }
  return inline(t)
}
function markdown(src) {
  if (src == null) return ''
  const lines = src.replace(/\r\n/g, '\n').replace(/\t/g, '    ').split('\n')
  const out = []
  let i = 0
  const isBlank = (l) => l.trim() === ''
  const listMatch = (l) => l.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/)
  while (i < lines.length) {
    const line = lines[i]
    const fence = line.match(/^(\s*)(```+|~~~+)(.*)$/)
    if (fence) {
      const marker = fence[2][0]
      const body = []
      i++
      while (i < lines.length && !new RegExp('^\\s*' + marker + '{3,}\\s*$').test(lines[i])) { body.push(lines[i]); i++ }
      i++
      out.push(`<pre><code>${escapeHtml(body.join('\n'))}</code></pre>`)
      continue
    }
    if (isBlank(line)) { i++; continue }
    const h = line.match(/^(#{1,6})\s+(.*?)\s*#*$/)
    if (h) { const lvl = h[1].length; out.push(`<h${lvl}>${inline(h[2])}</h${lvl}>`); i++; continue }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push('<hr>'); i++; continue }
    if (line.includes('|') && i + 1 < lines.length && /^\s*\|?[\s:|-]+\|?\s*$/.test(lines[i + 1]) && /-/.test(lines[i + 1])) {
      const splitRow = (r) => r.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim())
      const heads = splitRow(line)
      const aligns = splitRow(lines[i + 1]).map((c) => {
        const l = c.startsWith(':'), r = c.endsWith(':')
        return l && r ? 'center' : r ? 'right' : l ? 'left' : ''
      })
      i += 2
      const rows = []
      while (i < lines.length && lines[i].includes('|') && !isBlank(lines[i])) { rows.push(splitRow(lines[i])); i++ }
      let t = '<div class="tablewrap"><table><thead><tr>'
      heads.forEach((hh, c) => { t += `<th${aligns[c] ? ` style="text-align:${aligns[c]}"` : ''}>${inline(hh)}</th>` })
      t += '</tr></thead><tbody>'
      for (const r of rows) {
        t += '<tr>'
        heads.forEach((_, c) => { t += `<td${aligns[c] ? ` style="text-align:${aligns[c]}"` : ''}>${inline(r[c] || '')}</td>` })
        t += '</tr>'
      }
      t += '</tbody></table></div>'
      out.push(t)
      continue
    }
    if (/^\s*>/.test(line)) {
      const buf = []
      while (i < lines.length && /^\s*>/.test(lines[i])) { buf.push(lines[i].replace(/^\s*>\s?/, '')); i++ }
      out.push(`<blockquote>${markdown(buf.join('\n'))}</blockquote>`)
      continue
    }
    if (listMatch(line)) {
      const parsed = parseList(lines, i)
      out.push(parsed.html)
      i = parsed.next
      continue
    }
    const para = []
    while (i < lines.length && !isBlank(lines[i]) && !/^(#{1,6})\s/.test(lines[i]) &&
      !listMatch(lines[i]) && !/^\s*>/.test(lines[i]) && !/^(\s*)(```+|~~~+)/.test(lines[i]) &&
      !/^\s*([-*_])(\s*\1){2,}\s*$/.test(lines[i])) {
      para.push(lines[i]); i++
    }
    out.push(`<p>${inline(para.join(' '))}</p>`)
  }
  return out.join('\n')
}
function parseList(lines, start) {
  const baseIndent = lines[start].match(/^(\s*)/)[1].length
  const ordered = /^\s*\d+[.)]\s/.test(lines[start])
  let html = ordered ? '<ol>' : '<ul>'
  let i = start
  while (i < lines.length) {
    const line = lines[i]
    if (line.trim() === '') { i++; continue }
    const m = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/)
    if (!m) break
    const indent = m[1].length
    if (indent < baseIndent) break
    if (indent > baseIndent) {
      const sub = parseList(lines, i)
      html = html.replace(/<\/li>$/, sub.html + '</li>')
      i = sub.next
      continue
    }
    html += `<li>${renderListItemText(m[3])}</li>`
    i++
  }
  html += ordered ? '</ol>' : '</ul>'
  return { html, next: i }
}

// ---------------------------------------------------------------------------
// Workspace load
// ---------------------------------------------------------------------------

function firstHeading(text) {
  if (!text) return null
  const m = text.match(/^#\s+(.+)$/m)
  return m ? m[1].trim() : null
}

const READINESS = ['research', 'design', 'spec', 'plan', 'tasks']
const depthOf = (f) => f.depth || 'mvp'

const FEATURE_DOCS = [
  ['spec', 'spec.md'], ['plan', 'plan.md'], ['tasks', 'tasks.md'],
  ['research', 'research.md'], ['design', 'design.md'], ['converge', 'converge.md'],
]

function loadFeatures(featuresDir) {
  if (!existsSync(featuresDir)) return []
  const slugs = readdirSync(featuresDir).filter((d) => {
    try { return statSync(join(featuresDir, d)).isDirectory() } catch { return false }
  })
  const features = []
  for (const slug of slugs) {
    const manifestPath = join(featuresDir, slug, 'feature.md')
    if (!existsSync(manifestPath)) { warn(`features/${slug}/ has no feature.md - skipping`); continue }
    const text = readUtf8(manifestPath)
    const fm = parseManifest(text)
    if (!fm) { warn(`features/${slug}/feature.md has no frontmatter - skipping`); continue }
    if (!fm.id || !fm.slug || !fm.status) { warn(`features/${slug}/feature.md missing id/slug/status - skipping`); continue }
    if (!fm.title) fm.title = fm.slug
    if (fm.status === 'dropped') continue
    const docs = {}
    for (const [key, file] of FEATURE_DOCS) docs[key] = readIf(join(featuresDir, slug, file))
    fm._notes = bodyAfterFrontmatter(text).trim()
    fm._docs = docs
    features.push(fm)
  }
  features.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  return features
}

function boardMode(rootDir) {
  const t = readIf(join(rootDir, '.spec-flow.md'))
  if (!t) return null
  const fm = frontmatterBlock(t)
  if (fm == null) return null
  let inBoard = false
  for (const raw of fm.split(/\r?\n/)) {
    if (!raw.trim()) continue
    const indent = raw.length - raw.replace(/^[ \t]+/, '').length
    if (indent === 0) inBoard = /^board:[ \t]*(#.*)?$/.test(raw)
    else if (inBoard) { const mm = raw.match(/^[ \t]+mode:[ \t]*(.*)$/); if (mm) return scalar(mm[1]) }
  }
  return null
}

function build() {
  const pg = readIf(join(root, 'product-global.md'))
  const con = readIf(join(root, 'constitution.md'))
  const features = loadFeatures(join(root, 'features'))
  const doneIds = new Set(features.filter((f) => f.status === 'done').map((f) => f.id))
  const livePG = existsSync(join(root, 'product-global.md')) ? fileHash(join(root, 'product-global.md')) : null
  const liveCon = existsSync(join(root, 'constitution.md')) ? fileHash(join(root, 'constitution.md')) : null

  const rawName = firstHeading(pg) || firstHeading(con) || basename(resolve(root, '..')) || 'Product'
  // Strip a leading doc-label prefix ("Product-global - X", "Constitution - X") so the header shows the
  // product, not the file the name was lifted from.
  const productName = rawName.replace(/^\s*(product[\s-]*global|product|constitution)\s*(?:\u2014|:|\u2013|-)\s*/i, '').trim() || rawName

  const gateStale = (f) => {
    const res = (f.gate && f.gate.analyze) || 'not-run'
    if (!['pass', 'blocking', 'blocking-hard'].includes(res)) return false
    if (livePG != null && (f.gate.product_global_hash || '') !== livePG) return true
    if (liveCon != null && (f.gate.constitution_hash || '') !== liveCon) return true
    return false
  }
  const depsBlocked = (f) =>
    f.depends_on.some((d) => !doneIds.has(d)) && (!f.readiness.tasks || f.readiness.tasks === 'none')

  const feats = features.map((f) => ({
    id: f.id,
    slug: f.slug,
    title: f.title,
    owner: f.owner || '',
    status: (f.status === 'active' && depsBlocked(f)) ? 'active (blocked)' : f.status,
    depth: depthOf(f),
    sprint: (f.sprint && f.sprint !== 'null') ? f.sprint : '',
    deps: f.depends_on.map((d) => ({ id: d, done: doneIds.has(d) })),
    readiness: READINESS.reduce((o, k) => (o[k] = (f.readiness && f.readiness[k]) || 'none', o), {}),
    analyze: (f.gate && f.gate.analyze) || 'not-run',
    stale: gateStale(f),
    drift: f.converge ? { open: Number(f.converge.open || 0), contradicts: Number(f.converge.contradicts || 0), last_run: f.converge.last_run || '' } : null,
    hypotheses: (f.hypotheses || []).map((h) => ({ id: h.id || '', statement: h.statement || '', status: h.status || 'unvalidated' })),
    extends: (f.extends || []).map((e) => ({ id: e.id || '', feature: e.feature || '', what: e.what || '' })),
    openDecisions: (f.open_decisions || []).map((d) => ({ id: d.id || '', description: d.description || '', resolved: String(d.resolved).trim() === 'true' })),
    humanSignoff: (f.human_signoff || []).map((d) => ({ id: d.id || '', description: d.description || '', resolved: String(d.resolved).trim() === 'true' })),
    overrides: (f.overrides || []).map((d) => ({ id: d.id || '', gate: d.gate || '', by: d.by || '', reason: d.reason || '', at: d.at || '', resolved: String(d.resolved).trim() === 'true' })),
    docs: Object.fromEntries(FEATURE_DOCS.map(([k]) => [k, f._docs[k] ? markdown(f._docs[k]) : null])),
    notes: f._notes ? markdown(f._notes) : null,
  }))

  const docs = [
    ['constitution', 'Constitution', con],
    ['product-global', 'Product-global', pg],
    ['engineering', 'Engineering', readIf(join(root, 'engineering.md'))],
    ['design-system', 'Design system', readIf(join(root, 'design-system.md'))],
    ['roles', 'Roles', readIf(join(root, 'roles.md'))],
  ].filter(([, , t]) => t != null).map(([key, label, t]) => ({ key, label, html: markdown(t) }))

  const data = { productName, boardMode: boardMode(root) || 'none', features: feats, docs }
  return renderPage(data)
}

// ---------------------------------------------------------------------------
// Page template - inlined CSS + JS, one JSON data island. No external requests, theme-aware.
// ---------------------------------------------------------------------------

function renderPage(data) {
  const dataJson = JSON.stringify(data)
    .replace(/</g, '\\u003c').replace(/>/g, '\\u003e').replace(/&/g, '\\u0026')
  return [
    '<!doctype html>',
    '<!-- GENERATED by scripts/view.mjs - DO NOT EDIT. Regenerate: node scripts/view.mjs. Source of truth: spec/features/<slug>/*.md and the shared spec/ docs. -->',
    '<html lang="en"><head><meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    `<title>${escapeHtml(data.productName)} - spec-flow</title>`,
    `<style>${CSS}</style>`,
    '</head><body>',
    '<div id="app"></div>',
    `<script id="data" type="application/json">${dataJson}</script>`,
    `<script>${CLIENT_JS}</script>`,
    '</body></html>', '',
  ].join('\n')
}

const CSS = `
:root{
  --bg:#eceff4; --panel:#ffffff; --panel-2:#f6f8fa; --ink:#10151f; --muted:#48505e; --faint:#707988;
  --line:#dbe0e8; --line-2:#e8ecf2; --accent:#4338ca; --mark:#4338ca;
  --shadow:0 1px 2px rgba(16,21,31,.05),0 6px 20px -14px rgba(16,21,31,.18);
  --g:#137a3b; --gbg:#e2f2e8; --a:#9c5405; --abg:#f9efdd; --r:#b01640; --rbg:#fbe7ec; --b:#4340c9; --bbg:#e9e8fb; --n:#5a6472; --nbg:#edf0f4; --p:#6b3fa0; --pbg:#f0e9fa;
  --radius:12px;
  --display:Cambria,Georgia,'Times New Roman',serif;
  --mono:"Cascadia Code",Consolas,ui-monospace,SFMono-Regular,Menlo,monospace;
}
@media (prefers-color-scheme: dark){
  :root:not([data-theme="light"]){
    --bg:#0c111b; --panel:#131a28; --panel-2:#101725; --ink:#e9edf4; --muted:#aeb7c6; --faint:#7f8a9c;
    --line:#26324a; --line-2:#1e2940; --accent:#8f92f5; --mark:#7a7df0; --shadow:none;
    --g:#4cc07a; --gbg:#132b1d; --a:#dfa04a; --abg:#2c2110; --r:#ef5a80; --rbg:#31121d; --b:#8f92f5; --bbg:#1d2040; --n:#8791a3; --nbg:#1b2334; --p:#c79bff; --pbg:#261837;
  }
}
:root[data-theme="dark"]{
  --bg:#0c111b; --panel:#131a28; --panel-2:#101725; --ink:#e9edf4; --muted:#aeb7c6; --faint:#7f8a9c;
  --line:#26324a; --line-2:#1e2940; --accent:#8f92f5; --mark:#7a7df0; --shadow:none;
  --g:#4cc07a; --gbg:#132b1d; --a:#dfa04a; --abg:#2c2110; --r:#ef5a80; --rbg:#31121d; --b:#8f92f5; --bbg:#1d2040; --n:#8791a3; --nbg:#1b2334; --p:#c79bff; --pbg:#261837;
}
*{box-sizing:border-box}
html,body{margin:0;padding:0}
body{background:var(--bg);color:var(--ink);font:15px/1.6 "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,Roboto,Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
::selection{background:var(--bbg)}
:focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:4px}
.app{display:grid;grid-template-columns:280px 1fr;min-height:100vh}
.side{background:var(--panel);border-right:1px solid var(--line);position:sticky;top:0;height:100vh;overflow-y:auto;padding:18px 14px}
.brand{display:flex;align-items:center;gap:11px;padding:6px 8px 16px}
.brand .logo{width:32px;height:32px;border-radius:9px;background:linear-gradient(135deg,var(--mark),var(--b));display:flex;align-items:center;justify-content:center;color:#fff;font:700 17px/1 var(--display);flex:0 0 auto}
.brand .name{font:700 15.5px/1.25 var(--display);letter-spacing:-.01em}
.brand .sub{color:var(--faint);font:600 10.5px/1.6 var(--mono);letter-spacing:.12em;text-transform:uppercase}
.navlabel{color:var(--faint);font:600 10.5px/1.4 var(--mono);letter-spacing:.12em;text-transform:uppercase;margin:18px 8px 7px}
.navitem{display:flex;align-items:center;gap:8px;padding:7px 9px;border-radius:8px;color:var(--ink);cursor:pointer;font-size:13.5px;border:1px solid transparent}
.navitem:hover{background:var(--panel-2)}
.navitem.on{background:var(--bbg);border-color:var(--line);color:var(--ink);font-weight:600;box-shadow:inset 2.5px 0 0 var(--mark)}
.navitem .tag{margin-left:auto;font:500 11px/1.6 var(--mono);color:var(--faint)}
.navitem .dot{width:7px;height:7px;border-radius:50%;flex:0 0 auto}
.main{padding:28px 34px 80px;max-width:1180px;width:100%}
.topbar{display:flex;align-items:center;gap:14px;margin-bottom:6px}
.h1{font:700 27px/1.2 var(--display);letter-spacing:-.012em;margin:0}
.crumbs{color:var(--faint);font:500 12px/1.6 var(--mono);margin-bottom:20px}
.link{color:var(--accent);cursor:pointer}
.spacer{flex:1}
.iconbtn{border:1px solid var(--line);background:var(--panel);color:var(--muted);border-radius:999px;padding:6px 12px;cursor:pointer;font:600 12.5px/1.5 var(--mono)}
.iconbtn:hover{color:var(--ink);border-color:var(--faint)}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:8px 0 24px}
.stat{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:15px 17px;box-shadow:var(--shadow)}
.stat .n{font:700 30px/1.05 var(--display);letter-spacing:-.01em;font-variant-numeric:tabular-nums}
.stat .l{color:var(--muted);font-size:12.5px;margin-top:5px}
.stat .bar{display:flex;height:6px;border-radius:4px;overflow:hidden;margin-top:11px;background:var(--line-2);gap:2px}
.stat .bar span{display:block;border-radius:2px}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);box-shadow:var(--shadow);overflow:hidden;margin-bottom:20px}
.panel-h{display:flex;align-items:center;gap:10px;padding:12px 17px;border-bottom:1px solid var(--line);font:700 14px/1.5 var(--display);letter-spacing:.005em}
.panel-b{padding:16px 18px}
.controls{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:0 0 14px}
.search{flex:1;min-width:180px;border:1px solid var(--line);background:var(--panel);color:var(--ink);border-radius:8px;padding:8px 12px;font-size:13.5px}
.search:focus{outline:none;border-color:var(--accent)}
select.filter{border:1px solid var(--line);background:var(--panel);color:var(--ink);border-radius:8px;padding:8px 10px;font-size:13px;cursor:pointer}
.tablewrap{overflow-x:auto;border-radius:8px}
table{border-collapse:collapse;width:100%;font-size:13px}
thead th{text-align:left;color:var(--faint);font:600 10.5px/1.6 var(--mono);letter-spacing:.09em;text-transform:uppercase;padding:9px 12px;border-bottom:1px solid var(--line);white-space:nowrap;position:sticky;top:0;background:var(--panel)}
.dash thead th{cursor:pointer;user-select:none}
.dash thead th:hover{color:var(--ink)}
.dash thead th .car{opacity:.5;font-size:10px;margin-left:3px}
tbody td{padding:10px 12px;border-bottom:1px solid var(--line-2);vertical-align:middle}
tbody tr:last-child td{border-bottom:none}
.dash tbody tr{cursor:pointer}
.dash tbody tr:hover{background:var(--panel-2)}
.fid{font-family:var(--mono);font-size:12px;color:var(--muted)}
.ftitle{font-weight:600}
.fslug{color:var(--faint);font:400 11.5px/1.5 var(--mono)}
.chip{display:inline-flex;align-items:center;gap:5px;padding:2px 8px;border-radius:999px;font-size:11.5px;font-weight:600;line-height:1.7;white-space:nowrap}
.chip.g{background:var(--gbg);color:var(--g)} .chip.a{background:var(--abg);color:var(--a)} .chip.r{background:var(--rbg);color:var(--r)}
.chip.b{background:var(--bbg);color:var(--b)} .chip.n{background:var(--nbg);color:var(--n)} .chip.p{background:var(--pbg);color:var(--p)}
.rvec{display:inline-flex;gap:3px}
.rseg{width:15px;height:15px;border-radius:4px;display:inline-block}
.rseg.ready{background:var(--g)} .rseg.draft{background:var(--a)} .rseg.none{background:var(--line);border:1px solid var(--line-2)} .rseg.na{background:var(--faint);opacity:.5} .rseg.wip{background:var(--a)}
.deps .chip{margin:1px 2px 1px 0;cursor:pointer}
.detail-head{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:4px}
.meta-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px 20px;margin:6px 0 4px}
.meta .k{color:var(--faint);font-size:11px;letter-spacing:.04em;text-transform:uppercase;margin-bottom:3px}
.meta .v{font-size:13.5px;display:flex;flex-wrap:wrap;gap:5px;align-items:center}
.tabs{display:flex;gap:2px;border-bottom:1px solid var(--line);margin:2px 0 0;flex-wrap:wrap}
.tab{padding:9px 15px;font-size:13px;color:var(--muted);cursor:pointer;border-bottom:2px solid transparent;font-weight:500}
.tab:hover{color:var(--ink)}
.tab.on{color:var(--ink);border-bottom-color:var(--mark);font-weight:600}
.tab.empty{opacity:.35;cursor:default}
.md{font-size:14.5px;line-height:1.65;color:var(--ink);max-width:860px}
.md h1{font:700 24px/1.25 var(--display);letter-spacing:-.01em;margin:24px 0 10px}
.md h2{font:700 19.5px/1.3 var(--display);letter-spacing:-.008em;margin:28px 0 10px;padding-bottom:7px;border-bottom:1px solid var(--line)}
.md h3{font-size:15.5px;margin:20px 0 8px;font-weight:700}
.md h4,.md h5,.md h6{font-size:14px;margin:16px 0 6px;font-weight:700;color:var(--muted)}
.md p{margin:10px 0}
.md ul,.md ol{margin:10px 0;padding-left:22px}
.md li{margin:4px 0}
.md ul ul,.md ul ol,.md ol ul,.md ol ol{margin:4px 0}
.md code{background:var(--nbg);padding:1.5px 5px;border-radius:5px;font-family:var(--mono);font-size:12.5px}
.md pre{background:var(--panel-2);border:1px solid var(--line);border-radius:8px;padding:12px 14px;overflow-x:auto;margin:12px 0;font-family:var(--mono)}
.md pre code{background:none;padding:0;font-size:12.5px;line-height:1.5}
.md blockquote{margin:12px 0;padding:2px 14px;border-left:3px solid var(--mark);background:var(--panel-2);color:var(--muted);border-radius:0 6px 6px 0}
.md table{margin:12px 0;font-size:13px}
.md th,.md td{border:1px solid var(--line);padding:7px 10px}
.md th{background:var(--panel-2)}
.md hr{border:none;border-top:1px solid var(--line);margin:20px 0}
.md a{word-break:break-word}
.md img{max-width:100%}
.md .task{display:inline-flex;gap:7px;align-items:baseline}
.md .task .box{font-size:13px;flex:0 0 auto;color:var(--faint)}
.md .task-done .box{color:var(--g)} .md .task-done{color:var(--muted)}
.md .task-wip .box{color:var(--a)} .md .task-human .box{color:var(--p)} .md .task-na{color:var(--faint)}
.items{list-style:none;padding:0;margin:6px 0}
.items li{display:flex;gap:8px;align-items:baseline;padding:7px 0;border-bottom:1px solid var(--line-2)}
.items li:last-child{border-bottom:none}
.items .id{font-family:var(--mono);font-size:11.5px;color:var(--faint);flex:0 0 auto}
.empty-note{color:var(--faint);font-size:13.5px;padding:6px 0}
@media (max-width:820px){
  .app{grid-template-columns:1fr}
  .side{position:static;height:auto;border-right:none;border-bottom:1px solid var(--line)}
  .main{padding:18px 16px 60px}
}
`

// NOTE for maintainers: the client script uses DATA ATTRIBUTES + a single delegated listener (never
// inline onclick), specifically so no HTML string here needs an escaped quote. Keep it that way - inline
// handlers reintroduce nested-quote escaping that is painful to get right inside this template literal.
const CLIENT_JS = `
(function(){
  var DATA = JSON.parse(document.getElementById("data").textContent);
  var app = document.getElementById("app");
  function esc(s){return String(s==null?"":s).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;");}

  var stored=null; try{stored=localStorage.getItem("sf-theme");}catch(e){}
  if(stored) document.documentElement.setAttribute("data-theme", stored);
  function toggleTheme(){
    var cur=document.documentElement.getAttribute("data-theme");
    var mq=window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    var next=cur?(cur==="dark"?"light":"dark"):(mq?"light":"dark");
    document.documentElement.setAttribute("data-theme", next);
    try{localStorage.setItem("sf-theme", next);}catch(e){}
    render();
  }

  // Opt-in auto-refresh: a file:// page cannot poll its own mtime, so freshness is reload-based:
  // reload every AR_MS while the toggle is on, restoring scroll via sessionStorage (the hash route
  // survives a reload on its own). Off by default; the preference persists in localStorage.
  var AR_MS=5000, arTimer=null;
  function arOn(){ var v=null; try{v=localStorage.getItem("sf-autorefresh");}catch(e){} return v==="on"; }
  function scheduleAR(){
    if(arTimer){ clearTimeout(arTimer); arTimer=null; }
    if(!arOn()) return;
    arTimer=setTimeout(function(){
      try{sessionStorage.setItem("sf-ar-scroll", String(window.pageYOffset||0));}catch(e){}
      location.reload();
    }, AR_MS);
  }
  function toggleAR(){
    try{localStorage.setItem("sf-autorefresh", arOn()?"off":"on");}catch(e){}
    scheduleAR(); render();
  }

  function gateClass(g,stale){ if(stale) return "a"; if(g==="pass") return "g"; if(g==="blocking"||g==="blocking-hard") return "r"; return "n"; }
  function statusClass(s){ if(s==="done") return "g"; if(s==="dropped") return "n"; if(s.indexOf("blocked")>=0) return "a"; return "b"; }
  function depthClass(d){ return d==="ga"?"p":d==="mvp"?"b":"n"; }
  function readyClass(r){ return r==="ready"?"ready":r==="draft"?"draft":r==="n/a"?"na":r==="in-progress"?"wip":"none"; }
  function hypClass(s){ return s==="confirmed"?"g":s==="refuted"?"r":s==="net-new"?"b":"n"; }
  function driftClass(d){ if(!d) return "n"; if(d.open===0) return "g"; return d.contradicts>0?"r":"a"; }

  function chip(cls,txt){ return "<span class=\\"chip "+cls+"\\">"+esc(txt)+"</span>"; }
  function chipNav(cls,txt,route){ return "<span class=\\"chip "+cls+"\\" data-nav=\\""+esc(route)+"\\">"+esc(txt)+"</span>"; }
  function rvec(r){
    var order=["research","design","spec","plan","tasks"];
    var title=order.map(function(k){return k+": "+(r[k]||"none");}).join("  ");
    var h="<span class=\\"rvec\\" title=\\""+esc(title)+"\\">";
    order.forEach(function(k){ h+="<span class=\\"rseg "+readyClass(r[k])+"\\"></span>"; });
    return h+"</span>";
  }
  function driftCell(d){ if(!d) return chip("n","-"); if(d.open===0) return chip("g","clean"); return chip(driftClass(d), d.open+" open"+(d.contradicts>0?" ("+d.contradicts+" contra)":"")); }
  function depChips(f){
    if(!f.deps.length) return chip("n","-");
    return f.deps.map(function(d){ return chipNav(d.done?"n":"a", d.id.replace("feat-","")+(d.done?"":"!"), depRoute(d.id)); }).join("");
  }
  function depRoute(id){ var t=DATA.features.filter(function(x){return x.id===id;})[0]; return t?"feature:"+t.slug:"overview"; }

  function current(){ return decodeURIComponent((location.hash||"#overview").slice(1)); }
  function go(route){ location.hash=route; }
  window.addEventListener("hashchange", render);

  var sortKey="id", sortDir=1;

  function sidebar(){
    var route=current();
    var h="<div class=\\"side\\">";
    h+="<div class=\\"brand\\"><div class=\\"logo\\">"+esc((DATA.productName||"P").slice(0,1).toUpperCase())+"</div>";
    h+="<div><div class=\\"name\\">"+esc(DATA.productName)+"</div><div class=\\"sub\\">spec-flow</div></div></div>";
    h+="<div class=\\"navlabel\\">Workspace</div>";
    h+=navitem("overview","Overview",route,DATA.features.length+" features");
    h+="<div class=\\"navlabel\\">Features</div>";
    DATA.features.forEach(function(f){
      var cls=gateClass(f.analyze,f.stale);
      var col=cls==="g"?"var(--g)":cls==="r"?"var(--r)":cls==="a"?"var(--a)":"var(--faint)";
      var on=route.indexOf("feature:"+f.slug)===0?" on":"";
      h+="<div class=\\"navitem"+on+"\\" data-nav=\\"feature:"+f.slug+"\\"><span class=\\"dot\\" style=\\"background:"+col+"\\"></span>"+esc(f.title)+"<span class=\\"tag\\">"+esc(f.id.replace("feat-",""))+"</span></div>";
    });
    if(DATA.docs.length){
      h+="<div class=\\"navlabel\\">Shared docs</div>";
      DATA.docs.forEach(function(d){ h+=navitem("doc:"+d.key,d.label,route,""); });
    }
    return h+"</div>";
  }
  function navitem(route,label,cur,tag){
    var on=cur===route?" on":"";
    return "<div class=\\"navitem"+on+"\\" data-nav=\\""+esc(route)+"\\">"+esc(label)+(tag?"<span class=\\"tag\\">"+esc(tag)+"</span>":"")+"</div>";
  }
  function themeBtn(){ return "<button class=\\"iconbtn\\" data-act=\\"theme\\">&#9680; Theme</button>"; }
  function arBtn(){ return "<button class=\\"iconbtn\\" data-act=\\"autorefresh\\" title=\\"Reload every 5s to pick up a regenerated view.html\\">&#8635; Auto-refresh: "+(arOn()?"on":"off")+"</button>"; }

  function stat(n,l,bar){ return "<div class=\\"stat\\"><div class=\\"n\\">"+esc(n)+"</div><div class=\\"l\\">"+esc(l)+"</div>"+(bar||"")+"</div>"; }
  function barSeg(segs,total){ if(!total) return ""; var h="<div class=\\"bar\\">"; segs.forEach(function(s){ h+="<span style=\\"width:"+(100*s[0]/total)+"%;background:"+s[1]+"\\"></span>"; }); return h+"</div>"; }

  function overview(){
    var fs=DATA.features;
    var done=fs.filter(function(f){return f.status==="done";}).length;
    var active=fs.filter(function(f){return f.status.indexOf("active")>=0;}).length;
    var gatesPass=fs.filter(function(f){return f.analyze==="pass"&&!f.stale;}).length;
    var openItems=fs.reduce(function(a,f){return a+unres(f.openDecisions)+unres(f.humanSignoff);},0);
    var overrides=fs.reduce(function(a,f){return a+unres(f.overrides);},0);
    var contra=fs.reduce(function(a,f){return a+(f.drift?f.drift.contradicts:0);},0);

    var h="<div class=\\"topbar\\"><h1 class=\\"h1\\">Overview</h1><div class=\\"spacer\\"></div>"+arBtn()+themeBtn()+"</div>";
    h+="<div class=\\"crumbs\\">"+esc(DATA.productName)+" &middot; board mode: "+esc(DATA.boardMode)+"</div>";
    h+="<div class=\\"stats\\">";
    h+=stat(fs.length,"Features",barSeg([[done,"var(--g)"],[active,"var(--b)"]],fs.length));
    h+=stat(done+" / "+fs.length,"Done","");
    h+=stat(gatesPass+" / "+fs.length,"Analyze gate: pass","");
    h+=stat(openItems,"Open decisions / sign-offs","");
    h+=stat(overrides,"Unresolved overrides","");
    h+=stat(contra,"Drift contradictions","");
    h+="</div>";
    h+="<div class=\\"panel\\"><div class=\\"panel-h\\">Feature dashboard</div><div class=\\"panel-b\\">";
    h+="<div class=\\"controls\\">";
    h+="<input class=\\"search\\" id=\\"q\\" placeholder=\\"Search features\\u2026\\">";
    h+="<select class=\\"filter\\" id=\\"fstatus\\"><option value=\\"\\">All statuses</option><option>done</option><option>active</option><option>dropped</option></select>";
    h+="<select class=\\"filter\\" id=\\"fdepth\\"><option value=\\"\\">All depths</option><option>prototype</option><option>mvp</option><option>ga</option></select>";
    h+="</div><div id=\\"tbl\\"></div></div></div>";
    return h;
  }
  function unres(list){ return list.filter(function(x){return !x.resolved;}).length; }

  var COLS=[["id","ID"],["title","Feature"],["owner","Owner"],["status","Status"],["depth","Depth"],["deps","Deps"],["readiness","Readiness"],["analyze","Gate"],["drift","Drift"],["hyp","Hyp"],["items","Open"]];
  function renderTable(){
    var qEl=document.getElementById("q"); var q=(qEl?qEl.value:"").toLowerCase();
    var fst=(document.getElementById("fstatus")||{}).value||"";
    var fd=(document.getElementById("fdepth")||{}).value||"";
    var rows=DATA.features.filter(function(f){
      if(q && (f.title+" "+f.slug+" "+f.id+" "+f.owner).toLowerCase().indexOf(q)<0) return false;
      if(fst && f.status.indexOf(fst)<0) return false;
      if(fd && f.depth!==fd) return false;
      return true;
    });
    rows.sort(function(a,b){ var av=sortVal(a),bv=sortVal(b); if(av<bv)return -sortDir; if(av>bv)return sortDir; return 0; });
    var h="<div class=\\"tablewrap\\"><table class=\\"dash\\"><thead><tr>";
    COLS.forEach(function(c){ var car=sortKey===c[0]?"<span class=\\"car\\">"+(sortDir>0?"\\u25b2":"\\u25bc")+"</span>":""; h+="<th data-sort=\\""+c[0]+"\\">"+esc(c[1])+car+"</th>"; });
    h+="</tr></thead><tbody>";
    if(!rows.length) h+="<tr><td colspan=\\""+COLS.length+"\\" class=\\"empty-note\\">No features match.</td></tr>";
    rows.forEach(function(f){
      var openN=unres(f.openDecisions)+unres(f.humanSignoff)+unres(f.overrides);
      h+="<tr data-nav=\\"feature:"+f.slug+"\\">";
      h+="<td class=\\"fid\\">"+esc(f.id)+"</td>";
      h+="<td><div class=\\"ftitle\\">"+esc(f.title)+"</div><div class=\\"fslug\\">"+esc(f.slug)+"</div></td>";
      h+="<td>"+esc(f.owner||"-")+"</td>";
      h+="<td>"+chip(statusClass(f.status),f.status)+"</td>";
      h+="<td>"+chip(depthClass(f.depth),f.depth)+"</td>";
      h+="<td class=\\"deps\\">"+depChips(f)+"</td>";
      h+="<td>"+rvec(f.readiness)+"</td>";
      h+="<td>"+chip(gateClass(f.analyze,f.stale),f.analyze+(f.stale?" (stale)":""))+"</td>";
      h+="<td>"+driftCell(f.drift)+"</td>";
      h+="<td>"+(f.hypotheses.length?chip(f.hypotheses.some(function(x){return x.status==="refuted";})?"r":"n",String(f.hypotheses.length)):chip("n","-"))+"</td>";
      h+="<td>"+(openN?chip("a",String(openN)):chip("g","0"))+"</td>";
      h+="</tr>";
    });
    h+="</tbody></table></div>";
    var host=document.getElementById("tbl"); if(host) host.innerHTML=h;
  }
  function sortVal(f){
    switch(sortKey){
      case "title":return f.title.toLowerCase();
      case "owner":return (f.owner||"").toLowerCase();
      case "status":return f.status;
      case "depth":return ({prototype:0,mvp:1,ga:2})[f.depth];
      case "deps":return f.deps.length;
      case "readiness":return ["research","design","spec","plan","tasks"].filter(function(k){return f.readiness[k]==="ready";}).length;
      case "analyze":return f.analyze+(f.stale?"~":"");
      case "drift":return f.drift?(f.drift.contradicts*100+f.drift.open):-1;
      case "hyp":return f.hypotheses.length;
      case "items":return unres(f.openDecisions)+unres(f.humanSignoff)+unres(f.overrides);
      default:return f.id;
    }
  }

  function meta(k,v){ return "<div class=\\"meta\\"><div class=\\"k\\">"+esc(k)+"</div><div class=\\"v\\">"+v+"</div></div>"; }
  function metaBlock(k,items){ return "<div class=\\"meta\\" style=\\"grid-column:1/-1\\"><div class=\\"k\\">"+esc(k)+"</div><ul class=\\"items\\">"+items+"</ul></div>"; }

  function feature(slug){
    var f=DATA.features.filter(function(x){return x.slug===slug;})[0];
    if(!f) return overview();
    var route=current();
    var parts=route.split(":");
    var active=parts[2];

    var h="<div class=\\"topbar\\"><h1 class=\\"h1\\">"+esc(f.title)+"</h1><div class=\\"spacer\\"></div>"+arBtn()+themeBtn()+"</div>";
    h+="<div class=\\"crumbs\\"><span class=\\"link\\" data-nav=\\"overview\\">Overview</span> / "+esc(f.id)+" &middot; "+esc(f.slug)+"</div>";
    h+="<div class=\\"panel\\"><div class=\\"panel-b\\">";
    h+="<div class=\\"detail-head\\">"+chip(statusClass(f.status),f.status)+chip(depthClass(f.depth),"depth: "+f.depth)+chip(gateClass(f.analyze,f.stale),"gate: "+f.analyze+(f.stale?" (stale)":""))+driftCell(f.drift)+"</div>";
    h+="<div class=\\"meta-grid\\">";
    h+=meta("Owner",esc(f.owner||"-"));
    h+=meta("Readiness",rvec(f.readiness)+" <span style=\\"color:var(--faint);font-size:11px;margin-left:6px\\">R&middot;D&middot;S&middot;P&middot;T</span>");
    h+=meta("Depends on", f.deps.length?depChips(f):"<span class=\\"empty-note\\">none</span>");
    if(f.sprint) h+=meta("Sprint",esc(f.sprint));
    if(f.extends.length) h+=meta("Extends", f.extends.map(function(e){return chipNav("b",e.feature,depRoute(e.feature));}).join(" "));
    h+="</div>";

    var oi=f.openDecisions.filter(function(x){return !x.resolved;});
    var hs=f.humanSignoff.filter(function(x){return !x.resolved;});
    var ov=f.overrides.filter(function(x){return !x.resolved;});
    if(oi.length||hs.length||ov.length||f.hypotheses.length){
      h+="<div class=\\"meta-grid\\" style=\\"margin-top:14px\\">";
      if(f.hypotheses.length) h+=metaBlock("Hypotheses", f.hypotheses.map(function(x){return "<li><span class=\\"id\\">"+esc(x.id)+"</span>"+chip(hypClass(x.status),x.status)+" "+esc(x.statement)+"</li>";}).join(""));
      if(oi.length) h+=metaBlock("Open decisions", oi.map(function(x){return "<li><span class=\\"id\\">"+esc(x.id)+"</span>"+esc(x.description)+"</li>";}).join(""));
      if(hs.length) h+=metaBlock("Human sign-off", hs.map(function(x){return "<li><span class=\\"id\\">"+esc(x.id)+"</span>"+esc(x.description)+"</li>";}).join(""));
      if(ov.length) h+=metaBlock("Overrides", ov.map(function(x){return "<li><span class=\\"id\\">"+esc(x.id)+"</span>"+chip("a",x.gate)+" "+esc(x.reason)+" <span style=\\"color:var(--faint)\\">&mdash; "+esc(x.by)+", "+esc(x.at)+"</span></li>";}).join(""));
      h+="</div>";
    }
    h+="</div></div>";

    var order=[["spec","Spec"],["plan","Plan"],["tasks","Tasks"],["research","Research"],["design","Design"],["converge","Converge"],["notes","Notes"]];
    var avail=order.filter(function(t){ return t[0]==="notes"?f.notes:f.docs[t[0]]; });
    if(!active || !order.some(function(t){return t[0]===active && (t[0]==="notes"?f.notes:f.docs[t[0]]);})) active=avail[0]?avail[0][0]:null;
    h+="<div class=\\"panel\\"><div class=\\"tabs\\">";
    order.forEach(function(t){
      var has=t[0]==="notes"?!!f.notes:!!f.docs[t[0]];
      if(!has){ h+="<div class=\\"tab empty\\">"+esc(t[1])+"</div>"; return; }
      var on=active===t[0]?" on":"";
      h+="<div class=\\"tab"+on+"\\" data-nav=\\"feature:"+f.slug+":"+t[0]+"\\">"+esc(t[1])+"</div>";
    });
    h+="</div><div class=\\"panel-b\\"><div class=\\"md\\">";
    var content=active==="notes"?f.notes:(active?f.docs[active]:null);
    h+= content || "<div class=\\"empty-note\\">No document.</div>";
    h+="</div></div></div>";
    return h;
  }

  function doc(key){
    var d=DATA.docs.filter(function(x){return x.key===key;})[0];
    if(!d) return overview();
    var h="<div class=\\"topbar\\"><h1 class=\\"h1\\">"+esc(d.label)+"</h1><div class=\\"spacer\\"></div>"+arBtn()+themeBtn()+"</div>";
    h+="<div class=\\"crumbs\\"><span class=\\"link\\" data-nav=\\"overview\\">Overview</span> / Shared docs / "+esc(d.label)+"</div>";
    h+="<div class=\\"panel\\"><div class=\\"panel-b\\"><div class=\\"md\\">"+d.html+"</div></div></div>";
    return h;
  }

  function render(){
    var route=current();
    var body;
    if(route.indexOf("feature:")===0) body=feature(route.split(":")[1]);
    else if(route.indexOf("doc:")===0) body=doc(route.slice(4));
    else body=overview();
    app.className="app";
    app.innerHTML=sidebar()+"<div class=\\"main\\">"+body+"</div>";
    if(route==="overview"||route==="") renderTable();
    window.scrollTo(0,0);
  }

  // One delegated listener set - survives innerHTML swaps because it is bound to the persistent #app.
  app.addEventListener("click", function(e){
    var nav=e.target.closest("[data-nav]"); if(nav){ go(nav.getAttribute("data-nav")); return; }
    var srt=e.target.closest("[data-sort]"); if(srt){ var k=srt.getAttribute("data-sort"); if(sortKey===k) sortDir*=-1; else {sortKey=k;sortDir=1;} renderTable(); return; }
    var act=e.target.closest("[data-act]"); if(act){ var a=act.getAttribute("data-act"); if(a==="theme") toggleTheme(); else if(a==="autorefresh") toggleAR(); return; }
  });
  app.addEventListener("input", function(e){ if(e.target.id==="q") renderTable(); });
  app.addEventListener("change", function(e){ if(e.target.id==="fstatus"||e.target.id==="fdepth") renderTable(); });

  render();
  // Auto-refresh continuity: restore the pre-reload scroll position, then arm the next cycle.
  try{ var sy=sessionStorage.getItem("sf-ar-scroll"); if(sy!==null){ sessionStorage.removeItem("sf-ar-scroll"); window.scrollTo(0, parseInt(sy,10)||0); } }catch(e){}
  scheduleAR();
})();
`

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

let rendered
try { rendered = build() } catch (e) { fail(e && e.message ? e.message : String(e)) }

if (check) {
  if (!existsSync(outPath)) { process.stderr.write(`view: ${outPath} is missing - run view to generate it\n`); process.exit(1) }
  if (readUtf8(outPath) === rendered) { process.stdout.write(`view: ${outPath} is up to date\n`); process.exit(0) }
  process.stderr.write(`view: ${outPath} is STALE - the workspace has changed since it was generated\n`)
  process.exit(1)
} else {
  writeFileSync(outPath, rendered)
  process.stdout.write(`view: wrote ${outPath}\n`)
  process.exit(0)
}

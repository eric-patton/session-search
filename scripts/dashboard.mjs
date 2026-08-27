#!/usr/bin/env node
// dashboard.mjs - generate spec/dashboard.md, a cross-feature status board from the feature manifests.
//
// feature.md manifests are CANONICAL. dashboard.md is GENERATED and must never be hand-edited.
// Output is a pure function of the inputs (no timestamps), so `--check` is a clean equality test.
//
// Usage:
//   node scripts/dashboard.mjs [--root <specDir>]   # regenerate and WRITE <root>/dashboard.md
//   node scripts/dashboard.mjs --check [--root ...]  # verify on-disk dashboard.md is current (no write)
//
// Exit codes: 0 ok / up-to-date · 1 stale or missing (--check) · 2 hard error.
// Runtime: Node >=18, zero dependencies, cross-platform.

import { readFileSync, existsSync, writeFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { createHash } from 'node:crypto'

const args = process.argv.slice(2)
const check = args.includes('--check')
const rootIdx = args.indexOf('--root')
const root = rootIdx !== -1 && args[rootIdx + 1] ? args[rootIdx + 1] : 'spec'

const warn = (m) => process.stderr.write(`dashboard: warn: ${m}\n`)
const fail = (m) => { process.stderr.write(`dashboard: error: ${m}\n`); process.exit(2) }

const hash = (buf) => 'sha256:' + createHash('sha256').update(buf).digest('hex').slice(0, 12)
// sha256:12 of a file's raw UTF-8 bytes (contract §3) - matches the gate stamp written by `analyze`
// and assemble.mjs's productGlobalHash, so the dashboard can detect a stale per-feature analyze gate.
const fileHash = (p) => 'sha256:' + createHash('sha256').update(readFileSync(p)).digest('hex').slice(0, 12)
const readUtf8 = (p) => readFileSync(p, 'utf8')
const trimEnd = (s) => s.replace(/\s+$/, '')

// Pull the raw text of the first `---`-fenced frontmatter block (no parsing).
function frontmatterBlock(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---/)
  return m ? m[1] : null
}

// Parse the text after `key:` into a YAML scalar value (§11.f).
// - Quoted ("..."/'...') values are literal; a trailing ` # comment` after the closing quote is
//   ignored, and a literal `#` inside the quotes is preserved.
// - Unquoted values have an inline `# comment` stripped - whether the comment trails the value
//   (`foo   # note`) or is the entire remainder (`readiness:   # note`, i.e. a section header).
// Returns '' for an empty- or comment-only value, which lets the caller distinguish a section
// header (`readiness:`) from a scalar assignment (`status: active`).
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

// Split a comma-separated list at the TOP level only (commas inside quotes stay put), so a value like
// reason: "demo Friday, two deferred" survives intact. Sibling of validate.mjs's copy.
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

// Parse a single inline object `{ id: od-1, resolved: false }` into a flat key→value map, reusing
// scalar() for quote/comment handling on each value. Lets an item list use flow-mapping form.
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

// Minimal, dependency-free reader for the §2 manifest shape: top-level scalars (incl. the v2 `depth`),
// the `readiness` and `gate` nested mappings, and the `human_signoff`/`open_decisions`/`overrides`
// item lists. Indentation follows the canonical 2-space template; anything it does not recognise is
// ignored (e.g. the tracker-only `external:` block). v1 manifests parse
// unchanged - missing v2 fields fall back to their defaults (depth => effective mvp, overrides => []).
function parseManifest(text) {
  const fm = frontmatterBlock(text)
  if (fm == null) return null
  const out = {
    id: '', slug: '', title: '', owner: '', status: '', sprint: '',
    external: null, readiness: {}, gate: {}, converge: null, depends_on: [],
    human_signoff: [], open_decisions: [], overrides: [], hypotheses: [], extends: [],
  }
  let section = null     // active top-level parent key
  let listRef = null     // current list array (human_signoff | open_decisions | overrides)
  let item = null        // current list item being accumulated
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
        else if (key === 'hypotheses') listRef = out.hypotheses // intake-stamped discovery hypotheses (§8)
        else if (key === 'extends') listRef = out.extends // cross-feature additive-extension annotations (§11.t)
        else if (key === 'external') out.external = {} // tracker-only nested block (provider/id/url)
        else if (key === 'converge') out.converge = {} // converge-skill stamp (last_run/open/contradicts)
      } else if (val === '[]') {
        // empty inline list / empty collection - leave the seeded default in place
      } else if (key === 'depends_on' && val.startsWith('[')) {
        // inline list of feat-ids (contract §11.p - inline form only)
        out.depends_on = val.replace(/^\[|\]$/g, '').split(',').map((s) => s.trim()).filter(Boolean)
      } else {
        out[key] = val
      }
    } else if (section === 'readiness' || section === 'gate' || section === 'external' || section === 'converge') {
      const mm = raw.match(/^[ \t]+([A-Za-z_][\w-]*):[ \t]*(.*)$/)
      if (mm && out[section] && typeof out[section] === 'object') out[section][mm[1]] = scalar(mm[2])
    } else if (section === 'depends_on') {
      // block-list form (contract mandates inline; validate.mjs errors on it) - still capture the refs
      // so the deps column renders instead of silently vanishing.
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) out.depends_on.push(scalar(dash[1]))
    } else if (section === 'human_signoff' || section === 'open_decisions' || section === 'overrides' || section === 'hypotheses' || section === 'extends') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) {
        closeItem()
        const rest = dash[1].trim()
        // Accept both block form (`- id: od-1` then indented keys) and inline flow-mapping form
        // (`- { id: od-1, resolved: false }`) - both valid YAML; keeps the board in step with validate.mjs.
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

// Unresolved = every item that is not explicitly `resolved: true`.
const unresolved = (list) => list.filter((i) => String(i.resolved).trim() !== 'true').length
const sprintOf = (f) => (f.sprint && f.sprint !== 'null') ? f.sprint : ''

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
    if (!fm) fail(`features/${slug}/feature.md has no YAML frontmatter`)
    for (const req of ['id', 'slug', 'status']) {
      if (!fm[req]) fail(`features/${slug}/feature.md missing required field: ${req}`)
    }
    if (!fm.title) { warn(`features/${slug}/feature.md missing title - defaulting to slug`); fm.title = fm.slug }
    if (!['active', 'done', 'dropped'].includes(fm.status)) {
      fail(`features/${slug}/feature.md has invalid status: ${fm.status}`)
    }
    if (fm.status === 'dropped') continue // dropped/archived excluded from the board
    features.push({ ...fm, _manifest: text })
  }
  // Deterministic order by immutable id.
  features.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  return features
}

// Board mode drives grouping: `native` groups rows by sprint. Read it from `.spec-flow.md`.
function boardMode(rootDir) {
  const p = join(rootDir, '.spec-flow.md')
  if (!existsSync(p)) { warn(`${p} not found - board mode unknown; rendering a flat table`); return null }
  const fm = frontmatterBlock(readUtf8(p))
  if (fm == null) { warn(`${p} has no frontmatter - rendering a flat table`); return null }
  let inBoard = false
  for (const raw of fm.split(/\r?\n/)) {
    if (!raw.trim()) continue
    const indent = raw.length - raw.replace(/^[ \t]+/, '').length
    if (indent === 0) inBoard = /^board:[ \t]*(#.*)?$/.test(raw)
    else if (inBoard) {
      const mm = raw.match(/^[ \t]+mode:[ \t]*(.*)$/)
      if (mm) return scalar(mm[1])
    }
  }
  return null
}

const READINESS = ['research', 'design', 'spec', 'plan', 'tasks']
// `n/a` (legal only for `design`, §11.r) marks an artifact this feature does not need. Render it `-`,
// the glyph tasks.md already uses for an n/a task (§3, `[-]`). This is not only shorter: the literal
// `n/a` carries a slash, so a `/`-joined vector containing it reads as SIX segments, not five, and the
// positions stop lining up with the column header. An ABSENT key still renders `-` - a manifest with a
// hole in it, not a deliberate opt-out.
const readinessVec = (r) =>
  READINESS.map((k) => { const v = (r && r[k]) || '-'; return v === 'n/a' ? '-' : v }).join('/')

// Effective depth (contract §11.m): a v1 manifest without the field behaves exactly like mvp.
const depthOf = (f) => f.depth || 'mvp'

// depends_on rendering (contract §8): each dep id, suffixed `!` when that feature is not done.
const depsCell = (f, doneIds) =>
  f.depends_on.length ? f.depends_on.map((d) => (doneIds.has(d) ? d : `${d}!`)).join(', ') : ''
const depsBlocked = (f, doneIds) =>
  f.depends_on.some((d) => !doneIds.has(d)) &&
  (!f.readiness || !f.readiness.tasks || f.readiness.tasks === 'none')
const statusCell = (f, doneIds) =>
  (f.status === 'active' && depsBlocked(f, doneIds)) ? 'active (blocked)' : f.status

// converge stamp rendering (contract §8): - never run · clean · "N open (M contra)".
const driftCell = (f) => {
  if (!f.converge) return ''
  const open = Number(f.converge.open || 0)
  if (open === 0) return 'clean'
  const contra = Number(f.converge.contradicts || 0)
  return contra > 0 ? `${open} open (${contra} contra)` : `${open} open`
}

// hypotheses block rendering (contract §8): - none · N · "N (M refuted)". Populated by intake from a
// discovery hand-off; the post-contact loop flips a status to `refuted`, which surfaces here.
const hypCell = (f) => {
  const list = f.hypotheses || []
  if (list.length === 0) return ''
  const refuted = list.filter((h) => String(h.status).trim() === 'refuted').length
  return refuted > 0 ? `${list.length} (${refuted} refuted)` : String(list.length)
}

// extends rendering (contract §8, §11.t): the owning feature ids this feature additively extends. A
// documentation annotation - never gates - so we render the referenced ids plainly (no `!` marker).
const extendsCell = (f) => {
  const list = f.extends || []
  return list.length ? [...new Set(list.map((e) => e.feature).filter(Boolean))].join(', ') : ''
}

const COLS = [
  'id', 'slug', 'title', 'owner', 'status', 'depth', 'sprint', 'deps',
  'readiness (research/design/spec/plan/tasks)', 'analyze', 'drift', 'hyp', 'open_decisions', 'human_signoff', 'overrides', 'extends',
]
const HEADER = '| ' + COLS.join(' | ') + ' |'
const RULE = '| ' + COLS.map(() => '---').join(' | ') + ' |'

const cell = (s) => String(s === '' || s == null ? '-' : s).replace(/\|/g, '\\|')

// A feature's analyze gate goes STALE when either shared input (product-global.md / constitution.md)
// is edited after the gate was stamped: the live sha256:12 no longer matches the recorded hash, so
// `flow`/`analyze` treat `analyze` as not-pass and re-run it (contract §3 gate-staleness). The board
// must reflect that - a stored `pass` over changed shared inputs is not actually green.
function gateStale(f, livePG, liveConst) {
  const res = (f.gate && f.gate.analyze) || 'not-run'
  if (res !== 'pass' && res !== 'blocking' && res !== 'blocking-hard') return false // not-run / missing: nothing to invalidate
  const pg = (f.gate && f.gate.product_global_hash) || ''
  const con = (f.gate && f.gate.constitution_hash) || ''
  if (livePG != null && pg !== livePG) return true
  if (liveConst != null && con !== liveConst) return true
  return false
}
const analyzeCell = (f, livePG, liveConst) => {
  const res = (f.gate && f.gate.analyze) || 'not-run'
  return gateStale(f, livePG, liveConst) ? `${res} (stale)` : res
}
// In tracker mode the id links out to the seeding PBI (decorative); plain id otherwise.
const idCell = (f) => (f.external && typeof f.external === 'object' && f.external.url)
  ? `[${f.id}](${f.external.url})` : f.id

const row = (f) => '| ' + [
  idCell(f), f.slug, f.title, f.owner, f._status, depthOf(f), sprintOf(f), f._deps,
  readinessVec(f.readiness), f._analyze, driftCell(f), hypCell(f),
  unresolved(f.open_decisions), unresolved(f.human_signoff), unresolved(f.overrides), extendsCell(f),
].map(cell).join(' | ') + ' |'

// Sort sprint groups numerically when named `sprint-<n>`; the catch-all bucket sorts last.
function sprintCmp(a, b) {
  if (a === b) return 0
  if (a === '(no sprint)') return 1
  if (b === '(no sprint)') return -1
  const na = a.match(/(\d+)\s*$/), nb = b.match(/(\d+)\s*$/)
  if (na && nb) { const d = Number(na[1]) - Number(nb[1]); if (d) return d }
  return a < b ? -1 : 1
}

function build() {
  const features = loadFeatures(join(root, 'features'))
  const mode = boardMode(root)

  // Live shared-input hashes, to flag any feature whose analyze gate was stamped against an older
  // product-global.md / constitution.md (see analyzeCell). An absent file can't make a gate stale.
  const pgPath = join(root, 'product-global.md')
  const conPath = join(root, 'constitution.md')
  const livePG = existsSync(pgPath) ? fileHash(pgPath) : null
  const liveConst = existsSync(conPath) ? fileHash(conPath) : null
  const doneIds = new Set(features.filter((f) => f.status === 'done').map((f) => f.id))
  for (const f of features) {
    f._analyze = analyzeCell(f, livePG, liveConst)
    f._deps = depsCell(f, doneIds)
    f._status = statusCell(f, doneIds)
  }

  const cfgPath = join(root, '.spec-flow.md')
  const cfgHash = existsSync(cfgPath) ? hash(readUtf8(cfgPath)) : hash('')
  const provenance = [
    '<!-- provenance (generated - do not edit)',
    `configHash: ${cfgHash}`,
    `boardMode: ${mode == null ? 'unknown' : mode}`,
    'features:',
    ...features.map((f) => `  ${f.id} ${f.slug} ${hash(f._manifest)}`),
    '-->',
  ].join('\n')

  // Legend: one line per non-obvious column, in table-column order, so a reader can scan down to the
  // column they are stuck on. Each line says WHAT the column is before it says what the cell shows -
  // a reader who does not already know the pipeline cannot decode a bare value vocabulary. The
  // provenance stamp goes at the FOOT of the file (see below) to keep the hashes out of the way.
  const legend = [
    '**Legend.** One row per feature. Dropped features are not on the board.',
    '',
    '- `depth` - how far this feature is meant to be taken: `prototype`, `mvp`, or `ga`. A manifest with no `depth` field reads as `mvp`.',
    '- `deps` - the features this one waits on. A `!` marks a dependency that is not done. While it is unmet, implement stays gated and `status` reads `active (blocked)`.',
    '- `readiness` - how far each artifact has come, in column order: research / design / spec / plan / tasks. Each one reads `none`, `draft`, or `ready`. A `-` marks an artifact this feature does not need. A `-` marks a key the manifest does not set.',
    '- `analyze` - the gate that checks the spec, plan, and tasks against each other, the constitution, and product-global before anyone builds. Reads `not-run`, `pass`, `blocking` (findings to settle first), or `blocking-hard` (a constitution or security finding, which no override clears). `(stale)` means the constitution or product-global changed after the stamp, so the gate must run again.',
    '- `drift` - how far the built code has moved from the spec, as of the last converge run. `-` never run. `clean` no open gaps. `N open (M contra)` counts open gaps, of which M are code that contradicts the spec.',
    '- `hyp` - the discovery hypotheses this feature rests on, carried in by intake. `N` is the count. `N (M refuted)` means contact with users disproved M of them, so the ground under the feature moved.',
    '- `open_decisions` - the count of decisions raised and not yet settled. The gates read this count, so an open decision stops the flow instead of getting lost.',
    '- `human_signoff` - the count of checks that only a person can make, still outstanding. A model can never close one of these.',
    '- `overrides` - the count of gates a human waived to keep moving, where the debt is still unpaid. `promote` refuses to run while any one of them is open.',
    '- `extends` - the features whose contract or vocabulary this feature adds to. It records the link so the relationship is traceable. It never gates, and it resets no gate on the owning feature.',
  ].join('\n')

  const blocks = [
    '<!-- GENERATED by dashboard.mjs - DO NOT EDIT. Regenerate: node scripts/dashboard.mjs. Source of truth: spec/features/<slug>/feature.md. -->',
    '# Dashboard',
    legend,
  ]

  if (features.length === 0) {
    blocks.push('_No features yet._')
  } else if (mode === 'native') {
    const groups = new Map()
    for (const f of features) {
      const key = sprintOf(f) || '(no sprint)'
      if (!groups.has(key)) groups.set(key, [])
      groups.get(key).push(f)
    }
    for (const k of [...groups.keys()].sort(sprintCmp)) {
      blocks.push(`## ${k}`)
      blocks.push([HEADER, RULE, ...groups.get(k).map(row)].join('\n'))
    }
  } else {
    blocks.push([HEADER, RULE, ...features.map(row)].join('\n'))
  }
  blocks.push(provenance)
  return blocks.map(trimEnd).join('\n\n') + '\n'
}

const outPath = join(root, 'dashboard.md')
let rendered
try { rendered = build() } catch (e) { fail(e && e.message ? e.message : String(e)) }

if (check) {
  if (!existsSync(outPath)) { process.stderr.write(`dashboard: ${outPath} is missing - run dashboard to generate it\n`); process.exit(1) }
  const current = readUtf8(outPath)
  if (current === rendered) { process.stdout.write(`dashboard: ${outPath} is up to date\n`); process.exit(0) }
  process.stderr.write(`dashboard: ${outPath} is STALE - feature manifests have changed since it was generated\n`)
  process.exit(1)
} else {
  writeFileSync(outPath, rendered)
  process.stdout.write(`dashboard: wrote ${outPath}\n`)
  process.exit(0)
}

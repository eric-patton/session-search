#!/usr/bin/env node
// validate.mjs - lint the spec-flow workspace: feature manifests, spec.md structure, cross checks.
//
// Deterministic pre-filter (Implementation Contract §12): run it before the spec-reviewer agent
// spends tokens, as `analyze`'s first step, and in CI beside `assemble --check`. It reports; it
// never writes.
//
// Usage:
//   node scripts/validate.mjs [--root <specDir>] [--feature <slug>] [--json] [--as-ready]
//   node scripts/validate.mjs --bundle <dir> [--json]   discovery hand-off lint (contract §10)
//
// --bundle lints a discovery bundle (the typed hand-off `intake` ingests) instead of a workspace:
// required files, the bundle.md manifest shape, persona/hypothesis ids + status + coverage, and that
// every p-N/h-N cited in the content files resolves to a manifest id. Exits like the workspace lint.
//
// --as-ready lints every spec as if readiness.spec were `ready` - the pre-flight run specify/clarify
// perform before flipping readiness. Structural-completeness findings are ERRORS at ready and
// WARNINGS while draft (a draft is allowed to be incomplete; the claim of readiness is linted hard).
//
// Exit codes: 0 clean (warnings allowed) · 1 any error-severity finding · 2 hard error.
// Runtime: Node >=18, zero dependencies, cross-platform.

import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'

const args = process.argv.slice(2)
const json = args.includes('--json')
const asReady = args.includes('--as-ready')
const rootIdx = args.indexOf('--root')
const root = rootIdx !== -1 && args[rootIdx + 1] ? args[rootIdx + 1] : 'spec'
const featIdx = args.indexOf('--feature')
const onlyFeature = featIdx !== -1 && args[featIdx + 1] ? args[featIdx + 1] : null
const bundleIdx = args.indexOf('--bundle')
const bundlePath = bundleIdx !== -1 && args[bundleIdx + 1] ? args[bundleIdx + 1] : null

const fail = (m) => { process.stderr.write(`validate: error: ${m}\n`); process.exit(2) }
const readUtf8 = (p) => readFileSync(p, 'utf8')

// ---------------------------------------------------------------------------
// Frontmatter parsing - same minimal reader family as assemble.mjs/dashboard.mjs
// (scripts stay standalone-copyable, so each carries its own copy).
// ---------------------------------------------------------------------------

function frontmatterBlock(text) {
  const m = text.match(/^---\r?\n([\s\S]*?)\r?\n---/)
  return m ? m[1] : null
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

// Parse the §2 manifest shape: top-level scalars (incl. v2 depth/schema_version), the readiness and
// gate nested mappings, and the human_signoff/open_decisions/overrides item lists.
function parseManifest(text) {
  const fm = frontmatterBlock(text)
  if (fm == null) return null
  const out = {
    schema_version: '', id: '', slug: '', title: '', owner: '', status: '', sprint: '', depth: '',
    external: null, readiness: {}, gate: {}, converge: null, depends_on: [], depsBlockForm: false,
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
        // depends_on with an empty scalar => block-list form follows on indented `-` lines. The
        // contract mandates inline-list form, but we still capture the refs (so the dep lints and the
        // dashboard see them) and flag the form for the caller to error on.
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
      if (dash) { out.depends_on.push(scalar(dash[1])); out.depsBlockForm = true }
    } else if (section === 'human_signoff' || section === 'open_decisions' || section === 'overrides' || section === 'hypotheses' || section === 'extends') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) {
        closeItem()
        const rest = dash[1].trim()
        // Accept BOTH the canonical block form (`- id: od-1` then indented `key: value` lines) and the
        // inline flow-mapping form (`- { id: od-1, description: "…", resolved: false }`). Both are valid
        // YAML, and the schema comments in feature.template.md show the flow form - so a manifest that
        // uses it is linted on its merits, not spuriously rejected with a misdirecting "missing id".
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

// Effective depth (contract §11.m): a v1 manifest without the field behaves exactly like mvp.
const depthOf = (m) => m.depth || 'mvp'

// --- minimal glob walker (§12 AC→test coverage) -------------------------------------------------
// Supports `*`/`?` within a path segment and `**` across directories. Deliberately never descends
// into node_modules or .git. Zero-dep on purpose (scripts stay standalone-copyable).
function segmentRe(seg) {
  const esc = seg.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/\\\\]*').replace(/\?/g, '.')
  return new RegExp(`^${esc}$`)
}
function expandGlob(dir, segs, out) {
  if (!segs.length) return
  let entries
  try { entries = readdirSync(dir, { withFileTypes: true }) } catch { return }
  const [head, ...rest] = segs
  if (head === '**') {
    if (rest.length === 0) {
      for (const e of entries) {
        if (e.name === 'node_modules' || e.name === '.git') continue
        const p = join(dir, e.name)
        if (e.isFile()) out.add(p)
        else if (e.isDirectory()) expandGlob(p, segs, out)
      }
    } else {
      expandGlob(dir, rest, out) // `**` matches zero directories too
      for (const e of entries) {
        if (!e.isDirectory() || e.name === 'node_modules' || e.name === '.git') continue
        expandGlob(join(dir, e.name), segs, out)
      }
    }
    return
  }
  const re = segmentRe(head)
  for (const e of entries) {
    if (e.name === 'node_modules' || e.name === '.git') continue
    if (!re.test(e.name)) continue
    const p = join(dir, e.name)
    if (rest.length === 0) { if (e.isFile()) out.add(p) }
    else if (e.isDirectory()) expandGlob(p, rest, out)
  }
}
const isUnresolved = (i) => String(i.resolved).trim() !== 'true'

// Effective per-feature design requirement (§11.r): the manifest's OPTIONAL `requires_design` override
// (true|false) if set, else the workspace default from .spec-flow.md (ctx.requiresDesign). `null`/absent
// ⇒ inherit the workspace default - the exact pre-override behavior.
const requiresDesignFor = (m, ctx) => {
  const s = m.requires_design === undefined ? '' : String(m.requires_design).trim()
  if (s === 'true') return true
  if (s === 'false') return false
  return !!(ctx && ctx.requiresDesign === true)
}

// --- discovery-bundle parsing (hand-off contract §3) ------------------------------------------
// The bundle manifest uses INLINE-object item lists (`- { id: p-1, name: "…" }`) for personas and
// hypotheses - a different shape from the manifest's block-form items, so it gets its own reader.

// Split a comma-separated list at the TOP level only (commas inside quotes stay put), so a value like
// note: "wants rides, flaky" survives intact.
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

// Parse a single inline object `{ id: p-1, name: "x", note: "y, z" }` into a flat key→value map,
// reusing scalar() for quote/comment handling on each value.
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

// Parse bundle.md frontmatter: top-level scalars + the personas/hypotheses inline-object lists.
function parseBundleManifest(text) {
  const fm = frontmatterBlock(text)
  if (fm == null) return null
  const out = { personas: [], hypotheses: [] }
  let section = null
  for (const raw of fm.split(/\r?\n/)) {
    if (!raw.trim()) continue
    const indent = raw.length - raw.replace(/^[ \t]+/, '').length
    if (indent === 0) {
      const m = raw.match(/^([A-Za-z_][\w-]*):[ \t]*(.*)$/)
      if (!m) { section = null; continue }
      const val = scalar(m[2])
      if (val === '') { section = m[1] }       // a list/section follows on indented lines
      else { out[m[1]] = val; section = null }  // top-level scalar
    } else if (section === 'personas' || section === 'hypotheses') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) out[section].push(parseInlineObject(dash[1]))
    }
  }
  return out
}

// ---------------------------------------------------------------------------
// Lint machinery
// ---------------------------------------------------------------------------

const STATUS = ['active', 'done', 'dropped']
const READINESS = ['none', 'draft', 'ready']
const READINESS_DESIGN = ['none', 'draft', 'ready', 'n/a']
const GATE = ['not-run', 'pass', 'blocking', 'blocking-hard']
const GATE_STAMPED = ['pass', 'blocking', 'blocking-hard']
const DEPTH = ['prototype', 'mvp', 'ga']
const OVERRIDE_GATES = ['analyze', 'open-items', 'deps']
const GAP_CLASSES = ['missing', 'partial', 'contradicts', 'unrequested']
// Discovery-bundle enums (hand-off contract §3, §10).
const BUNDLE_REQUIRED_FILES = ['bundle.md', 'problem-statement.md', 'personas.md', 'hypotheses.md', 'constraints.md', 'sources.md']
const BUNDLE_SCHEMA_VERSIONS = ['1']
const VIABILITY = ['GO', 'NARROW', 'PIVOT']
const HYP_STATUS = ['unvalidated', 'confirmed', 'refuted', 'net-new']
// Marker detection (§12): case-insensitive, whitespace-tolerant on the internal gap, and applied only
// outside fenced/inline code so an illustrative `[NEEDS CLARIFICATION]` in docs is not a live marker.
const MARKER_RE = /\[needs\s+clarification\b/i
// Required spec.md sections (contract §12). Headings match on text: `## Why` matches a line whose
// text starts with the canonical name (trailing content on the line is allowed). CRLF-safe.
const SPEC_SECTIONS = ['Why', 'User stories', 'Behavior & scenarios', 'Acceptance criteria', 'Open questions']
const SPEC_SECTIONS_MVP = ['Edge cases & errors', 'Non-functional requirements']

function makeFinding(list, severity, code, file, line, message) {
  list.push({ severity, code, file, line, message })
}

// Split into lines once; heading index = first line whose `## `-stripped text starts with the name.
const splitLines = (text) => text.split(/\r?\n/)
function findHeading(lines, name) {
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].replace(/\r$/, '')
    if (t.startsWith('## ') && t.slice(3).trim().startsWith(name)) return i
  }
  return -1
}
// Body of the section starting at heading index: lines until the next `## ` heading (exclusive).
function sectionBody(lines, at) {
  const body = []
  for (let i = at + 1; i < lines.length; i++) {
    if (lines[i].replace(/\r$/, '').startsWith('## ')) break
    body.push(lines[i])
  }
  return body
}

// Scenario blocks: a `- **Scenario: …**` line plus following lines until the next scenario line or
// `## ` heading. A complete block carries Given, When, and Then lines.
function scenarioBlocks(lines) {
  const isScenario = (t) => /^\s*-\s*\*\*Scenario\b/i.test(t)
  const blocks = []
  let cur = null
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].replace(/\r$/, '')
    if (isScenario(t)) { if (cur) blocks.push(cur); cur = { line: i + 1, text: [] }; continue }
    if (t.startsWith('## ')) { if (cur) { blocks.push(cur); cur = null } continue }
    if (cur) cur.text.push(t)
  }
  if (cur) blocks.push(cur)
  for (const b of blocks) {
    const joined = b.text.join('\n')
    b.given = /(^|\n)\s*-?\s*Given\b/i.test(joined)
    b.when = /(^|\n)\s*-?\s*When\b/i.test(joined)
    b.then = /(^|\n)\s*-?\s*Then\b/i.test(joined)
    b.complete = b.given && b.when && b.then
  }
  return blocks
}

function lintItemList(findings, file, list, name, idPrefix) {
  const seen = new Set()
  const idRe = new RegExp(`^${idPrefix}-\\d+$`)
  for (const item of list) {
    const id = item.id || ''
    if (!idRe.test(id)) {
      makeFinding(findings, 'error', 'manifest-item-id', file, 1,
        `${name} item id "${id || '(missing)'}" must match ${idPrefix}-N`)
    } else if (seen.has(id)) {
      makeFinding(findings, 'error', 'manifest-item-id', file, 1, `${name} item id "${id}" is duplicated`)
    }
    seen.add(id)
    if (!item.description && name !== 'overrides') {
      makeFinding(findings, 'error', 'manifest-item-shape', file, 1, `${name} item "${id}" is missing a description`)
    }
    if (item.resolved !== undefined && !['true', 'false'].includes(String(item.resolved).trim())) {
      makeFinding(findings, 'error', 'manifest-item-shape', file, 1,
        `${name} item "${id}" resolved must be true or false (got "${item.resolved}")`)
    }
    if (name === 'overrides') {
      if (!OVERRIDE_GATES.includes(item.gate || '')) {
        makeFinding(findings, 'error', 'manifest-override-gate', file, 1,
          `override "${id}" gate must be one of ${OVERRIDE_GATES.join(' | ')} (got "${item.gate || ''}")`)
      }
      if (!item.by) {
        makeFinding(findings, 'error', 'manifest-override-shape', file, 1,
          `override "${id}" is missing "by" (the approving human)`)
      }
      if (!item.reason) {
        makeFinding(findings, 'error', 'manifest-override-shape', file, 1, `override "${id}" is missing a reason`)
      }
    }
  }
}

// --- converge ledger lint (§13) ---------------------------------------------------------------
// Parses run blocks/events/verdicts and checks the grammar + the manifest stamp.
function lintLedger(findings, slug, dir, m) {
  const ledgerPath = join(dir, 'converge.md')
  const ledgerFile = `features/${slug}/converge.md`
  const ledgerExists = existsSync(ledgerPath)

  if (m.converge && !ledgerExists) {
    makeFinding(findings, 'error', 'converge-no-ledger', `features/${slug}/feature.md`, 1,
      'manifest has a converge block but no converge.md ledger exists')
    return
  }
  if (!ledgerExists) return

  const lines = splitLines(readUtf8(ledgerPath))
  const gaps = new Map() // gap-NNN -> { state: 'open'|'closed', class }
  let lastRun = 0
  let lastVerdict = null
  let inRun = false
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].replace(/\r$/, '')
    const run = t.match(/^## run (\d+) [\u2014-] /)
    if (run) {
      const n = Number(run[1])
      if (n <= lastRun) {
        makeFinding(findings, 'error', 'ledger-run-order', ledgerFile, i + 1,
          `run ${n} does not increase (previous was ${lastRun})`)
      }
      if (lastRun === 0 && n !== 1) {
        makeFinding(findings, 'error', 'ledger-run-order', ledgerFile, i + 1, `first run must be 1 (got ${n})`)
      }
      lastRun = n
      inRun = true
      continue
    }
    if (!inRun) continue
    const ev = t.match(/^-\s+(opened|confirmed|reclassed|closed)\s+(gap-\d{3,})(?:\s+\[([a-z]+)\](?:\s*(?:->|→)\s*\[([a-z]+)\])?)?/)
    if (ev) {
      const [, verb, id, cls, newCls] = ev
      if (verb === 'opened') {
        if (gaps.has(id)) {
          makeFinding(findings, 'error', 'ledger-reused-gap', ledgerFile, i + 1,
            `${id} opened twice - gap ids are never reused`)
        }
        if (!GAP_CLASSES.includes(cls || '')) {
          makeFinding(findings, 'error', 'ledger-bad-class', ledgerFile, i + 1,
            `opened ${id} class "[${cls || ''}]" not in ${GAP_CLASSES.join(' | ')}`)
        }
        gaps.set(id, { state: 'open', class: cls })
      } else {
        const g = gaps.get(id)
        if (!g) {
          makeFinding(findings, 'error', 'ledger-unknown-gap', ledgerFile, i + 1,
            `${verb} ${id} - never opened`)
        } else if (g.state === 'closed') {
          makeFinding(findings, 'error', 'ledger-unknown-gap', ledgerFile, i + 1,
            `${verb} ${id} - already closed`)
        } else if (verb === 'closed') {
          g.state = 'closed'
        } else if (verb === 'confirmed') {
          // confirmed gap-NNN [class] re-states the gap's UNCHANGED class: the [class] bracket is
          // REQUIRED (like opened/reclassed), must be a valid class, and must equal the running class -
          // a class change is a reclassed, never smuggled through (or hidden by omitting) a confirmed.
          if (!GAP_CLASSES.includes(cls || '')) {
            makeFinding(findings, 'error', 'ledger-bad-class', ledgerFile, i + 1,
              `confirmed ${id} class "[${cls || ''}]" not in ${GAP_CLASSES.join(' | ')}`)
          } else if (g.class && cls !== g.class) {
            makeFinding(findings, 'error', 'ledger-reclass-mismatch', ledgerFile, i + 1,
              `confirmed ${id} says [${cls}] but its current class is [${g.class}] - a class change must be a reclassed, not a confirmed`)
          }
        } else if (verb === 'reclassed') {
          // reclassed gap-NNN [old]→[new]: old must match the running class, new must be valid AND
          // differ from old (a same-class outcome is a confirmed); the gap keeps its id and its class
          // updates to [new] (the verdict then counts it under [new]).
          if (cls && g.class && cls !== g.class) {
            makeFinding(findings, 'error', 'ledger-reclass-mismatch', ledgerFile, i + 1,
              `reclassed ${id} says [${cls}] but its current class is [${g.class}]`)
          }
          if (!GAP_CLASSES.includes(newCls || '')) {
            makeFinding(findings, 'error', 'ledger-bad-class', ledgerFile, i + 1,
              `reclassed ${id} new class "[${newCls || ''}]" not in ${GAP_CLASSES.join(' | ')}`)
          } else {
            if (newCls === g.class) {
              makeFinding(findings, 'error', 'ledger-reclass-noop', ledgerFile, i + 1,
                `reclassed ${id} [${g.class}]→[${newCls}] does not change the class - a same-class outcome is a confirmed, not a reclassed`)
            }
            g.class = newCls
          }
        }
      }
      continue
    }
    const v = t.match(/^verdict:\s*open\s+(\d+)\s*\(missing\s+(\d+),\s*partial\s+(\d+),\s*contradicts\s+(\d+),\s*unrequested\s+(\d+)\)/)
    if (v) {
      const openGaps = [...gaps.values()].filter((g) => g.state === 'open')
      const count = (cls) => openGaps.filter((g) => g.class === cls).length
      const [total, missing, partial, contradicts, unrequested] = v.slice(1).map(Number)
      if (total !== openGaps.length || missing !== count('missing') || partial !== count('partial') ||
          contradicts !== count('contradicts') || unrequested !== count('unrequested')) {
        makeFinding(findings, 'error', 'ledger-verdict-mismatch', ledgerFile, i + 1,
          `verdict says open ${total} (missing ${missing}, partial ${partial}, contradicts ${contradicts}, unrequested ${unrequested}) but the event history computes open ${openGaps.length} (missing ${count('missing')}, partial ${count('partial')}, contradicts ${count('contradicts')}, unrequested ${count('unrequested')})`)
      }
      lastVerdict = { total, contradicts }
    }
  }
  if (lastRun > 0 && !lastVerdict) {
    makeFinding(findings, 'error', 'ledger-verdict-mismatch', ledgerFile, 1, 'ledger has runs but no verdict line')
  }
  if (lastVerdict) {
    if (!m.converge) {
      makeFinding(findings, 'error', 'converge-stamp-mismatch', `features/${slug}/feature.md`, 1,
        'converge.md ledger exists but the manifest has no converge block stamp')
    } else {
      const stampedOpen = Number(m.converge.open || 0)
      const stampedContra = Number(m.converge.contradicts || 0)
      if (stampedOpen !== lastVerdict.total || stampedContra !== lastVerdict.contradicts) {
        makeFinding(findings, 'error', 'converge-stamp-mismatch', `features/${slug}/feature.md`, 1,
          `manifest converge stamp (open ${stampedOpen}, contradicts ${stampedContra}) does not match the ledger's last verdict (open ${lastVerdict.total}, contradicts ${lastVerdict.contradicts})`)
      }
    }
  }
}

function lintFeature(slug, dir, ctx) {
  const findings = []
  const manifestPath = join(dir, 'feature.md')
  const manifestFile = `features/${slug}/feature.md`
  const specPath = join(dir, 'spec.md')
  const specFile = `features/${slug}/spec.md`

  if (!existsSync(manifestPath)) {
    makeFinding(findings, 'error', 'manifest-missing', manifestFile, 1, 'feature folder has no feature.md')
    return findings
  }
  const text = readUtf8(manifestPath)
  const m = parseManifest(text)
  if (!m) {
    makeFinding(findings, 'error', 'manifest-no-frontmatter', manifestFile, 1, 'feature.md has no YAML frontmatter')
    return findings
  }

  // --- manifest lints ---
  for (const req of ['id', 'slug', 'title', 'status', 'owner']) {
    if (!m[req]) makeFinding(findings, 'error', 'manifest-missing-field', manifestFile, 1, `missing required field: ${req}`)
  }
  if (m.id && !/^feat-\d{3,}$/.test(m.id)) {
    makeFinding(findings, 'error', 'manifest-id-format', manifestFile, 1, `id "${m.id}" must match feat-NNN (>=3 digits)`)
  }
  if (m.id && ctx && ctx.dupIds.has(m.id)) {
    const others = ctx.dupIds.get(m.id).filter((s) => s !== slug)
    makeFinding(findings, 'error', 'manifest-id-duplicate', manifestFile, 1,
      `id "${m.id}" is not unique - also used by ${others.join(', ')} (immutable ids must be unique across features/)`)
  }
  if (m.slug) {
    if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(m.slug)) {
      makeFinding(findings, 'error', 'manifest-slug-format', manifestFile, 1, `slug "${m.slug}" is not kebab-case`)
    }
    if (m.slug !== slug) {
      makeFinding(findings, 'error', 'manifest-slug-folder', manifestFile, 1,
        `slug "${m.slug}" does not match its folder name "${slug}"`)
    }
  }
  if (m.status && !STATUS.includes(m.status)) {
    makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1, `status "${m.status}" not in ${STATUS.join(' | ')}`)
  }
  for (const k of ['research', 'design', 'spec', 'plan', 'tasks']) {
    const v = m.readiness[k]
    const legal = k === 'design' ? READINESS_DESIGN : READINESS
    if (v === undefined) {
      makeFinding(findings, 'error', 'manifest-missing-field', manifestFile, 1, `readiness.${k} is missing`)
    } else if (!legal.includes(v)) {
      makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1,
        `readiness.${k} "${v}" not in ${legal.join(' | ')}${k !== 'design' && v === 'n/a' ? ' (n/a is legal only for design)' : ''}`)
    }
  }
  const gateVal = m.gate.analyze
  if (gateVal === undefined) {
    makeFinding(findings, 'error', 'manifest-missing-field', manifestFile, 1, 'gate.analyze is missing')
  } else if (!GATE.includes(gateVal)) {
    makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1, `gate.analyze "${gateVal}" not in ${GATE.join(' | ')}`)
  }
  if (m.depth && !DEPTH.includes(m.depth)) {
    makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1, `depth "${m.depth}" not in ${DEPTH.join(' | ')}`)
  }
  if (m.requires_design !== undefined && !['true', 'false', 'null'].includes(String(m.requires_design).trim())) {
    makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1,
      `requires_design "${m.requires_design}" must be true, false, or null (the per-feature design-gate override, §11.r)`)
  }
  if (m.schema_version && !['1', '2'].includes(m.schema_version)) {
    makeFinding(findings, 'warn', 'manifest-schema-version', manifestFile, 1,
      `schema_version "${m.schema_version}" is not 1 or 2`)
  }
  lintItemList(findings, manifestFile, m.human_signoff, 'human_signoff', 'hs')
  lintItemList(findings, manifestFile, m.open_decisions, 'open_decisions', 'od')
  lintItemList(findings, manifestFile, m.overrides, 'overrides', 'ov')
  const openOverrides = m.overrides.filter(isUnresolved).length
  if (openOverrides > 0 && depthOf(m) !== 'prototype') {
    makeFinding(findings, 'warn', 'manifest-override-depth', manifestFile, 1,
      `${openOverrides} unresolved override(s) at ${depthOf(m)} depth - overrides buy passage only at prototype; promote refuses on them`)
  }
  if (/\{\{/.test(frontmatterBlock(text) || '')) {
    makeFinding(findings, 'warn', 'manifest-template-token', manifestFile, 1,
      'manifest frontmatter still contains {{template}} tokens')
  }

  // --- hypotheses block lints (§8/§10): discovery hypotheses intake carries onto the feature ---
  const hypIds = new Set()
  for (const h of m.hypotheses) {
    const id = h.id || ''
    if (!/^h-\d+$/.test(id)) {
      makeFinding(findings, 'error', 'manifest-hypothesis-id', manifestFile, 1, `hypothesis id "${id || '(missing)'}" must match h-N`)
    } else if (hypIds.has(id)) {
      makeFinding(findings, 'error', 'manifest-hypothesis-id', manifestFile, 1, `hypothesis id "${id}" is duplicated`)
    } else {
      hypIds.add(id)
      // Trace-back (§10): when intake preserved the bundle, a manifest hypothesis must be one the bundle
      // actually defined - a stray id means the block and the provenance have diverged.
      if (ctx && ctx.bundleHypIds && !ctx.bundleHypIds.has(id)) {
        makeFinding(findings, 'error', 'manifest-hypothesis-untraced', manifestFile, 1,
          `hypothesis "${id}" is not defined in the preserved discovery bundle (spec/.discovery/bundle.md)`)
      }
    }
    if (!h.statement) {
      makeFinding(findings, 'error', 'manifest-hypothesis-shape', manifestFile, 1, `hypothesis "${id || '(missing id)'}" is missing a statement`)
    }
    if (!HYP_STATUS.includes(h.status || '')) {
      makeFinding(findings, 'error', 'manifest-hypothesis-status', manifestFile, 1,
        `hypothesis "${id || '(missing id)'}" status "${h.status || ''}" not in ${HYP_STATUS.join(' | ')}`)
    }
  }

  // --- depends_on lints (§11.p) ---
  if (m.depsBlockForm) {
    makeFinding(findings, 'error', 'deps-block-form', manifestFile, 1,
      'depends_on must use inline-list form [feat-001, feat-002]; block-list ("- feat-001") form is not the contract form')
  }
  for (const dep of m.depends_on) {
    if (!/^feat-\d{3,}$/.test(dep)) {
      makeFinding(findings, 'error', 'deps-format', manifestFile, 1,
        `depends_on "${dep}" must be an immutable feat-NNN id (never a slug)`)
    } else if (dep === m.id) {
      makeFinding(findings, 'error', 'deps-self', manifestFile, 1, `depends_on contains the feature's own id`)
    } else if (ctx && !ctx.idStatus.has(dep)) {
      makeFinding(findings, 'error', 'deps-unresolved', manifestFile, 1,
        `depends_on "${dep}" does not resolve to a feature under features/ (archived or missing)`)
    } else if (ctx && ctx.idStatus.get(dep) === 'dropped') {
      makeFinding(findings, 'error', 'deps-dropped', manifestFile, 1,
        `depends_on "${dep}" resolves to a dropped feature - a dep on dropped work is an error (§11.p)`)
    }
  }
  if (ctx && ctx.cycles.has(m.id)) {
    makeFinding(findings, 'error', 'deps-cycle', manifestFile, 1,
      `depends_on cycle: ${ctx.cycles.get(m.id)}`)
  }
  if (m.converge) {
    for (const k of ['open', 'contradicts']) {
      if (m.converge[k] !== undefined && !/^\d+$/.test(String(m.converge[k]).trim())) {
        makeFinding(findings, 'error', 'manifest-enum', manifestFile, 1,
          `converge.${k} "${m.converge[k]}" must be a non-negative integer`)
      }
    }
  }
  lintLedger(findings, slug, dir, m)

  // Converge-before-done gate (§11.s): done at mvp+ requires a converge run (§6 drift-ok needs the
  // block PRESENT with contradicts = 0), so a done v2 feature with no block is a state flow cannot
  // produce: a hand-set done or pre-policy history. An ERROR. Prototype is exempt (drift is advisory
  // at prototype, §13), and so is a v1 manifest (converge is a schema-v2 capability, §13 - nagging
  // pre-converge features is anachronistic; the audience is v2 workspaces).
  if (m.status === 'done' && depthOf(m) !== 'prototype' && !m.converge && String(m.schema_version) === '2') {
    makeFinding(findings, 'error', 'converge-not-run', manifestFile, 1,
      `feature is done at ${depthOf(m)} depth but has no converge run; done at mvp+ requires a converge block with contradicts = 0 (§11.s): run /spec-flow:converge or re-open the feature`)
  }

  // --- extends lints (§11.t): cross-feature additive-extension annotations ---
  // Advisory (never gates the dispatch table), but referentially checked so a stale annotation can't
  // point at a feature that no longer exists.
  const extIds = new Set()
  for (const e of m.extends) {
    const id = e.id || ''
    if (!/^ext-\d+$/.test(id)) {
      makeFinding(findings, 'error', 'manifest-extends-id', manifestFile, 1, `extends item id "${id || '(missing)'}" must match ext-N`)
    } else if (extIds.has(id)) {
      makeFinding(findings, 'error', 'manifest-extends-id', manifestFile, 1, `extends item id "${id}" is duplicated`)
    } else {
      extIds.add(id)
    }
    if (!e.what) {
      makeFinding(findings, 'error', 'manifest-extends-shape', manifestFile, 1,
        `extends item "${id || '(missing id)'}" is missing "what" (the contract/vocabulary being extended)`)
    }
    const ref = e.feature || ''
    if (!/^feat-\d{3,}$/.test(ref)) {
      makeFinding(findings, 'error', 'manifest-extends-ref', manifestFile, 1,
        `extends item "${id}" feature "${ref || '(missing)'}" must be an immutable feat-NNN id (never a slug)`)
    } else if (ref === m.id) {
      makeFinding(findings, 'error', 'manifest-extends-ref', manifestFile, 1,
        `extends item "${id}" names the feature's own id - a feature cannot extend itself`)
    } else if (ctx && !ctx.idStatus.has(ref)) {
      makeFinding(findings, 'error', 'manifest-extends-ref', manifestFile, 1,
        `extends item "${id}" feature "${ref}" does not resolve to a feature under features/`)
    } else if (ctx && ctx.idStatus.get(ref) === 'dropped') {
      makeFinding(findings, 'error', 'manifest-extends-ref', manifestFile, 1,
        `extends item "${id}" feature "${ref}" resolves to a dropped feature`)
    }
  }

  // --- cross lints ---
  const specExists = existsSync(specPath)
  if (m.readiness.spec === 'ready' && !specExists) {
    makeFinding(findings, 'error', 'cross-ready-no-spec', manifestFile, 1, 'readiness.spec is ready but spec.md does not exist')
  }
  if (GATE_STAMPED.includes(gateVal) && (!m.gate.product_global_hash || !m.gate.constitution_hash)) {
    makeFinding(findings, 'error', 'cross-gate-no-hash', manifestFile, 1,
      `gate.analyze "${gateVal}" is stamped but a shared-input hash is empty`)
  }
  // Per-feature override (§11.r): n/a is legal iff the EFFECTIVE requirement is false - the feature's
  // own requires_design if set, else the workspace default. A backend-only feature in a design-required
  // workspace takes requires_design: false to make n/a legal without disabling the gate globally.
  if (requiresDesignFor(m, ctx) && m.readiness.design === 'n/a') {
    makeFinding(findings, 'error', 'cross-design-na', manifestFile, 1,
      'readiness.design is n/a but design is required for this feature (manifest requires_design, else .spec-flow.md requires_design: true) - n/a is legal only when design is not required (otherwise the design gate is silently skipped)')
  }

  // --- tasks lints (§12): the readiness claim over tasks.md is linted hard, like the spec lints ---
  const tasksPath = join(dir, 'tasks.md')
  const tasksFile = `features/${slug}/tasks.md`
  if (existsSync(tasksPath)) {
    const tlines = splitLines(readUtf8(tasksPath))
    const unfinished = [] // any [ ] / [~] / [H] task line
    let hGated = 0
    tlines.forEach((raw, i) => {
      const t = raw.replace(/\r$/, '')
      const g = t.match(/^\s*-\s*\[( |x|X|~|H|-)\]/)
      if (!g) return
      if (g[1] === 'H') hGated++
      if (g[1] === ' ' || g[1] === '~' || g[1] === 'H') unfinished.push({ line: i + 1, text: t.trim() })
    })
    if (m.readiness.tasks === 'ready' && unfinished.length > 0) {
      makeFinding(findings, 'error', 'tasks-ready-unfinished', tasksFile, unfinished[0].line,
        `readiness.tasks is ready but ${unfinished.length} task(s) are not done (first: "${unfinished[0].text.slice(0, 60)}"); every task must be [x] or [-] before tasks: ready`)
    }
    if (hGated > 0 && m.human_signoff.length === 0) {
      makeFinding(findings, 'warn', 'tasks-hgated-no-signoff', tasksFile, 1,
        `${hGated} [H] task(s) but the manifest human_signoff list is empty; every [H] task needs a matching human_signoff item (§11.b)`)
    }
  } else if (m.readiness.tasks === 'ready' && depthOf(m) !== 'prototype') {
    makeFinding(findings, 'warn', 'tasks-missing-ready', manifestFile, 1,
      `readiness.tasks is ready at ${depthOf(m)} depth but tasks.md does not exist (an express prototype may keep tasks in plan.md; mvp+ should carve a real tasks.md)`)
  }

  // --- spec lints ---
  if (specExists) {
    const spec = readUtf8(specPath)
    const lines = splitLines(spec)
    const eff = depthOf(m)
    // Severity is readiness-scaled (§12): completeness findings are errors when the spec CLAIMS
    // readiness (or under --as-ready, the specify/clarify pre-flight); warnings while it is a draft.
    const specReady = m.readiness.spec === 'ready' || asReady
    const sev = specReady ? 'error' : 'warn'

    for (const name of SPEC_SECTIONS) {
      if (findHeading(lines, name) === -1) {
        makeFinding(findings, sev, 'spec-missing-section', specFile, 1, `missing required section: ## ${name}`)
      }
    }
    if (eff !== 'prototype') {
      for (const name of SPEC_SECTIONS_MVP) {
        const at = findHeading(lines, name)
        if (at === -1) {
          makeFinding(findings, sev, 'spec-missing-section', specFile, 1,
            `missing required section at ${eff} depth: ## ${name}`)
        } else if (!sectionBody(lines, at).some((l) => l.trim() !== '')) {
          makeFinding(findings, sev, 'spec-empty-section', specFile, at + 1,
            `## ${name} is empty - required non-placeholder content at ${eff} depth`)
        }
      }
    }

    const blocks = scenarioBlocks(lines)
    if (!blocks.some((b) => b.complete)) {
      makeFinding(findings, sev, 'spec-no-scenario', specFile, 1,
        'no complete Given/When/Then scenario found (need at least one **Scenario:** block with all three)')
    } else if (eff !== 'prototype') {
      for (const b of blocks) {
        if (!b.complete) {
          makeFinding(findings, sev, 'spec-incomplete-scenario', specFile, b.line,
            `scenario block at ${eff} depth is missing ${['Given', 'When', 'Then'].filter((k) => !b[k.toLowerCase()]).join('/')}`)
        }
      }
    }

    lines.forEach((l, i) => {
      if (l.includes('{{')) {
        makeFinding(findings, sev, 'spec-template-token', specFile, i + 1, 'unfilled {{template}} token')
      }
    })

    let markers = 0
    let inFence = false
    lines.forEach((l, i) => {
      const t = l.replace(/\r$/, '')
      if (/^\s*(```|~~~)/.test(t)) { inFence = !inFence; return }  // toggle on code-fence delimiters
      if (inFence) return
      const stripped = t.replace(/`[^`]*`/g, '')  // drop inline-code spans (illustrative markers)
      const re = new RegExp(MARKER_RE.source, 'ig')
      let hit = false
      while (re.exec(stripped) !== null) { markers++; hit = true }
      if (hit && specReady) {
        makeFinding(findings, 'error', 'spec-marker-ready', specFile, i + 1,
          'readiness.spec is ready but spec.md still carries a [NEEDS CLARIFICATION] marker')
      }
    })
    if (markers > 0 && !specReady) {
      makeFinding(findings, 'warn', 'spec-markers', specFile, 1,
        `${markers} clarification marker(s) - drain via /spec-flow:clarify before readiness.spec: ready`)
    }

    // Acceptance criteria carry stable AC-N ids (§3) - the anchor converge/change/tasks cite. Each
    // column-0 bullet under `## Acceptance criteria` needs a unique `AC-N:` id. Readiness-scaled.
    const acAt = findHeading(lines, 'Acceptance criteria')
    const acList = [] // { id, manual, line } - feeds the AC→test coverage lint below
    if (acAt !== -1) {
      const acIds = new Set()
      sectionBody(lines, acAt).forEach((raw, j) => {
        const l = raw.replace(/\r$/, '')
        const bullet = l.match(/^-\s+(?:\[[ xX~-]\]\s+)?(.+)$/) // column-0 bullet, optional checkbox
        if (!bullet || bullet[1].trim() === '') return
        const idm = bullet[1].match(/^(AC-\d+)\b/)
        if (!idm) {
          makeFinding(findings, sev, 'spec-ac-unnumbered', specFile, acAt + 2 + j,
            `acceptance criterion needs an AC-N id: "${bullet[1].slice(0, 50)}"`)
        } else if (acIds.has(idm[1])) {
          makeFinding(findings, sev, 'spec-ac-dup-id', specFile, acAt + 2 + j,
            `duplicate acceptance-criterion id ${idm[1]}`)
        } else {
          acIds.add(idm[1])
          acList.push({ id: idm[1], manual: /\(manual\)\s*$/i.test(bullet[1]), line: acAt + 2 + j })
        }
      })
    }

    // AC→test coverage (§3 trace token, §12): at mvp+ with tasks claimed ready, every non-(manual) AC
    // must be cited by its QUALIFIED token feat-NNN/AC-N in a file the workspace tests: globs match.
    // Warning at mvp, error at ga; prototype exempt; off (with a nudge) when tests: is unconfigured.
    if (eff !== 'prototype' && m.readiness.tasks === 'ready' && m.id && ctx) {
      if (ctx.testsConfigured) {
        for (const ac of acList) {
          if (ac.manual) continue
          const token = `${m.id}/${ac.id}`
          if (!ctx.testCorpus.includes(token)) {
            makeFinding(findings, eff === 'ga' ? 'error' : 'warn', 'ac-untested', specFile, ac.line,
              `${ac.id} has no citing test: no configured test file contains "${token}" (§3 trace token; suffix the criterion "(manual)" if it is verified by recorded human observation)`)
          }
        }
      } else if (acList.length > 0) {
        makeFinding(findings, 'warn', 'tests-unconfigured', manifestFile, 1,
          `readiness.tasks is ready at ${eff} depth but .spec-flow.md declares no tests: globs, so AC->test coverage cannot be traced (§12)`)
      }
    }

    // A `> validates: h-N` prose cite that names an id the manifest hypotheses block does not carry =
    // warning (§10): the spec claims to validate a hypothesis the feature was never assigned.
    lines.forEach((raw, i) => {
      const re = /validates:\s*(h-\d+)/ig
      let hit
      while ((hit = re.exec(raw.replace(/\r$/, ''))) !== null) {
        if (!hypIds.has(hit[1])) {
          makeFinding(findings, 'warn', 'spec-hypothesis-uncited', specFile, i + 1,
            `spec cites ${hit[1]} but the manifest hypotheses block has no such id`)
        }
      }
    })
  }

  return findings
}

// --- discovery-bundle lint (hand-off contract §10) --------------------------------------------
function lintBundle(dir) {
  const findings = []

  // Required files present.
  for (const f of BUNDLE_REQUIRED_FILES) {
    if (!existsSync(join(dir, f))) {
      makeFinding(findings, 'error', 'bundle-missing-file', f, 1, `required bundle file is missing: ${f}`)
    }
  }
  const manifestPath = join(dir, 'bundle.md')
  if (!existsSync(manifestPath)) return findings   // nothing more to check without the manifest
  const m = parseBundleManifest(readUtf8(manifestPath))
  if (!m) {
    makeFinding(findings, 'error', 'bundle-no-frontmatter', 'bundle.md', 1, 'bundle.md has no YAML frontmatter')
    return findings
  }

  // Manifest scalars.
  if (m.kind !== 'discovery-bundle') {
    makeFinding(findings, 'error', 'bundle-kind', 'bundle.md', 1, `kind "${m.kind || ''}" must be discovery-bundle`)
  }
  for (const req of ['schema_version', 'product', 'generated', 'discovery_run', 'viability', 'suggested_depth']) {
    if (!m[req]) makeFinding(findings, 'error', 'bundle-missing-field', 'bundle.md', 1, `missing required field: ${req}`)
  }
  if (m.schema_version && !BUNDLE_SCHEMA_VERSIONS.includes(m.schema_version)) {
    makeFinding(findings, 'error', 'bundle-schema-version', 'bundle.md', 1,
      `schema_version "${m.schema_version}" is unknown (known: ${BUNDLE_SCHEMA_VERSIONS.join(', ')}) - refuse rather than guess`)
  }
  if (m.viability && !VIABILITY.includes(m.viability)) {
    makeFinding(findings, 'error', 'bundle-enum', 'bundle.md', 1, `viability "${m.viability}" not in ${VIABILITY.join(' | ')}`)
  }
  if (m.suggested_depth && !DEPTH.includes(m.suggested_depth)) {
    makeFinding(findings, 'error', 'bundle-enum', 'bundle.md', 1, `suggested_depth "${m.suggested_depth}" not in ${DEPTH.join(' | ')}`)
  }
  if (m.generated && !/^\d{4}-\d{2}-\d{2}$/.test(m.generated)) {
    makeFinding(findings, 'warn', 'bundle-date', 'bundle.md', 1, `generated "${m.generated}" is not an ISO date (YYYY-MM-DD)`)
  }

  // Personas + hypotheses: well-formed unique ids, required shape, valid status/coverage.
  const pIds = new Set()
  if (m.personas.length === 0) makeFinding(findings, 'error', 'bundle-empty', 'bundle.md', 1, 'no personas in the manifest')
  for (const p of m.personas) {
    const id = p.id || ''
    if (!/^p-\d+$/.test(id)) makeFinding(findings, 'error', 'bundle-persona-id', 'bundle.md', 1, `persona id "${id || '(missing)'}" must match p-N`)
    else if (pIds.has(id)) makeFinding(findings, 'error', 'bundle-persona-id', 'bundle.md', 1, `persona id "${id}" is duplicated`)
    else pIds.add(id)
    if (!p.name) makeFinding(findings, 'error', 'bundle-persona-shape', 'bundle.md', 1, `persona "${id || '(missing id)'}" is missing a name`)
  }
  const hIds = new Set()
  if (m.hypotheses.length === 0) makeFinding(findings, 'error', 'bundle-empty', 'bundle.md', 1, 'no hypotheses in the manifest')
  for (const h of m.hypotheses) {
    const id = h.id || ''
    if (!/^h-\d+$/.test(id)) makeFinding(findings, 'error', 'bundle-hypothesis-id', 'bundle.md', 1, `hypothesis id "${id || '(missing)'}" must match h-N`)
    else if (hIds.has(id)) makeFinding(findings, 'error', 'bundle-hypothesis-id', 'bundle.md', 1, `hypothesis id "${id}" is duplicated`)
    else hIds.add(id)
    if (!h.statement) makeFinding(findings, 'error', 'bundle-hypothesis-shape', 'bundle.md', 1, `hypothesis "${id || '(missing id)'}" is missing a statement`)
    if (!HYP_STATUS.includes(h.status || '')) {
      makeFinding(findings, 'error', 'bundle-hypothesis-status', 'bundle.md', 1,
        `hypothesis "${id || '(missing id)'}" status "${h.status || ''}" not in ${HYP_STATUS.join(' | ')}`)
    }
    if (h.coverage !== undefined && h.coverage !== '') {
      const c = Number(h.coverage)
      if (!Number.isFinite(c) || c < 0 || c > 1) {
        makeFinding(findings, 'error', 'bundle-hypothesis-coverage', 'bundle.md', 1,
          `hypothesis "${id || '(missing id)'}" coverage "${h.coverage}" must be a number in [0,1]`)
      }
    }
  }

  // Dangling cites: every p-N/h-N named in a content file must be defined in the manifest.
  const reported = new Set()
  const contentFiles = [...BUNDLE_REQUIRED_FILES.filter((f) => f !== 'bundle.md'), 'pitch.md', 'validation.md']
  for (const f of contentFiles) {
    const p = join(dir, f)
    if (!existsSync(p)) continue
    splitLines(readUtf8(p)).forEach((raw, i) => {
      const t = raw.replace(/\r$/, '')
      const re = /\b([ph]-\d+)\b/g
      let hit
      while ((hit = re.exec(t)) !== null) {
        const id = hit[1]
        const known = id[0] === 'p' ? pIds.has(id) : hIds.has(id)
        if (!known && !reported.has(id)) {
          reported.add(id)
          makeFinding(findings, 'error', 'bundle-dangling-cite', f, i + 1, `references "${id}" but it is not defined in bundle.md`)
        }
      }
    })
  }

  // sources.md must carry content when the manifest claims sources; a count mismatch is a warning.
  if (Number(m.sources_count || 0) > 0) {
    const sp = join(dir, 'sources.md')
    if (existsSync(sp)) {
      const body = readUtf8(sp).trim()
      if (body === '') {
        makeFinding(findings, 'error', 'bundle-sources-empty', 'sources.md', 1, `sources_count is ${m.sources_count} but sources.md is empty`)
      } else {
        const n = splitLines(body).filter((l) => /^\d+\.\s/.test(l.replace(/\r$/, ''))).length
        if (n !== Number(m.sources_count)) {
          makeFinding(findings, 'warn', 'bundle-sources-count', 'sources.md', 1,
            `sources_count is ${m.sources_count} but sources.md lists ${n} numbered entries`)
        }
      }
    }
  }

  // Numbered source-citation integrity (warn): a content file that back-cites a source as [n] must point
  // to a numbered entry that exists in sources.md. Optional convention - a bundle that links evidence only
  // via sources.md's forward "-> h-N" annotations carries no [n] tokens and is unaffected.
  {
    const srcPath = join(dir, 'sources.md')
    if (existsSync(srcPath)) {
      const srcNums = new Set()
      for (const raw of splitLines(readUtf8(srcPath))) {
        const mm = raw.replace(/\r$/, '').match(/^(\d+)\.\s/)
        if (mm) srcNums.add(mm[1])
      }
      if (srcNums.size > 0) {
        const seenBad = new Set()
        for (const f of ['problem-statement.md', 'personas.md', 'hypotheses.md', 'constraints.md', 'pitch.md', 'validation.md']) {
          const p = join(dir, f)
          if (!existsSync(p)) continue
          splitLines(readUtf8(p)).forEach((raw, i) => {
            const t = raw.replace(/\r$/, '')
            const re = /\[(\d+)\]/g
            let hit
            while ((hit = re.exec(t)) !== null) {
              const n = hit[1]
              if (!srcNums.has(n) && !seenBad.has(f + '#' + n)) {
                seenBad.add(f + '#' + n)
                makeFinding(findings, 'warn', 'bundle-cite-unresolved', f, i + 1,
                  `cites source [${n}] but sources.md has no numbered entry ${n}`)
              }
            }
          })
        }
      }
    }
  }

  return findings
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

// Bundle mode (contract §10): lint a discovery bundle and exit, before any workspace logic.
if (bundlePath) {
  if (!existsSync(bundlePath)) fail(`bundle "${bundlePath}" does not exist`)
  if (!statSync(bundlePath).isDirectory()) fail(`bundle "${bundlePath}" is not a directory`)
  let findings
  try { findings = lintBundle(bundlePath) } catch (e) { fail(e && e.message ? e.message : String(e)) }
  const berrors = findings.filter((f) => f.severity === 'error').length
  const bwarnings = findings.filter((f) => f.severity === 'warn').length
  if (json) {
    process.stdout.write(JSON.stringify({ bundle: bundlePath, findings, errors: berrors, warnings: bwarnings }, null, 2) + '\n')
  } else {
    for (const f of findings) {
      process.stdout.write(`validate: ${f.severity}: bundle: ${f.code}: ${f.message} (${f.file}:${f.line})\n`)
    }
    process.stdout.write(`validate: ${berrors} error(s), ${bwarnings} warning(s) in discovery bundle\n`)
  }
  process.exit(berrors > 0 ? 1 : 0)
}

if (!existsSync(root)) fail(`root "${root}" does not exist`)
const featuresDir = join(root, 'features')

const allSlugs = existsSync(featuresDir)
  ? readdirSync(featuresDir).filter((d) => {
      try { return statSync(join(featuresDir, d)).isDirectory() } catch { return false }
    }).sort()
  : []

let slugs = allSlugs
if (onlyFeature) {
  if (!existsSync(join(featuresDir, onlyFeature))) fail(`feature "${onlyFeature}" not found under ${featuresDir}`)
  slugs = [onlyFeature]
}

// Cross-feature context: EVERY parseable manifest feeds the dep graph, even under --feature.
const idStatus = new Map() // feat-id -> status
const idDeps = new Map()   // feat-id -> depends_on
const idSlugs = new Map()  // feat-id -> [slugs] - to detect duplicate immutable ids across features/
for (const s of allSlugs) {
  const p = join(featuresDir, s, 'feature.md')
  if (!existsSync(p)) continue
  let m = null
  try { m = parseManifest(readUtf8(p)) } catch { /* reported by that feature's own lint */ }
  if (m && m.id) {
    idStatus.set(m.id, m.status); idDeps.set(m.id, m.depends_on)
    idSlugs.set(m.id, [...(idSlugs.get(m.id) || []), s])
  }
}
const dupIds = new Map() // feat-id -> [slugs] for any id used by more than one feature
for (const [id, ss] of idSlugs) if (ss.length > 1) dupIds.set(id, ss)

// Workspace config (.spec-flow.md): requires_design decides whether readiness.design: n/a is legal (§3);
// tests: names the product's test globs (§5) and enables the AC→test coverage lint (§12).
let requiresDesign = null
let testsGlobs = null
{
  const cfgPath = join(root, '.spec-flow.md')
  if (existsSync(cfgPath)) {
    const fm = frontmatterBlock(readUtf8(cfgPath)) || ''
    const mm = fm.match(/^requires_design:[ \t]*(.*)$/m)
    if (mm) requiresDesign = scalar(mm[1]) === 'true'
    const tm = fm.match(/^tests:[ \t]*(.*)$/m)
    if (tm) {
      const raw = tm[1].trim()
      if (raw.startsWith('[')) {
        const list = raw.replace(/^\[|\]$/g, '').split(',').map((s) => scalar(s)).filter(Boolean)
        if (list.length) testsGlobs = list
      } else {
        const v = scalar(raw)
        if (v && v !== 'null') testsGlobs = [v]
      }
    }
  }
}
// The test corpus: every file the tests globs match (relative to the PARENT of the spec root),
// concatenated once for §3 trace-token search. null = tests not configured (the lint stays off).
let testCorpus = null
if (testsGlobs) {
  const projectRoot = join(root, '..')
  const files = new Set()
  for (const g of testsGlobs) {
    expandGlob(projectRoot, g.replace(/\\/g, '/').split('/').filter(Boolean), files)
  }
  const parts = []
  for (const f of [...files].sort()) {
    try { parts.push(readUtf8(f)) } catch { /* an unreadable test file just drops out of the corpus */ }
  }
  testCorpus = parts.join('\n')
}
// Preserved discovery bundle (spec/.discovery/bundle.md), if intake ran - the source of truth for which
// hypothesis ids a feature manifest may carry (§10 trace-back). null when no bundle was preserved.
let bundleHypIds = null
{
  const bp = join(root, '.discovery', 'bundle.md')
  if (existsSync(bp)) {
    let bm = null
    try { bm = parseBundleManifest(readUtf8(bp)) } catch { /* a malformed preserved bundle just disables the trace */ }
    if (bm) bundleHypIds = new Set(bm.hypotheses.map((h) => h.id).filter(Boolean))
  }
}
// Cycle detection: colored DFS over the dep graph (§12). Every id on a cycle gets the path.
const cycles = new Map() // feat-id -> cycle path string
{
  const state = new Map() // 0 unvisited / 1 on-stack / 2 done
  const stack = []
  const visit = (id) => {
    state.set(id, 1); stack.push(id)
    for (const dep of idDeps.get(id) || []) {
      if (!idDeps.has(dep)) continue
      const s = state.get(dep) || 0
      if (s === 0) visit(dep)
      else if (s === 1) {
        const path = [...stack.slice(stack.indexOf(dep)), dep]
        for (const n of path) if (!cycles.has(n)) cycles.set(n, path.join(' -> '))
      }
    }
    stack.pop(); state.set(id, 2)
  }
  for (const id of idDeps.keys()) if ((state.get(id) || 0) === 0) visit(id)
}
const ctx = { idStatus, cycles, dupIds, requiresDesign, bundleHypIds, testsConfigured: testsGlobs != null, testCorpus: testCorpus || '' }

let results
try {
  results = slugs.map((slug) => ({ slug, findings: lintFeature(slug, join(featuresDir, slug), ctx) }))
} catch (e) {
  fail(e && e.message ? e.message : String(e))
}

const all = results.flatMap((r) => r.findings)
const errors = all.filter((f) => f.severity === 'error').length
const warnings = all.filter((f) => f.severity === 'warn').length

if (json) {
  process.stdout.write(JSON.stringify({ features: results, errors, warnings }, null, 2) + '\n')
} else {
  for (const r of results) {
    for (const f of r.findings) {
      process.stdout.write(`validate: ${f.severity}: ${r.slug}: ${f.code}: ${f.message} (${f.file}:${f.line})\n`)
    }
  }
  process.stdout.write(`validate: ${errors} error(s), ${warnings} warning(s) across ${results.length} feature(s)\n`)
}
process.exit(errors > 0 ? 1 : 0)

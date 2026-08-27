#!/usr/bin/env node
// status.mjs: render the plain-language state of one feature (the reporting contract's status card)
// or a one-line portfolio health summary, straight from the canonical manifests.
//
// feature.md manifests are CANONICAL. This script is a read-only VIEW, like dashboard.md, aimed at a
// human instead of a table: it says where a feature is on the road in plain words, so skills reuse its
// wording instead of improvising state (contract §11.w). It writes nothing.
//
// Usage:
//   node scripts/status.mjs [--root <specDir>]            # portfolio summary (all features)
//   node scripts/status.mjs <slug-or-id> [--root ...]     # one feature's status card state lines
//
// Exit codes: 0 ok · 2 hard error (missing workspace, unknown feature, unparseable manifest).
// Runtime: Node >=18, zero dependencies, cross-platform. Carries its own parser copy, like every
// spec-flow script (scripts stay standalone-copyable).

import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { createHash } from 'node:crypto'

const args = process.argv.slice(2)
const rootIdx = args.indexOf('--root')
const root = rootIdx !== -1 && args[rootIdx + 1] ? args[rootIdx + 1] : 'spec'
const target = args.filter((a, i) => a !== '--root' && i !== rootIdx + 1)[0] || null

const fail = (m) => { process.stderr.write(`status: error: ${m}\n`); process.exit(2) }
const out = (m) => process.stdout.write(m + '\n')

const fileHash = (p) => 'sha256:' + createHash('sha256').update(readFileSync(p)).digest('hex').slice(0, 12)
const readUtf8 = (p) => readFileSync(p, 'utf8')

// ---- manifest parsing (sibling of dashboard.mjs's copy; §2/§11.f subset) ----

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
    id: '', slug: '', title: '', owner: '', status: '', depth: '', requires_design: '',
    readiness: {}, gate: {}, converge: null, depends_on: [],
    human_signoff: [], open_decisions: [], overrides: [],
  }
  let section = null
  let listRef = null
  let item = null
  let folded = null
  const closeItem = () => { if (item && listRef) { listRef.push(item); item = null; folded = null } }

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
        else if (key === 'converge') out.converge = {}
      } else if (val === '[]') {
        // empty inline list, keep the seeded default
      } else if (key === 'depends_on' && val.startsWith('[')) {
        out.depends_on = val.replace(/^\[|\]$/g, '').split(',').map((s) => s.trim()).filter(Boolean)
      } else {
        out[key] = val
      }
    } else if (section === 'readiness' || section === 'gate' || section === 'converge') {
      const mm = raw.match(/^[ \t]+([A-Za-z_][\w-]*):[ \t]*(.*)$/)
      if (mm && out[section] && typeof out[section] === 'object') out[section][mm[1]] = scalar(mm[2])
    } else if (section === 'human_signoff' || section === 'open_decisions' || section === 'overrides') {
      const dash = raw.match(/^[ \t]+-[ \t]*(.*)$/)
      if (dash) {
        closeItem()
        folded = null
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
        if (kv && !(folded && indent > folded.indent)) {
          // A folded scalar (`description: >-`) collects the deeper-indented lines that follow.
          const v = kv[2].trim()
          if (v === '>-' || v === '>') folded = { key: kv[1], indent, parts: [] }
          else { folded = null; item[kv[1]] = scalar(kv[2]) }
        } else if (folded && indent > folded.indent) {
          folded.parts.push(raw.trim())
          item[folded.key] = folded.parts.join(' ')
        }
      }
    }
  }
  closeItem()
  return out
}

// ---- workspace loading ----

function loadFeatures(featuresDir) {
  if (!existsSync(featuresDir)) fail(`${featuresDir} not found; is --root pointing at the spec workspace?`)
  const features = []
  for (const slug of readdirSync(featuresDir)) {
    const dir = join(featuresDir, slug)
    try { if (!statSync(dir).isDirectory()) continue } catch { continue }
    const manifestPath = join(dir, 'feature.md')
    if (!existsSync(manifestPath)) continue
    const fm = parseManifest(readUtf8(manifestPath))
    if (!fm) fail(`features/${slug}/feature.md has no YAML frontmatter`)
    if (fm.status === 'dropped') continue
    fm._dir = dir
    features.push(fm)
  }
  features.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  return features
}

const unresolvedItems = (list) => list.filter((i) => String(i.resolved).trim() !== 'true')
const depthOf = (f) => f.depth || 'mvp'

// [NEEDS CLARIFICATION] marker count in spec.md (0 when the file is absent).
function markerCount(featureDir) {
  const p = join(featureDir, 'spec.md')
  if (!existsSync(p)) return 0
  const m = readUtf8(p).match(/\[\s*NEEDS\s+CLARIFICATION/gi)
  return m ? m.length : 0
}

// Task glyph tally from tasks.md: done [x], waiting-on-a-human [H], everything else open.
function taskCounts(featureDir) {
  const p = join(featureDir, 'tasks.md')
  if (!existsSync(p)) return null
  let total = 0, done = 0, human = 0
  for (const line of readUtf8(p).split(/\r?\n/)) {
    const m = line.match(/^\s*[-*]\s*\[( |x|X|~|H)\]/)
    if (!m) continue
    total += 1
    if (m[1] === 'x' || m[1] === 'X') done += 1
    if (m[1] === 'H') human += 1
  }
  return total === 0 ? null : { total, done, human }
}

// A stamped verdict goes stale when either shared input was edited after the gate ran (contract §3).
function gateState(f, livePG, liveConst) {
  const verdict = (f.gate && f.gate.analyze) || 'not-run'
  if (verdict === 'not-run' || verdict === '') return { verdict: 'not-run', stale: false }
  const stale =
    (livePG != null && (f.gate.product_global_hash || '') !== livePG) ||
    (liveConst != null && (f.gate.constitution_hash || '') !== liveConst)
  return { verdict, stale }
}

// ---- plain-language renderings ----

const READY_WORDS = { none: 'not started', draft: 'in progress', ready: 'done', 'n/a': 'not needed', '': 'not started' }

function roadLine(f) {
  const r = f.readiness || {}
  const seg = []
  seg.push(`research ${READY_WORDS[r.research] || r.research}`)
  if (r.design && r.design !== 'n/a') seg.push(`design ${READY_WORDS[r.design] || r.design}`)
  else if (r.design === 'n/a') seg.push('design not needed')
  const markers = markerCount(f._dir)
  if (r.spec === 'ready') seg.push('spec ready')
  else if (r.spec === 'draft') seg.push(markers > 0 ? `spec drafted, ${markers} open question${markers === 1 ? '' : 's'}` : 'spec drafted')
  else seg.push('spec not written')
  seg.push(`plan ${r.plan === 'ready' ? 'ready' : (READY_WORDS[r.plan] || 'not started')}`)
  const t = taskCounts(f._dir)
  if (r.tasks === 'ready') seg.push(t ? `build complete (${t.total} tasks)` : 'build complete')
  else if (t) seg.push(`build ${t.done}/${t.total} tasks${t.human > 0 ? ` (${t.human} waiting on a human)` : ''}`)
  else seg.push('build not started')
  if (f.converge) {
    const open = Number(f.converge.open || 0)
    const contra = Number(f.converge.contradicts || 0)
    const when = f.converge.last_run ? ` (last run ${f.converge.last_run})` : ''
    seg.push(open === 0 ? `code audit clean${when}` : `code audit: ${open} gap${open === 1 ? '' : 's'} open, ${contra} contradiction${contra === 1 ? '' : 's'}${when}`)
  } else {
    seg.push('code not yet audited against the spec')
  }
  return seg.join(' · ')
}

function gateLine(f, livePG, liveConst) {
  const g = gateState(f, livePG, liveConst)
  const rerun = ', and needs a re-run (the shared project rules changed after it ran)'
  if (g.verdict === 'not-run') return 'pre-build check not yet run'
  if (g.verdict === 'pass') return g.stale ? `pre-build check passed${rerun}` : 'pre-build check passed'
  if (g.verdict === 'blocking-hard') return `pre-build check found hard blockers (never overridable)${g.stale ? rerun : ''}`
  return `pre-build check found blockers${g.stale ? rerun : ''}`
}

const firstSentence = (s, max = 90) => {
  const t = String(s || '').trim().replace(/\s+/g, ' ')
  const cut = t.search(/\.\s|\.$/) !== -1 ? t.slice(0, t.search(/\.\s|\.$/)) : t
  return cut.length > max ? cut.slice(0, max - 1).trimEnd() + '…' : cut
}

function waitingLine(f) {
  const items = [...unresolvedItems(f.open_decisions), ...unresolvedItems(f.human_signoff)]
  if (items.length === 0) return 'nothing'
  const head = items[0]
  const label = head.id ? `${firstSentence(head.description || head.id)} (${head.id})` : firstSentence(head.description || '')
  return items.length === 1 ? label : `${label}, and ${items.length - 1} more unresolved`
}

// Advisory next step, approximating the flow dispatch order in plain words. flow stays authoritative.
function likelyNext(f, livePG, liveConst) {
  if (f.status === 'done') return 'nothing: this feature is done (consider probing it with break, or raising its depth with promote)'
  const r = f.readiness || {}
  const markers = markerCount(f._dir)
  const g = gateState(f, livePG, liveConst)
  const open = unresolvedItems(f.open_decisions).length + unresolvedItems(f.human_signoff).length
  if ((r.research || 'none') !== 'ready') return 'finish the research (discover, then define)'
  if (r.design && r.design !== 'n/a' && r.design !== 'ready') return 'finish the design'
  if (markers > 0) return `answer the ${markers} open question${markers === 1 ? '' : 's'} in the spec (clarify)`
  if ((r.spec || 'none') !== 'ready') return 'finish the spec (specify)'
  if ((r.plan || 'none') !== 'ready') return 'write the plan'
  if (g.verdict === 'not-run' || g.stale) return 'run the pre-build check (analyze)'
  if (g.verdict === 'blocking' || g.verdict === 'blocking-hard') return 'resolve the pre-build findings, then re-run the check'
  if (open > 0) return 'resolve the open decisions and sign-offs'
  if ((r.tasks || 'none') !== 'ready') return 'build it (implement)'
  if (depthOf(f) !== 'prototype') {
    if (!f.converge) return 'audit the code against the spec (converge), then mark it done'
    if (Number(f.converge.contradicts || 0) > 0) return 'fix the code-vs-spec contradictions found by the audit'
  }
  return 'mark it done (run flow to close it out)'
}

function questionsLine(f) {
  const markers = markerCount(f._dir)
  const decisions = unresolvedItems(f.open_decisions).length
  const signoffs = unresolvedItems(f.human_signoff).length
  const parts = []
  if (markers > 0) parts.push(`${markers} in the spec`)
  if (decisions > 0) parts.push(`${decisions} decision${decisions === 1 ? '' : 's'} waiting on a human`)
  if (signoffs > 0) parts.push(`${signoffs} sign-off${signoffs === 1 ? '' : 's'} outstanding`)
  return parts.length === 0 ? 'none' : parts.join(' · ')
}

// ---- output modes ----

function featureCard(f, livePG, liveConst) {
  out(`📍 ${f.title || f.slug}  (${f.id} · ${f.slug} · depth ${depthOf(f)} · ${f.status})`)
  out(`Road:           ${roadLine(f)}`)
  out(`Gate:           ${gateLine(f, livePG, liveConst)}`)
  out(`Waiting on you: ${waitingLine(f)}`)
  out(`Likely next:    ${likelyNext(f, livePG, liveConst)}`)
  out(`Open questions: ${questionsLine(f)}`)
}

function portfolio(features, livePG, liveConst) {
  const done = features.filter((f) => f.status === 'done').length
  let stale = 0, blocking = 0, notRun = 0, passFresh = 0
  let contradictions = 0, gaps = 0, audited = 0
  let markers = 0, decisions = 0
  for (const f of features) {
    const g = gateState(f, livePG, liveConst)
    if (g.verdict === 'not-run') notRun += 1
    else if (g.stale) stale += 1
    else if (g.verdict === 'pass') passFresh += 1
    else blocking += 1
    if (f.converge) {
      audited += 1
      contradictions += Number(f.converge.contradicts || 0)
      gaps += Number(f.converge.open || 0)
    }
    markers += markerCount(f._dir)
    decisions += unresolvedItems(f.open_decisions).length + unresolvedItems(f.human_signoff).length
  }
  out(`Project: ${features.length} feature${features.length === 1 ? '' : 's'} · ${done} done · ${features.length - done} active`)
  out(`Gates:   ${passFresh} passed and current · ${blocking} with blockers · ${stale} needing a re-run (shared rules changed) · ${notRun} not yet run`)
  out(`Drift:   ${audited} of ${features.length} audited against the code · ${contradictions} contradiction${contradictions === 1 ? '' : 's'} · ${gaps} gap${gaps === 1 ? '' : 's'} open`)
  out(`Open:    ${markers} spec question${markers === 1 ? '' : 's'} · ${decisions} decision${decisions === 1 ? '' : 's'} waiting on a human`)
  let read
  if (contradictions > 0) read = `${contradictions} contradiction${contradictions === 1 ? '' : 's'} between code and spec need remediation first`
  else if (stale > 0) read = `structurally healthy; one analyze sweep would refresh ${stale} gate${stale === 1 ? '' : 's'}`
  else if (blocking > 0) read = `${blocking} feature${blocking === 1 ? ' is' : 's are'} held by pre-build findings`
  else if (markers > 0) read = `the front of work is answering the ${markers} open spec question${markers === 1 ? '' : 's'}`
  else if (notRun > 0) read = `${notRun} feature${notRun === 1 ? ' has' : 's have'} not run their pre-build check yet`
  else read = 'all green'
  out(`Read:    ${read}`)
}

// ---- main ----

const features = loadFeatures(join(root, 'features'))
const pgPath = join(root, 'product-global.md')
const conPath = join(root, 'constitution.md')
const livePG = existsSync(pgPath) ? fileHash(pgPath) : null
const liveConst = existsSync(conPath) ? fileHash(conPath) : null

if (target == null) {
  portfolio(features, livePG, liveConst)
} else {
  const f = features.find((x) => x.slug === target || x.id === target)
  if (!f) fail(`no feature "${target}" (known: ${features.map((x) => x.slug).join(', ') || 'none'})`)
  featureCard(f, livePG, liveConst)
}
process.exit(0)

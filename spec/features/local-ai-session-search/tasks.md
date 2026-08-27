# Tasks - Local AI Session Search

## Foundation and contracts

- [x] T01 Scaffold `SessionSearch.slnx`, the three production projects, four test
  projects, benchmark project, central package versions, approved-source
  `NuGet.config`, locked restore, High and Critical audit failure policy, shared
  analyzers, raw-multiline-string source check, Release settings, and repository
  build scripts (feat-001/AC-20). Files: `SessionSearch.slnx`, `global.json`,
  `Directory.*`, `NuGet.config`, `.editorconfig`, project files, package locks,
  `.gitignore`, `scripts/check-source.mjs`, and `scripts/build.ps1`.
- [~] T02 Add sanitized Claude Code and Codex metadata, transcript, child-log,
  active-marker, malformed, append, partial-line, oversized, deep-JSON, control,
  UNC, device, junction, hostile-PATH, script-shim, signed-executable, migration,
  ACL, purge-sentinel, and artifact-canary fixtures under `tests/Fixtures`;
  record expected normalized outputs and source integrity data.
- [x] T03 Write Core tests first, then implement immutable models, provider
  identities, availability precedence, Form KC text normalization,
  display-safe projection, `SessionTextResolver`, bounded `QueryParser`, literal
  FTS expression model, and the complete rank tuple in `src/SessionSearch.Core`
  (feat-001/AC-4, feat-001/AC-6, feat-001/AC-12). Files:
  `src/SessionSearch.Core/{Models,Providers,Search,Sessions,Text}` and
  `tests/SessionSearch.Core.Tests`.

## Provider adapters

- [~] [P] T04 Write Claude adapter fixture tests first, then implement root
  local fixed-drive and handle-containment checks, fast metadata discovery,
  streamed recognized JSONL record parsing,
  filename and record identity validation, custom-title precedence, stub state,
  control-envelope classification, nested child ownership, skipped sidecars, and
  sanitized diagnostics in `src/SessionSearch.Infrastructure/Claude` (feat-001/AC-1,
  feat-001/AC-5, feat-001/AC-6, feat-001/AC-17, feat-001/AC-18).
- [~] [P] T05 Write Codex adapter fixture tests first, then implement root
  local fixed-drive and handle-containment checks, explicit SQLite read-only plus
  query-only state-database probing with sidecar integrity, rollout
  filename-matched session metadata, streamed recognized event parsing,
  recursive child ownership independent of spawn edges, archive moves, duplicate
  enrichment handling, and sanitized diagnostics in
  `src/SessionSearch.Infrastructure/Codex` (feat-001/AC-1,
  feat-001/AC-5, feat-001/AC-6, feat-001/AC-14, feat-001/AC-17,
  feat-001/AC-18).

## Storage, search, and indexing

- [x] T06 Write migration and capability tests first, then implement the pinned
  SQLite bootstrap, protected current-user app-data DACL, sidecar ACL validation,
  application ID and schema allowlist, hardening pragmas and runtime limits,
  FTS5 capability and integrity checks, WAL configuration, secure deletion,
  transactional migration rollback, repositories, and reparse-safe path
  isolation in `src/SessionSearch.Infrastructure/Storage` (feat-001/AC-19,
  feat-001/AC-20).
- [x] T07 Write search integration tests first, then implement parameterized
  metadata candidate queries, canonically escaped literal FTS5 expressions,
  numeric query limits and cancellation, class merging, total ordering, 50-row
  paging, snippets, scopes, and Partial state in
  `src/SessionSearch.Infrastructure/Search` (feat-001/AC-3,
  feat-001/AC-4, feat-001/AC-5).
- [~] T08 Write incremental-index integration tests first, then implement bounded
  streaming readers, oversized-line skipping, JSON depth and extraction limits,
  finite length snapshots, 64 KiB segments, disk and index limits, a single
  transactional writer, complete-line offsets, append reuse, per-source
  replacement, child rollup, cancellation, and retained committed progress in
  `src/SessionSearch.Infrastructure/Indexing`
  (feat-001/AC-13, feat-001/AC-17).
- [~] T09 Add discovery-first startup, debounced watcher hints, periodic and
  user-triggered full root reconciliation, create and archive-move discovery,
  complete-generation deletion, interrupted restart, removed-source rules,
  physical removed-content purge, progress and timing snapshots, and Rescan with
  integration tests (feat-001/AC-2, feat-001/AC-7, feat-001/AC-13,
  feat-001/AC-17, feat-001/AC-19).
- [~] T10 Implement transactional session and directory favorites, Windows path
  normalization, missing-path display metadata, and rollback notices with
  restart and injected-failure tests (feat-001/AC-7).

## Resume actions and active state

- [x] [P] T11 Write process-identity tests first, then implement Claude activity
  files, explicit resume-command mapping, possibly-active and unmapped warnings,
  and Codex held-lock plus child-rollup validation through an injectable process
  snapshot seam in `src/SessionSearch.Infrastructure/Windows` (feat-001/AC-8).
- [~] [P] T12 Write launch and quoting tests first, then implement CLI and
  Windows Terminal trusted absolute executable resolution, expected Authenticode
  publisher checks, hostile-PATH and current-directory rejection, UUID and final
  path revalidation, structured `-w 0 new-tab` plans with shell execution off,
  the recording launcher, PowerShell literal formatting, and clipboard history
  and cloud-exclusion formats (feat-001/AC-9, feat-001/AC-10,
  feat-001/AC-18).
- [~] T13 Implement the application command router for single Open and Copy,
  visible-order batch deduplication, the four named selection categories, Unsafe
  directory and other action-matrix skips, continued failures, and exact notices
  with mixed-selection tests (feat-001/AC-10, feat-001/AC-11,
  feat-001/AC-12, feat-001/AC-18).

## Native application

- [~] T14 Build the startup composition root, protected and hardened database
  access, search-first window shell, dominant search header, scope controls,
  collapsible favorites rail, and virtual result list in
  `src/SessionSearch.App`.
- [x] T15 Add asynchronous page caching and cancellation, selection action bar,
  details pane, match excerpt, title and description metadata, command preview,
  both favorite controls, Index status with startup, query, memory, index-size,
  and progress telemetry, Rescan, four-category selection counts, query-limit
  messages, and the named status region (feat-001/AC-2, feat-001/AC-3).
- [~] T16 Implement the complete keyboard and focus model, accessible names and
  state text, status announcements, dark and high-contrast token switches,
  reduced-motion behavior, responsive pane collapse, and 200 percent scaling.
  Prove it through external UI Automation and offscreen preference-injection
  acceptance tests that never steal focus (feat-001/AC-16).

## Acceptance, performance, and delivery

- [~] T17 Build a synthetic end-to-end corpus test that indexes, searches,
  favorites, formats commands, simulates launches, reconciles, injects migration
  failure, verifies a secure-deletion sentinel, and confirms every provider
  fixture including Codex DB sidecars keeps its hash, length, and timestamp
  (feat-001/AC-1, feat-001/AC-5, feat-001/AC-7, feat-001/AC-9,
  feat-001/AC-11, feat-001/AC-13, feat-001/AC-14, feat-001/AC-19).
- [x] T18 Run the hostile security acceptance matrix for local-path containment,
  junctions, UNC and device rejection before probing, DACL inheritance, hostile
  executable lookup, signature publisher, UUID validation, time-of-check path
  swaps, SQLite schema tampering, and diagnostics redaction
  (feat-001/AC-18, feat-001/AC-19, feat-001/AC-20).
- [~] T19 Implement the JSON benchmark harness, fixed query-category manifest,
  existing-index and first-run milestones, p95 search runs, working-set sampler,
  and 25 ms UI probe. Run synthetic calibration, then a read-only real-corpus
  index only under the random protected LocalAppData benchmark root, emit the
  aggregate redacted report, and remove the real index (feat-001/AC-2,
  feat-001/AC-3, feat-001/AC-15, feat-001/AC-19).
- [x] T20 Launch the Release build against sanitized synthetic fixtures and use
  background Windows UI inspection to
  verify recent/search/favorite/details/index/error states at normal and 200
  percent scaling; save screenshots and UI Automation evidence under
  `artifacts/visual-qa`. Run the benign real Windows Terminal sentinel check,
  inspect its uniquely titled tab and working directory, and close only that tab
  (feat-001/AC-9, feat-001/AC-16, feat-001/AC-19).
- [x] T21 Add README usage, local-only threat boundary, protected storage and
  purge limits, exact provider and Terminal prerequisites, clipboard-history
  caveat, command-copy examples, diagnostics guidance, and migration-failure
  recovery. Publish the versioned framework-dependent ReadyToRun `win-x64`
  folder under `artifacts/release`.
- [x] T22 Run approved-source locked restore and audit, source-string check,
  format, Release build, all tests, SQLite capability and hardening checks,
  specification validation, generated-document refresh, source-integrity and
  sensitive-artifact scans, and repository-wide literal Unicode dash scan.
  Record final evidence and close every verified task (feat-001/AC-20).

## Convergence remediation

- [ ] T23 Add target-machine provider-root override coverage and complete title
  and description fallback and 180-scalar boundary fixtures (feat-001/AC-1,
  feat-001/AC-6).
- [ ] T24 Instrument warm existing-index result-list visibility, optimize the
  real-corpus transcript group to at most 150 ms p95 without changing exact
  rank semantics, and rerun the protected aggregate benchmark report
  (feat-001/AC-2, feat-001/AC-3).
- [ ] T25 Complete the query, favorite rollback, resume-command, batch-action,
  and availability boundary matrices, including both providers, every numeric
  query limit, all special path characters, Possibly active selection, and a
  first-seen unidentified record (feat-001/AC-4, feat-001/AC-7,
  feat-001/AC-10, feat-001/AC-11, feat-001/AC-12).
- [ ] T26 Either implement the planned bounded Channel and single-writer index
  pipeline or approve a plan change for the measured sequential design, then
  add watcher, interrupted restart, locked-file, authoritative Rescan, and full
  retry and reconciliation acceptance coverage (feat-001/AC-13,
  feat-001/AC-17).
- [ ] T27 Add the real WinForms 25 ms UI-thread probe and a narrow-layout
  favorites and details drawer or toolbar alternative, then repeat keyboard,
  focus-return, high-contrast, reduced-motion, and 200 percent verification
  against those surfaces (feat-001/AC-15, feat-001/AC-16).
- [x] T28 Implement the exact official Codex installer-alias exception with
  metadata-only validation of both expected redirects, current-release equality,
  final release-root containment, reparse-free target validation, WinVerifyTrust
  plus the versioned full OpenAI signer-subject allowlist, final-path-only copy
  and launch, identity revalidation, hostile redirection and retarget tests, and
  a target-machine published smoke check (feat-001/AC-18).

## Parallelism self-check

T04 and T05 share only the interfaces and fixture conventions completed by T02
and T03, so they can proceed together. T11 and T12 share only completed Core
contracts and can proceed together. Every other task consumes prior storage,
provider, or presentation output and is intentionally sequential.

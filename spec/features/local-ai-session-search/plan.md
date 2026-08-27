# Implementation Plan - Local AI Session Search

## Outcome

Build a native Windows application that opens immediately against its last
committed index, incrementally discovers and indexes local Claude Code and Codex
sessions, performs deterministic metadata and transcript search, manages local
favorites, and safely resumes Ready sessions in Windows Terminal.

## Design decisions

### 1. Native stack and delivery

- Target .NET 10 with WinForms on `net10.0-windows` and `win-x64`.
- Use standard WinForms controls, including a Details-mode `ListView` with
  `VirtualMode`, so only visible results become controls and Windows UI
  Automation remains available without a browser runtime.
- Publish a framework-dependent, ReadyToRun folder first. The installed Windows
  Desktop Runtime keeps the package small and avoids self-contained runtime
  duplication. A self-contained package is deferred unless deployment proves it
  necessary.
- Enable Workstation GC and avoid Generic Host, dependency-injection containers,
  object-relational mappers, browser controls, and semantic embedding libraries.

This follows the constitution's native Windows and lightweight defaults. Raw
Win32 Rust has a lower theoretical floor, but manual UI Automation, DPI, unsafe
ownership, and message-loop work add prototype risk without reducing the 8 GB
indexing load. WinForms keeps the primary controls native while leaving a clear
escape hatch for a native indexer helper if measured limits are missed.

### 2. Projects and public seams

```text
SessionSearch.slnx
src/
  SessionSearch.Core/
  SessionSearch.Infrastructure/
  SessionSearch.App/
tests/
  SessionSearch.Core.Tests/
  SessionSearch.Provider.Tests/
  SessionSearch.IntegrationTests/
  SessionSearch.AcceptanceTests/
  Fixtures/
benchmarks/
  SessionSearch.Benchmarks/
```

- `SessionSearch.Core` owns immutable domain models, query parsing, text
  normalization, title and description rules, availability evaluation, provider
  interfaces, launch plans, and orchestration contracts. It has no SQLite or UI
  dependency.
- `SessionSearch.Infrastructure` owns provider adapters, read-only source access,
  SQLite schema and repositories, FTS5 querying, incremental indexing, process
  inspection, CLI resolution, and Windows Terminal process creation.
- `SessionSearch.App` is the composition root and WinForms presentation layer.
  It receives page-sized result models through asynchronous services and never
  parses source files on the UI thread.
- `SessionSearch.Benchmarks` is a small console harness using `Stopwatch` and JSON
  output. It shares production services instead of adding BenchmarkDotNet to the
  shipped application.

The acceptance seams are `ISessionProvider`, `ITranscriptRecordReader`,
`ISessionIndex`, `ISessionSearch`, `IResumePlanner`, `IProcessLauncher`,
`IActiveSessionDetector`, `IndexCoordinator`, and the application command router.
Tests do not reach around those interfaces into private implementation details.

### 3. Pinned dependencies and test toolchain

- `Microsoft.Data.Sqlite.Core` 10.0.11
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5
- `Microsoft.NET.Test.Sdk` 18.9.0
- `xunit.v3` 4.0.0
- `coverlet.collector` 10.0.1, test projects only

Central package management and locked restore files pin the graph. Startup
asserts the native SQLite version and verifies that `ENABLE_FTS5` is present.
Tests run with `dotnet test SessionSearch.slnx --configuration Release`. The
acceptance project targets Windows and uses the platform UI Automation client
assemblies, so no third-party desktop driver ships with the product.

Repository `NuGet.config` permits only nuget.org with package-source mapping and
requires repository or author signatures. Restore uses locked mode, enables
NuGet audit across direct and transitive packages, and fails on High or Critical
findings. A source-policy check rejects any unapproved source added later.

### 4. App-owned data model

The default database is
`%LOCALAPPDATA%\SessionSearch\session-search.sqlite3`. Tests and benchmarks pass
an isolated path through startup options. Provider storage is always opened
read-only with read, write, and delete sharing where files can be appended.

Before any SQLite file is created, `AppDataSecurity` rejects a reparse-point or
escaped root and applies a protected DACL granting full control only to the
current user SID, Local System, and Builtin Administrators. It verifies the root
and inherited database, WAL, SHM, diagnostic, temporary, and benchmark files
before use. A permissive or substituted path fails closed.

- `sessions`: provider, immutable session ID, trusted source, title,
  description, directory, branch, model, created and updated UTC values, source
  bytes, archive and content states, parser version, availability inputs, and
  normalized metadata columns. Primary key is provider plus session ID.
- `source_files`: canonical path, provider, owning session key, top-level or
  child kind, file identity sample, length, last-write UTC, last complete byte
  offset, parser version, status, retry count, and last non-sensitive error.
- `segments`: stable row ID, owning session key, source file key, ordinal, role,
  UTC timestamp, segment kind, child flag, and searchable normalized text.
- `transcript_fts`: external-content FTS5 table over `segments.text`, tokenized
  with `unicode61 remove_diacritics 2` and updated explicitly in the same
  transaction as segment rows.
- `session_favorites` and `directory_favorites`: app-owned independent state.
- `diagnostics`, `settings`, and `schema_migrations`: bounded operational state
  without transcript bodies.

SQLite uses WAL, foreign keys, `busy_timeout`, `synchronous=NORMAL`, an 8 MB page
cache target, `secure_delete=ON`, FTS secure deletion, `trusted_schema=OFF`, and
memory mapping disabled. Extension loading is never enabled. Each connection
checks application ID, schema version, an exact schema-object allowlist, and
reduced SQLite runtime limits before use. Startup runs `quick_check`; acceptance
also runs FTS integrity checks. One writer owns short batch transactions. Read
connections are short-lived and cancellation-aware.

Schema changes run in an explicit transaction after validation. An injected
failure rolls back to byte-for-byte usable schema state. A future migration that
cannot be transactional must build and validate a new database inside the same
protected directory, then swap only after success. The prototype does not keep
an unprotected migration copy.

Removed transcript rows use explicit FTS and content deletion, then a truncate
checkpoint. After clean close, a purge acceptance check scans the live database,
WAL, and SHM for a unique sentinel. If SQLite cannot meet that database-level
boundary, the coordinator builds a clean protected database and swaps it only
after integrity verification. SSD firmware, external backups, administrators,
and malicious same-user processes remain outside this purge guarantee.

### 5. Versioned provider adapters

- Provider roots respect `CLAUDE_CONFIG_DIR` and `CODEX_HOME`, then fall back to
  the current user's standard roots.
- `LocalPathPolicy` performs lexical rejection before any filesystem call, then
  canonicalizes an opened handle. Roots and working directories must be absolute
  local fixed-drive paths, may not use UNC or device namespaces, and may not
  cross a reparse-point component. Every source handle's final path must remain
  inside its trusted root using ordinal case-insensitive Windows comparison.
  Invalid overrides fail that provider visibly instead of silently falling back.
- Discovery and transcript parsing are separate. Discovery yields trusted
  top-level identities and fast metadata. Transcript readers stream complete
  JSONL records without loading a source file into memory.
- The Codex adapter scans live and archived `rollout-*.jsonl` files, takes the
  filename UUID as the file identity, and selects the `session_meta` whose
  payload ID matches it. Parent metadata cloned into a child file is never
  accepted as that file's identity. `parent_thread_id`, `agent_path`, subagent
  thread source, or object-shaped subagent source marks a child; its root owner
  is resolved recursively with cycle protection. The highest compatible Codex
  state database is an optional metadata accelerator opened with
  `Mode=ReadOnly;Cache=Private` plus `PRAGMA query_only=ON` after required column
  probes. It never attaches, migrates, recovers, or creates sidecars. Rollout
  JSONL remains the transcript source and fallback.
- The Claude adapter discovers immediate UUID JSONLs under each encoded project
  directory without decoding that directory. A trusted resumable candidate needs
  a filename-matching session ID, non-sidechain user or assistant record, and
  recorded working directory. Bridge-only, system-only, or metadata-only stubs
  keep their trusted identity as Unsupported format. `agent-*.jsonl` files below
  an owner UUID's `subagents` tree roll up to that owner after validating any
  child session ID. Workflow journals, sidecars, attachments, and file-history
  artifacts are skipped.
- Title mapping is provider-aware. Claude uses latest custom title before latest
  AI title regardless of record order. Codex uses its provider-supplied explicit
  session name before latest compatible thread-name enrichment and provider
  title. Both fall
  back to first included user text and immutable ID under the shared Core rule.
- Every accepted shape has a parser version and a sanitized fixture. Unknown
  first-seen shapes become diagnostics unless provider plus immutable ID are
  trusted. A previously indexed trusted row retains last committed metadata and
  becomes Unsupported format.

Candidate enumeration skips directory reparse points. Each file is opened once
with provider-compatible sharing, its final handle path is revalidated, and all
parsing occurs through that handle. A containment failure becomes a source-only
diagnostic. Persisted diagnostics use provider alias plus root-relative path or a
stable local hash, while the interactive detail view may resolve the full path
without exporting it.

Provider-specific field mappings and active-marker rules will be recorded beside
their fixtures. Adapters never infer identity from a directory name or display
title.

### 6. Incremental indexing pipeline

1. Open and migrate the app database without touching provider storage.
2. Return cached recent rows immediately.
3. Discover fast provider metadata, upsert it in small transactions, and publish
   the first 50 rows as soon as both provider passes finish or one is absent.
4. Queue changed content through a bounded `Channel` with a small number of
   streaming readers and exactly one SQLite writer.
5. Commit only complete JSONL records. Persist the last complete byte offset,
   length, last-write value, and a file-identity sample.
6. On append, parse from the committed offset. On shrink, identity mismatch, or
   parser-version change, replace only that source's segments in one transaction.
7. Treat `FileSystemWatcher` events as hints. Debounce them and run periodic plus
   user-triggered reconciliation because watcher buffers can overflow.
8. Recompute owning-session recency and content state transactionally. Delete
   removed non-favorite sessions and transcript content; retain only favorite
   metadata for removed favorite sessions.

Each pass snapshots source length at open. A byte newline scanner can skip an
oversized record without materializing it. Limits are 32 MiB per JSONL record,
JSON depth 64, 8 MiB extracted text per record, 64 KiB UTF-8 per stored segment,
100,000 candidates per provider generation, 64 GiB app database, and 5 GiB
minimum fixed-disk free space. Crossing a limit stops only the affected record,
provider generation, or content phase at a committed boundary and marks search
Partial. Cancellation is checked between records, each MiB of scanning, and each
stored segment.

Cancellation stops after a safe record or transaction boundary. Closing the app
does not discard committed progress, and startup requeues incomplete sources.

### 7. Deterministic query service

`QueryParser` implements the specification's quote, Form KC, casing, whitespace,
AND, and unmatched-quote rules. It creates a metadata predicate and a parameter
value for a generated FTS5 expression. User text is never inserted into SQL.

It rejects NUL, more than 512 Unicode scalars, 32 atoms, 128 transcript tokens,
or a generated expression above 4,096 characters. Every FTS token is emitted as
an escaped quoted literal, and only application code appends the prefix operator.
Fuzz tests cover every reserved FTS operator, quote, parenthesis, combining mark,
control character, and limit boundary.

Metadata candidates and FTS candidates are merged by provider plus session ID.
The service computes match classes 0 through 7, uses FTS5 `bm25` only for class
7, applies the complete stable tie-break tuple, and returns at most 50 result
models per page. Snippet lookup is a separate page-sized query. Blank search
uses an indexed updated-time order and does not touch FTS.

### 8. Availability, favorites, and active detection

`AvailabilityEvaluator` applies the specified precedence from trusted adapter
state, archive state, active detection, directory existence, and CLI resolution.
The UI receives a status plus allowed actions, not booleans it can contradict.

- A Codex writer lock produces Active only when an exclusive-open probe receives
  a sharing violation. Openable stale locks do not block, and a held child lock
  rolls up to its root owner.
- Claude activity files and explicit resume command lines must match session ID,
  PID, expected executable, and process-start fingerprint when provided. A live
  matching provider process with a session-specific marker but no fingerprint is
  Possibly active and blocks. A different executable or stale PID does not
  block. A live Claude process that cannot be mapped produces a global warning
  without changing any session's status.
- Session favorites key on provider plus ID. Directory favorites key on a
  Windows-normalized full path using ordinal case-insensitive equality while
  preserving the last display casing.
- Favorite writes are transactional. The presentation model changes only after
  commit, or rolls back and raises the named status notice on failure.

### 9. Safe resume and command copy

`ResumePlanner` produces structured plans, never command strings, for direct
launch. `TrustedExecutableResolver` runs before applying a session directory. It
accepts exact-name local `.exe` files from known install roots or an explicit app
setting only after canonical-path, fixed-drive, no-reparse, Authenticode status,
and expected-publisher validation. For the exact official Codex installer alias,
the resolver reads redirect metadata without opening the alias executable. Its
`bin` redirect must target the exact provider `current\bin` path, and `current`
must target one immediate version directory under the known standalone releases
root. The derived final `codex.exe` must itself be reparse-free, local,
fixed-drive, WinVerifyTrust-valid, and signed with a subject in the versioned
exact OpenAI allowlist. `ResolvedExecutable.CanonicalPath`, structured launch
arguments, displayed commands, and clipboard commands use only this verified
final versioned path, never the mutable alias. Revalidation repeats alias
convergence, WinVerifyTrust, signer-subject, and file-identity checks before
dispatch. Signer policy `codex-signer-v1` is defined on the application Codex
executable profile. It compares `X509Certificate2.Subject` by ordinal equality
and initially accepts only
`CN="OpenAI OpCo, LLC", O="OpenAI OpCo, LLC", L=San Francisco, S=California, C=US`.
Windows Terminal's user alias is accepted only when the matching registered
Microsoft package and signed package binary validate. Empty or relative PATH
entries, current-directory lookup, script shims, wrong publishers, and
option-looking or non-UUID IDs are rejected.

For one Ready row the planner adds `-w 0`, `new-tab`, the safe recorded directory,
the resolved provider executable, and exact resume-by-ID arguments through
`ProcessStartInfo.ArgumentList`. It sets `UseShellExecute=false` and revalidates
directory and executable identity immediately before process creation.

Batch open deduplicates provider plus ID and executes one plan per Ready row in
visible order. It continues after failures and returns named opened, skipped,
and failed records. This favors simple failure isolation over one complex
Windows Terminal command line.

`PowerShellCommandFormatter` is a separate pure component. It emits
`Set-Location -LiteralPath '<path>'; & '<executable>' ...` and doubles every
apostrophe inside a single-quoted literal. Only Ready rows produce commands.
Clipboard text is exactly the preview text. Missing Windows Terminal and launch
failure never route through PowerShell automatically.

The clipboard adapter adds the registered Windows formats that request exclusion
from clipboard history and cloud upload. It does not claim protection from a
same-user clipboard monitor and never logs or persists copied commands.

Acceptance combines recording-launcher tests with one real benign Windows
Terminal check. The real check opens a uniquely titled tab running a harmless
PowerShell sentinel in an isolated safe directory, verifies one added tab and the
actual working directory through UI Automation and the sentinel, then closes only
that tab. It never invokes Claude Code or Codex and is driven through background
window automation.

### 10. Native UI and accessibility

- A compact header contains the dominant search field, provider scopes, Starred,
  content-state text, and Index status.
- A collapsible favorite-directory rail, virtual result list, and collapsible
  details pane realize the flight-recorder design. Standard buttons implement
  session and directory favorite actions; glyphs are decorative companions to
  text and accessible names.
- The result list caches only requested pages. Cancellation and a short debounce
  prevent stale searches from replacing current results.
- A selection bar exposes Ready and skipped counts, Open ready tabs, Copy
  commands, and Clear selection.
- A named status region announces index, clipboard, favorite, Partial search,
  and launch state through UI Automation without moving focus.
- System colors replace custom tokens in high contrast. Layout and minimum sizes
  are verified at 100 and 200 percent scaling. No diagnostic launch or test may
  take foreground focus.
- Index status displays process-start to first usable rows, metadata-ready time,
  latest query duration, rolling metadata and transcript 95th percentiles,
  current working set, index bytes, and progress. Its bounded timing samples
  never retain query text, IDs, paths, titles, snippets, or commands.
- The selection bar reports the four specified categories separately: Ready,
  active or possibly active, duplicate, and other unavailable.

### 11. Diagnostics, integrity, and packaging

Diagnostics record provider, sanitized source reference, parser version, status code, retry
state, timing, and exception type. Persisted paths are provider aliases plus
root-relative paths or stable local hashes. Diagnostics do not record transcript
bodies, raw queries, environment values, session IDs, titles, snippets, or
resume commands. Logs are size-bounded under protected local application data.

Acceptance fixtures are copied to isolated protected temporary roots. Their hashes,
lengths, and last-write times are recorded before and after all operations.
Real-corpus benchmarks write their database only under a random protected
`%LOCALAPPDATA%\SessionSearch\Benchmarks` directory and remove it after producing
a sanitized report. The repository receives aggregate counts, sizes, timing,
query category IDs, and per-run salted root pseudonyms only. Saved screenshots
and UI Automation captures always use the sanitized synthetic corpus.

The release output is a versioned `win-x64` ReadyToRun folder plus a PowerShell
launcher and concise README. A source-built release must pass restore-lock,
format, build, unit, provider, integration, acceptance, Unicode-dash scan, SQLite
capability, and spec validation checks. `scripts/check-source.mjs` lexes C# string
tokens and fails if a verbatim or interpolated verbatim string crosses a line;
tests prove the check accepts raw multiline strings and rejects non-raw ones.
The artifact scanner rejects SQLite magic, WAL, SHM, JSONL, real-root canaries,
and transcript canaries from repository artifacts and release output.

## Verification approach

| Criterion | Test seam and planned proof |
| --- | --- |
| feat-001/AC-1 | `ISessionProvider.DiscoverAsync`; provider fixture tests cover roots, top-level identity, and child ownership |
| feat-001/AC-2 | App startup event stream and benchmark harness; real-corpus existing-index and empty-index reports use the specified milestones |
| feat-001/AC-3 | `ISessionSearch.SearchAsync`; versioned 20-query real-corpus benchmark reports first-page p95 by query group |
| feat-001/AC-4 | `QueryParser` and `ISessionSearch`; table-driven grammar, matching, rank tuple, snippet, scope, reserved-literal fuzzing, numeric limits, and Partial tests |
| feat-001/AC-5 | Provider adapters through `ISessionIndex`; Claude and Codex child-only fixtures return one labeled owner |
| feat-001/AC-6 | `SessionTextResolver`; table-driven typed-record, envelope, scalar-boundary, precedence, display-control projection, and offline tests |
| feat-001/AC-7 | `IFavoritesStore` and reconciliation; restart, case, save-failure, removed-favorite, and removed-non-favorite tests |
| feat-001/AC-8 | `IActiveSessionDetector`; active, possibly active, stale, PID-reuse, different-executable, held child lock, released lock, and unmapped-process fixtures |
| feat-001/AC-9 | `IResumePlanner`, recording launcher, and benign real Terminal sentinel; exact provider argument sequence plus actual single-tab count and working directory |
| feat-001/AC-10 | `PowerShellCommandFormatter` and clipboard adapter; PowerShell round-trip fixtures, clipboard privacy formats, and Terminal-missing and launch-failure states |
| feat-001/AC-11 | Application command router; ordered mixed selections with duplicates, blocked rows, injected failures, and exact batch-copy lines |
| feat-001/AC-12 | `AvailabilityEvaluator` plus provider diagnostics; every precedence and action row, trusted prior format, and unknown first-seen format |
| feat-001/AC-13 | `IndexCoordinator`; append, partial tail, create, archive move, delete generation, watcher hint, full Rescan, interrupted restart, shrink, replacement, parser-version, each resource limit, finite pass, locked, and corrupt-source cases |
| feat-001/AC-14 | Acceptance corpus wrapper; SHA-256, length, and last-write values for sources and read-only Codex database sidecars remain identical around every public operation |
| feat-001/AC-15 | Benchmark harness and UI probe; protocol memory samples and bounded indexing response maximum |
| feat-001/AC-16 | WinForms command router plus external UI Automation process test; focus order, names, status text, shortcuts, selection, overlay focus return, injected high contrast and reduced motion, responsive collapse, and 200 percent layout |
| feat-001/AC-17 | `IndexCoordinator.RescanAsync`, fresh-query retry, and diagnostics repository; retained commits, full root reconciliation, sanitized fields, and no body leakage |
| feat-001/AC-18 | `LocalPathPolicy`, `TrustedExecutableResolver`, installer-alias metadata resolver, and launch revalidation; hostile root, junction, UNC, device, PATH, current-directory, shim, publisher, full signer subject, exact redirect topology, stale and nested release, final-path-only dispatch, UUID, and time-of-check fixtures |
| feat-001/AC-19 | `AppDataSecurity`, migration runner, secure purge harness, benchmark sanitizer, and artifact scanner; DACL inheritance, rollback, sentinel absence, synthetic-only captures, and no sensitive repository artifact |
| feat-001/AC-20 | Release scripts and SQLite bootstrap; raw-string rejection fixture, approved locked restore and audit, native SQLite capability, schema allowlist, hardening pragmas, limits, and integrity checks |

Every proving test includes its qualified token in its method display name or an
adjacent test comment. `tests/**/*.cs` is the spec validator's acceptance-token
scan boundary.

## Risk controls

- Full-text indexing may make the database larger than source data. Record index
  growth during the real run, expose it in Index status, and keep segment sizes
  bounded. Do not silently omit searchable record classes to hit a size target.
- Provider schemas are internal. Keep fixtures sanitized, version parsers, probe
  optional metadata stores, and make an unsupported source visible.
- Windows Terminal reports process start before a tab command fully settles in
  some cases. Treat process-creation failure as definitive and report later exit
  failures when observable; never claim the resumed CLI itself completed.
- The prototype excludes a malicious same-user process that replaces a verified
  provider executable between final identity revalidation and the Windows
  loader opening it. MVP hardening should hold a non-delete-sharing file handle
  through process creation where the Windows launch contract permits it.
- The 100 MB target is strict for managed desktop code. Cap SQLite cache, avoid
  transcript materialization, page UI results, and measure before considering a
  native helper.

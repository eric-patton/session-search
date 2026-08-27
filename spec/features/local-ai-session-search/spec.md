## Why

Local Claude Code and Codex sessions are split across provider-specific stores
and pickers. At the target scale, remembering a phrase or directory is not
enough to recover the right conversation quickly. This feature provides one
local, fast, read-only index and a safe path back into the selected work.

## User stories

- As a Windows developer, I want to see sessions from both providers ordered by
  recent activity so that I can return to current work without remembering which
  CLI created it.
- As a returning user, I want to search metadata and transcript text so that a
  remembered phrase, path, error, or command finds the owning session.
- As a user with recurring projects, I want to favorite sessions and directories
  independently so that important work remains one action away.
- As a user who found a session, I want to resume it in a Windows Terminal tab or
  copy an exact PowerShell command so that I can continue from its directory.
- As a user returning to several tasks, I want to open a mixed selection in
  separate terminal tabs so that I can restore a working set at once.
- As a user with a changing local history, I want indexing to stay current and
  explain partial failures so that search remains trustworthy.

## Behavior & scenarios

- **Scenario: Return to recent work**

- Given a usable application index exists
- When the application starts with a blank search
- Then it focuses search and shows top-level sessions from both providers sorted
  by last activity, newest first
- And each row shows favorite state, provider, display title, recent-request
  description, directory, branch when known, availability, and relative age
- And selecting a row shows its immutable ID, exact timestamps, model, source
  size, match context when relevant, and available actions

- **Scenario: Build the first usable index**

- Given no application index exists
- When the application starts
- Then it discovers configured provider roots and publishes searchable session
  metadata before full transcript indexing completes
- And it keeps browsing and metadata search responsive while content progress is
  visible in Index status
- And closing and reopening the application resumes unfinished work without
  discarding already committed index data

- **Scenario: Preserve the last database when migration fails**

- Given an existing usable app database needs a schema migration
- When a migration step fails before validation and commit
- Then the migration transaction rolls back and the original database bytes and
  schema version remain usable
- And the application reports a local index migration failure without creating
  or modifying any provider-owned file

- **Scenario: Reconcile new, changed, and removed sources**

- Given provider roots have already completed one scan generation
- When a source is created, appended, replaced, archived, moved, or deleted
- Then watcher events may request prompt work, but a complete reconciliation is
  authoritative
- And new and changed sources are queued, stable provider identities survive a
  path or archive move, and unseen sources are removed only after a complete
  generation confirms their absence
- And an interrupted index resumes from committed complete-record offsets after
  restart without replaying committed chunks
- When the user invokes Rescan
- Then the application re-enumerates every provider root, reconciles new,
  deleted, changed, and failed sources, and refreshes provider metadata without
  clearing usable committed data

- **Scenario: Search session metadata**

- Given metadata has been indexed
- When the user enters a query containing one or more parsed atoms
- Then title, description, directory, branch, provider, model, and session ID are
  matched using the deterministic search contract below
- And every atom is required, while different atoms may match different indexed
  fields or transcript segments in the same owning session
- And exact title or directory matches rank ahead of title prefixes, other
  metadata classes, transcript-only matches, and older equal-quality matches
- And provider, favorite, and directory scopes restrict results without clearing
  the query

- **Scenario: Search transcript content**

- Given content indexing has completed for a session
- When the query contains unquoted terms or a balanced quoted phrase
- Then searchable user, assistant, command, path, error, tool-name, and textual
  tool-result content contributes matches
- And unquoted transcript terms support `unicode61` word-prefix matching while a
  quoted transcript phrase requires its normalized tokens in order
- And the result identifies the matching field or transcript excerpt
- And system instructions, encrypted reasoning, image payloads, file snapshots,
  telemetry, and duplicated metadata do not contribute matches

- **Scenario: Roll up a child-log match**

- Given a child log contains matching searchable text
- When the user searches for that text
- Then the independently resumable owning session appears as the result
- And details label the excerpt as a child-log match
- And the child log never appears as a separately resumable main-list row

- **Scenario: No complete search result yet**

- Given transcript indexing is incomplete or content search has failed
- When no fully indexed result matches the query
- Then the application retains metadata results and the query
- And it labels the result set Partial with indexed scope and progress
- And it does not claim that no matching session exists in unindexed content

- **Scenario: Inspect local performance telemetry**

- Given the application has started and at least one query has completed
- When the user opens Index status
- Then it shows process-start to first usable rows, first metadata-ready time,
  current and latest query duration, rolling metadata and transcript 95th
  percentiles, working-set samples, index bytes, and indexing progress
- And telemetry excludes query text, transcript text, raw provider roots, session
  IDs, and command values

- **Scenario: Resolve a display title and description**

- Given provider records contain different combinations of explicit session
  name, AI title, first included user-role text, and latest included user-role
  text
- When a session is normalized
- Then the latest provider-supplied explicit session name takes precedence over
  the latest provider AI title, normalized first included user-role text, and
  immutable ID in that order, regardless of record order across title types
- And the description uses the latest included user-role text record, removes
  provider control envelopes, collapses whitespace, and applies the documented
  180-scalar truncation algorithm
- And app-owned display overrides, if later present, affect display only and
  never replace the immutable resume ID

- **Scenario: Favorite a session or directory**

- Given a session row or its details are visible
- When the user toggles its session star
- Then the favorite is stored by provider and immutable session ID and appears in
  the Starred scope after restart and reindex
- When the user toggles the directory star
- Then the normalized directory appears as an independent quick filter
- And no session in that directory is implicitly starred

- **Scenario: A favorite write fails**

- Given the current favorite state is visible
- When an app-database write for a session or directory favorite fails
- Then the displayed star returns to its last committed state
- And a named status notice says the favorite was not saved
- And provider-owned data remains untouched

- **Scenario: A favorite source disappears**

- Given a starred session or directory is removed from provider storage or disk
- When reconciliation runs
- Then its favorite metadata remains visible as unavailable
- And transcript content is not retained or presented as a backup
- And removing the star removes the unavailable favorite record
- And a removed non-favorite session row and its transcript index are deleted at
  successful reconciliation

- **Scenario: Resume one ready session**

- Given the selected session is Ready and not active
- When the user invokes Open in terminal
- Then exactly one Windows Terminal tab starts in the recorded existing directory
- And the tab runs `claude --resume <session-id>` for Claude Code or
  `codex resume <session-id>` for Codex using structured process arguments
- And the application does not invoke a shell-interpolated command string

- **Scenario: Copy one resume command**

- Given the selected session is Ready and not active
- When the user invokes Copy command
- Then the clipboard receives an equivalent PowerShell command that selects the
  recorded directory and resumes by ID
- And spaces, apostrophes, Unicode, shell metacharacters, and provider IDs are
  quoted without changing their values
- And the confirmation identifies the session whose command was copied

- **Scenario: Prevent duplicate active resume**

- Given a provider activity marker or resume command line refers to the session
- And its PID, expected provider executable, and process-start fingerprint match
  a live process when the fingerprint is available
- When the user attempts to open that session
- Then the application labels it Active and does not launch or copy a duplicate
  resume command
- When a session-specific marker maps to a live expected provider executable but
  supplies no process-start fingerprint
- Then the application labels it Possibly active and blocks automatic Open and
  Copy until the process or marker clears
- When the marker PID is absent, stale, or belongs to a different executable
- Then the session is not blocked as Active
- When a live Claude process has no marker or command-line session ID
- Then Index status warns that some Claude activity could not be mapped, but the
  application does not guess which session is active

- **Scenario: Explain an unavailable session**

- Given a trusted session is archived, its directory is missing, its provider CLI
  is missing, its favorite source was removed, or its previously recognized
  record format is now unsupported
- When the session is shown
- Then it receives the distinct status Archived, Missing directory, Missing CLI,
  Source removed, or Unsupported format
- And Open in terminal is unavailable with a specific safe next action
- And Copy command is unavailable because the row is not Ready
- And the application does not unarchive, rewrite, move, or delete provider data

- **Scenario: Reject an unsafe provider or working path**

- Given a configured provider root, discovered source, or recorded directory is
  relative, UNC, device-namespaced, on non-fixed storage, crosses a reparse point,
  or resolves outside its trusted local root
- When discovery or availability evaluation encounters the path
- Then lexical checks reject it before `Directory.Exists`, enumeration, or any
  other network-capable filesystem probe
- And an unsafe provider root becomes a provider diagnostic while an otherwise
  trusted session receives Unsafe directory
- And the application never searches, indexes, launches, or copies a command for
  that path

- **Scenario: Encounter an unknown first-seen format**

- Given a first-seen record shape cannot provide both a recognized provider and
  a trusted immutable session ID
- When indexing encounters that source
- Then no guessed session row is created
- And Index status records the source, parser version, and non-sensitive reason
- And indexing continues for other sources

- **Scenario: Open several ready sessions**

- Given the user selects sessions across either provider
- When the selection changes
- Then the action bar reports ready, active or possibly active, duplicate, and
  other unavailable counts as four separate categories
- When the user invokes Open ready tabs
- Then identities are deduplicated by provider and immutable ID
- And one tab is launched for each Ready session in visible selection order
- And active, possibly active, archived, and unavailable sessions are skipped
  with named reasons
- And one tab failure does not cancel later ready tabs
- And the final notice reports opened, skipped, and failed counts

- **Scenario: Windows Terminal is unavailable or a tab launch fails**

- Given one or more selected sessions are Ready
- When `wt.exe` cannot be resolved
- Then no shell command is executed and Open actions explain that Windows
  Terminal is missing
- And Copy command or Copy commands remains available for the Ready sessions
- When an attempted tab launch returns an error
- Then that session is reported as failed, later Ready launches continue, and an
  exact copy action remains available

- **Scenario: Copy several commands**

- Given one or more sessions are selected
- When the user invokes Copy commands
- Then the clipboard receives one safely quoted PowerShell command per unique
  Ready session in visible selection order
- And active, possibly active, duplicate, archived, and unavailable rows are
  omitted and reported by named reason rather than receiving commented or
  non-runnable commands

- **Scenario: Update an appended transcript**

- Given a known source file has previously been indexed
- When the provider appends one or more complete JSONL records
- Then the application reads only new complete records, updates searchable text
  and recency transactionally, and leaves a partial trailing line for the next
  pass
- And a truncation, replacement, or parser-version change schedules safe
  reprocessing of that source rather than applying the old byte offset

- **Scenario: Bound a hostile or pathological source**

- Given a JSONL record exceeds 32 MiB, JSON nesting exceeds 64, extracted text in
  one record exceeds 8 MiB, a provider exposes more than 100,000 candidate source
  files, the app index reaches 64 GiB, or fixed-disk free space falls below 5 GiB
- When indexing reaches the applicable limit
- Then the affected record, provider scan, or content phase stops at a committed
  boundary with a named Partial diagnostic
- And prior committed search data remains usable, metadata reconciliation for
  safe sources continues where possible, and no unbounded record is materialized
- And a content pass snapshots each source length at pass start so continuous
  provider appends cannot make that pass infinite
- And searchable text is emitted in segments of at most 64 KiB UTF-8 bytes with
  cancellation checks between segments

- **Scenario: Purge content for a removed source**

- Given reconciliation confirms that a source was removed
- When its transcript segments are deleted
- Then the live SQLite database uses database and FTS secure deletion, checkpoints
  and truncates sidecars after the deletion transaction, and no removed sentinel
  remains in the closed database, WAL, or SHM files
- And failure to meet that database-level purge check schedules a protected clean
  rebuild rather than presenting the old index as purged
- And the guarantee does not claim forensic erasure from storage firmware,
  external backups, or a malicious process already running as the same user

- **Scenario: Isolate a source failure**

- Given one source file is malformed, temporarily locked, or uses an unknown
  record shape
- When indexing reaches it
- Then other sources continue indexing and remain searchable
- And Index status records the source path, provider, non-sensitive reason, and
  retry state without logging transcript bodies
- And the last usable committed data remains available until reconciliation
- And entering or editing the current query retries a failed content query

## Deterministic search and display contract

### Query parsing and normalization

1. Unicode whitespace at the query edges is removed. A remaining blank query is
   Browse mode and returns sessions by recency without a match class.
2. A balanced pair of straight double quotes creates one phrase atom. Text
   outside quotes is split into unquoted atoms on Unicode whitespace. An
   unmatched quote is discarded as punctuation and its remaining text is parsed
   as unquoted atoms. Empty atoms are ignored. Backslash has no escape meaning.
3. Metadata text and query atoms are normalized with Unicode Form KC, invariant
   uppercase casing, trimmed edges, and every whitespace run collapsed to one
   ASCII space. Each unquoted atom is a metadata substring. Each phrase atom is a
   metadata substring including its normalized internal spaces.
4. Transcript text uses SQLite FTS5 `unicode61 remove_diacritics 2`. Tokens from
   each unquoted atom are required and use a final-token prefix match. Tokens in
   a phrase atom must be contiguous and ordered. An atom that produces no
   transcript token may still match metadata.
5. Every atom must match at least one indexed metadata field or searchable
   transcript segment belonging to the same owning session. Different atoms may
   match different fields or segments. Provider and favorite scopes are applied
   before paging and do not alter the query.
6. A query is rejected before SQLite when it contains NUL, exceeds 512 Unicode
   scalars, exceeds 32 atoms, produces more than 128 transcript tokens, or would
   produce an FTS expression longer than 4,096 characters. The UI retains the
   query and names the exceeded limit. Generated FTS terms are individually
   double-quote escaped as literals; the application adds any final prefix
   operator outside the literal. User text never supplies FTS operators.

Representative parser fixtures:

| Query | Parsed atoms | Required behavior |
| --- | --- | --- |
| `tile error` | terms `tile`, `error` | Both terms are required, possibly in different fields |
| `"tile loading" error` | phrase `tile loading`, term `error` | Phrase tokens are contiguous; `error` is also required |
| `C:\repos\todo` | term `C:\repos\todo` | Full path is a metadata substring; transcript tokens are tokenizer-derived |
| `"unterminated phrase` | terms `unterminated`, `phrase` | The unmatched quote is discarded without a query error |
| `OR NEAR(foo) *` | terms `OR`, `NEAR(foo)`, `*` | Reserved-looking text remains literal and cannot alter FTS grammar |
| a query with NUL or 513 scalars | rejected | No SQLite query runs and the named limit remains visible |
| `   ` | none | Browse mode, newest activity first |

### Result ordering

For a nonblank query, each matching session receives the first applicable match
class below. Results sort by class ascending, then FTS5 `bm25` ascending for
class 7 only, last activity descending, provider order Claude Code then Codex,
and immutable session ID by ordinal ascending. This tuple is total and is also
used when a page is fetched again.

| Class | Condition |
| --- | --- |
| 0 | The normalized query text, with quote syntax removed and atoms joined by one space, exactly equals the title |
| 1 | The same normalized query text exactly equals the directory |
| 2 | The title starts with the same normalized query text |
| 3 | All atoms match metadata and at least one best atom match is in title |
| 4 | All atoms match metadata and no title atom matches, but a description atom matches |
| 5 | All atoms match metadata and only directory or lower-priority metadata matches remain, with a directory match present |
| 6 | All atoms match only branch, provider, model, or session ID metadata |
| 7 | At least one required atom needs transcript content, including a mixed metadata and transcript match |

Favorite state never changes ranking unless the Starred scope is selected.
Transcript snippets select the lowest `bm25` matching segment, then lowest
segment ordinal, and expose at most 240 normalized text scalars around the first
match. Child-log snippets are labeled with their child source.

### Title and description extraction

- Display title precedence is app override, latest provider-supplied explicit
  session name, latest provider AI title, first included user-role text, then
  immutable session ID. A later AI-title record never supersedes an existing
  explicit session name. Whitespace normalization follows the metadata rule
  above without changing display case.
- Included user-role text excludes records typed by the provider as system,
  developer, tool, summary, telemetry, or synthetic metadata. A versioned
  adapter may remove only recognized provider-generated control envelopes. It
  must not remove user-authored XML-like text merely because it resembles markup.
- Display projections collapse line breaks and tabs to spaces, replace C0, C1,
  bidirectional override, and bidirectional isolate controls with a visible
  replacement character, and enforce component line limits. Searchable source
  text remains unchanged. Trusted provider and status labels occupy separate
  controls so transcript text cannot impersonate them.
- The description is the latest included user-role text after envelope removal.
  If it contains at most 180 Unicode scalar values, it is unchanged. Otherwise,
  the app examines the first 177 scalars, keeps text through the last Unicode
  whitespace scalar within that range after trimming it, and appends three
  periods. If no Unicode whitespace scalar exists in that range, the first 177
  scalars plus three periods are used.

## Availability and action contract

A main-list row requires a recognized provider plus trusted immutable session
ID. Ready means the provider source, provider CLI, and recorded directory are
available and the session is not archived, active, or possibly active. Status
precedence is Unsupported format, Source removed, Archived, Active, Possibly
active, Unsafe directory, Missing directory, Missing CLI, then Ready.

| Session status | Open one or batch | Copy one or batch | Safe next action |
| --- | --- | --- | --- |
| Ready | Enabled when Windows Terminal resolves | Enabled | Open or paste the exact command |
| Active | Blocked and reported | Blocked and reported | Return to the already active provider session |
| Possibly active | Blocked and reported | Blocked and reported | Check the provider process, then close it or wait for its marker to clear |
| Unsafe directory | Blocked without probing the path | Blocked and reported | Move or restore the project to a canonical local fixed-drive path |
| Archived | Blocked and reported | Blocked and reported | Unarchive with the provider, then Rescan |
| Missing directory | Blocked and reported | Blocked and reported | Restore the recorded directory, then Rescan |
| Missing CLI | Blocked and reported | Blocked and reported | Install or expose the provider CLI, then Rescan |
| Source removed | Blocked and reported | Blocked and reported | Remove the stale favorite or restore provider storage |
| Unsupported format | Blocked and reported | Blocked and reported | Update the app or provider adapter, then Rescan |

If Windows Terminal cannot be resolved, Ready rows remain Ready, Open is
disabled with a named reason, and Copy remains enabled. A failed `wt.exe` start
or nonzero tab-launch result does not invoke PowerShell. It reports the failure
and leaves Copy enabled.

Executable resolution occurs before applying the recorded working directory.
The prototype accepts only an absolute canonical local `.exe` with the exact
expected filename and expected Authenticode publisher, or the verified Microsoft
Windows Terminal app-execution alias backed by a registered signed package. It
never searches the session directory, accepts an empty or relative PATH entry,
runs `.cmd`, `.bat`, or PowerShell shims, or enables shell execution. Provider
session IDs must parse as UUIDs before either direct launch or command copy.
Directory and executable identity are revalidated immediately before dispatch.

Open targets `wt.exe -w 0`, which reuses the most recently used Windows Terminal
window and creates one when none exists. Each Ready session receives one
`new-tab` command in visible selection order with its recorded directory and
provider executable passed as structured arguments. Copy emits one line per
deduplicated Ready identity. All other rows are omitted and counted by status;
commented pseudo-commands are never placed on the clipboard.

Enter and a double-click on non-interactive row space invoke Open for one Ready
focused row. `Ctrl+Shift+C` invokes Copy for the current selection. Standard
Ctrl-click and Shift-click extend result selection. These gestures never bypass
the status matrix.

Clipboard writes request exclusion from Windows clipboard history and cloud
clipboard using the documented registered formats. This is best-effort privacy,
not protection from another process already running as the same user. Copied
commands are never persisted in diagnostics.

Reconciliation retains a removed session row only while it is a session
favorite, then deletes its indexed transcript. A directory favorite persists
independently and may show a missing-path label. A previously recognized source
that changes to an unsupported shape keeps its last trusted metadata and becomes
Unsupported format while the source exists. A first-seen unknown shape with no
trusted identity is diagnostic-only.

## Benchmark protocol

- The target corpus is the configured real local corpus at measurement time. Its
  database lives only in a random current-user-protected directory below
  `%LOCALAPPDATA%\SessionSearch\Benchmarks`, never in the repository, and is
  removed after the sanitized report is committed.
- A report records a random run ID, operating-system version, app and parser
  versions, provider CLI versions, aggregate file counts, source bytes, index
  bytes, query category IDs, and timings. It excludes machine identifiers, raw
  roots, query text, transcript text, titles, descriptions, paths, session IDs,
  snippets, and commands. Per-run salted root pseudonyms may distinguish roots
  inside one report but cannot be correlated across reports.
- Existing-index startup begins immediately before process creation and ends
  when the search control accepts input and the first 50 recent rows have been
  supplied to the virtual list. One discarded warm-up is followed by 10 launches
  against a complete unchanged index; AC-2 uses the 95th percentile.
- First-run metadata timing begins immediately before process creation with an
  empty app-owned database and ends when at least the newest 50 trusted sessions,
  including both providers when present, are persisted and searchable while
  content indexing remains active. The real-corpus run must meet 5 seconds.
- Query timing measures the search service from dispatch of an already parsed
  query through return of the first 50 result models. The versioned query set has
  at least 20 cases covering blank, provider, title, ID, directory, multiple
  terms, phrase, child content, no match, and common transcript terms. Each query
  receives five warm-ups and 30 measured repetitions against an unchanged full
  index. AC-3 uses the 95th percentile of all measured calls for metadata-only
  and transcript query groups separately.
- Steady-state memory is the maximum `Process.WorkingSet64` of three samples five
  seconds apart, beginning 30 seconds after full indexing finishes with the app
  idle and the search database page cache capped by configuration. Operating
  system file-cache processes are not included.
- UI responsiveness is measured during a bounded synthetic indexing workload by
  posting a UI-thread probe every 25 ms. No probe may take more than 100 ms from
  post to execution. The benchmark records median, 95th percentile, and maximum.
- Saved screenshots and UI Automation captures use only sanitized synthetic
  fixtures. A repository and release scan rejects SQLite headers, WAL and SHM
  files, JSONL transcripts, known real-root strings, and transcript canaries from
  `artifacts` and package output.

## Acceptance criteria

- [ ] AC-1: On the target machine and synthetic fixtures, provider discovery
  honors `CODEX_HOME` and `CLAUDE_CONFIG_DIR` overrides, otherwise uses standard
  user locations, and emits one normalized main result per top-level resumable
  session while mapping child logs to their owner.
- [ ] AC-2: With a warm existing index over the target corpus, an automated
  startup measurement reaches a usable recent-result list within 500 ms, and a
  first-run measurement reaches searchable metadata within 5 seconds while
  content indexing continues. Index status displays both measured milestones
  without exposing sensitive values.
- [ ] AC-3: A benchmark over the target corpus records metadata query latency at
  or below 50 ms at the 95th percentile and indexed transcript first-page
  latency at or below 150 ms at the 95th percentile, while Index status displays
  the current duration and rolling group percentiles.
- [ ] AC-4: Search tests prove case-insensitive metadata substring matching,
  all-atom AND semantics, `unicode61` transcript prefix matching, balanced and
  unmatched quote behavior, phrase matching, scope preservation, the complete
  rank tuple and stable tie-breaker, snippet selection, reserved-operator
  escaping, every numeric query limit, and Partial labeling while content is
  incomplete using every representative query fixture.
- [ ] AC-5: A fixture whose only match is in a Claude or Codex child log returns
  the owning top-level session with a child-log excerpt and never exposes the
  child as a resumable row.
- [ ] AC-6: Title and description fixtures prove documented precedence, typed
  record exclusion, recognized-envelope removal without deleting user-authored
  markup, whitespace cleanup, exact 180-scalar boundary behavior, display-safe
  control projection, and immutable-ID fallback without any network request.
- [ ] AC-7: Session and directory favorites persist across restart, reindex, path
  case differences, and source removal; directory favorites never alter session
  favorite state, an injected save failure restores the committed star, a
  removed favorite keeps metadata only, and a removed non-favorite row and its
  transcript index disappear after reconciliation.
- [ ] AC-8: Active-state tests validate provider marker, PID, and process-start
  identity. A verified active session blocks launch and command copy, a live
  expected provider process with missing start evidence becomes Possibly active
  and blocks, a stale or different-executable PID does not block, a reused PID
  is rejected by start mismatch, a held Codex child lock rolls up to its owner,
  and unmapped Claude activity produces only the global warning.
- [ ] AC-9: Single-launch tests capture process creation and prove exactly one
  Windows Terminal tab targets `-w 0`, the recorded existing starting directory,
  the correct provider executable, and the immutable resume ID are passed as
  structured arguments without shell interpolation. A benign real Terminal
  integration check opens exactly one uniquely titled tab, verifies its actual
  working directory through a sentinel and UI Automation, and closes only that
  tab without running a provider resume command.
- [ ] AC-10: PowerShell command tests round-trip directories and IDs containing
  spaces, apostrophes, Unicode, and shell metacharacters for both providers, and
  the displayed command equals the clipboard command. Only Ready rows produce a
  command; missing Windows Terminal and an injected launch failure preserve the
  exact Ready-row copy action. The clipboard adapter also publishes the
  best-effort history and cloud exclusion formats and never logs copied text.
- [ ] AC-11: Batch tests with mixed providers, duplicate identities, active rows,
  possibly active rows, unavailable rows, and an injected tab failure open every
  unique Ready row once in visible order, skip blocked rows, continue after
  failure, and report exact opened, skipped, and failed totals. Batch copy emits
  exactly one command line per unique Ready row in the same order, omits blocked
  rows, and reports their named counts without pseudo-commands.
- [ ] AC-12: Availability fixtures produce the exact Ready, Active, Archived,
  Possibly active, Unsafe directory, Missing directory, Missing CLI, Source
  removed, and Unsupported format states, precedence and action matrix. A
  trusted previously recognized unsupported row remains visible, an unidentified
  first-seen unknown shape is diagnostic-only, and no unavailable action mutates
  provider data.
- [ ] AC-13: Incremental-index tests prove append-only offset reuse, partial-line
  deferral, truncation detection, source replacement detection, new-source
  discovery, archive moves without duplicate identity, deletion after a complete
  scan generation, watcher-triggered work, authoritative Rescan reconciliation,
  interrupted-index restart from committed offsets, transactional visibility,
  every parser and storage limit, finite pass length, and continued progress
  after a corrupt, oversized, or locked record or file.
- [ ] AC-14: Hashes and timestamps for every provider-owned source used by an
  acceptance corpus are unchanged after indexing, searching, favoriting,
  command copying, and simulated launch generation. This includes a Codex state
  database plus existing WAL and SHM sidecars opened with explicit SQLite
  read-only and query-only settings.
- [ ] AC-15: A steady-state measurement after indexing reports at most 100 MB
  working set using the benchmark protocol, and an injected background indexing
  workload produces no UI-thread probe over 100 ms.
- [ ] AC-16: Automated accessibility inspection reaches search, scopes, result
  rows, both favorite controls, details, index status, copy, and launch actions
  by keyboard; each exposes a programmatic name and visible focus, and provider
  or availability meaning is not color-only. Keyboard tests cover Enter,
  double-click equivalence, `Ctrl+Shift+C`, Ctrl and Shift selection, overlay
  dismissal with focus return, and an accessible announcement for status change.
  Preference-injection and offscreen layout checks also prove high-contrast
  token replacement, reduced-motion suppression, responsive pane collapse, and
  usable 200 percent scaling without changing global Windows settings.
- [ ] AC-17: Retry and failure-isolation tests keep the last committed search data
  usable; Rescan re-enumerates roots and reconciles new, deleted, changed, and
  failed sources; a fresh query retries content search; and diagnostics record
  provider-root aliases or relative paths, parser versions, retry states, and
  non-sensitive reasons without transcript bodies or raw queries.
- [ ] AC-18: Hostile-path and executable tests reject relative, UNC, device,
  non-fixed, root-escaping, and reparse paths before network-capable probes;
  reject current-directory, hostile-PATH, script-shim, wrong-name, unsigned, and
  wrong-publisher executables; require UUID IDs; and revalidate safe local paths
  and signed absolute executables immediately before dispatch.
- [ ] AC-19: Storage security tests prove a protected current-user DACL for the
  app root, database, WAL, SHM, diagnostics, temporary and benchmark files;
  injected migration failure preserves the original usable database; secure
  deletion removes a sentinel from the closed live database and sidecars; and
  real-corpus reports, screenshots, artifacts, and release output pass the
  sensitive-evidence policy.
- [ ] AC-20: The release gate rejects a C# multiline string that is not raw,
  restores only through the approved locked NuGet source policy with no High or
  Critical audit finding, and verifies SQLite FTS5, application ID, expected
  schema objects, trusted-schema-off, extension-disabled, memory-map-off,
  integrity, and reduced runtime limits before the index is used.

## Known sharp edges, prototype

- Both providers may change internal record and metadata schemas without a
  compatibility guarantee. Adapters must reject unknown shapes visibly.
- Multi-gigabyte first indexing can create a large local database and expose
  storage, antivirus, and thermal bottlenecks that synthetic fixtures miss.
- Active markers can survive crashes; process identity checks must account for
  PID reuse and provider versions that omit process-start data.
- Provider retention can remove an old favorite. The application preserves only
  favorite metadata and availability, not a hidden transcript archive.
- Some historical or imported Codex threads may not have an explicit modern
  parent or source classification and require conservative top-level handling.
- Concurrent transcript writes can end with an incomplete JSON line or replace
  a file between metadata read and content read.

## Edge cases and errors

Whole-database disaster recovery, provider-data repair, cross-machine favorites,
and automatic installation of missing CLIs are deferred to MVP promotion.

## Non-functional requirements

Inherited from `product-global.md`. The feature benchmark protocol defines the
prototype measurement boundary, including the 100 ms UI-thread probe target.

## Open questions

- None for the prototype.

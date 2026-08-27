# Problem Brief - local-ai-session-search

## Problem statement

A Windows developer who uses Claude Code and Codex across many repositories
cannot quickly recall which session contains a past decision, error, or task.
Provider pickers are separated by tool and working directory, while raw local
transcripts are too large and numerous to inspect directly. This causes repeated
work and lost time reconstructing context. A solution should make current local
sessions instantly recognizable, searchable, and resumable without uploading or
mutating transcript data.

## Target users

- **Primary:** a single Windows power user working across many local projects
  with both Claude Code and Codex.
- **Secondary:** the same user returning days or weeks later with only a phrase,
  file name, directory, or rough recency in mind.

## Jobs to be done

- See the most recently active sessions across both providers in one place.
- Find a session by title, directory, branch, prompt text, tool output, error,
  or other remembered transcript content.
- Recognize the session from a concise title and recent-request description.
- Star important sessions and directories for durable quick access.
- Resume one session, or a mixed selection of sessions, in Windows Terminal
  tabs rooted in their recorded directories.
- Copy an exact PowerShell resume command whenever direct launch is unavailable
  or not desired.

## Success signals

- The current local corpus is represented without top-level subagent clutter,
  and child-log content can still lead to its owning session.
- Recent sessions and metadata search are usable within the product-wide startup
  and latency targets before full transcript indexing finishes.
- A known phrase from an old user message or tool result finds the expected
  session after content indexing.
- Stars survive reindexing, and starring a directory creates a quick filter
  without implicitly starring every session in it.
- Single and multi-session actions produce correctly quoted, provider-specific
  resume commands and never duplicate a selected session.
- The application does not modify any Claude Code or Codex source artifact
  during indexing, searching, favoriting, or command generation.

## Evidence from the target machine

- On 2026-08-26, a point-in-time scan found 2,956 Claude top-level candidates,
  of which 2,944 contained substantive user or assistant records, plus 3,920
  child-agent JSONLs. It separately excluded 296 workflow journals and 3,920
  sidecar JSON files. Codex contained 173 top-level and 35 child rollout files.
  The combined searchable source data was approximately 8 GB.
- Claude records expose working directory, branch, timestamps, AI title, last
  prompt, model, and session identity. Codex metadata exposes title, preview,
  directory, recency, model, branch, archive state, and thread identity.
- Claude Code 2.1.247 and Codex CLI 0.149.1 both support direct resume by session
  ID. Windows Terminal and PowerShell 7 are installed.
- Both providers expose local activity markers that can be validated against a
  live process before labeling a session active.
- Claude custom titles can be followed by newer AI-title records, so the latest
  custom title must retain precedence. Codex child rollouts can clone parent
  metadata, so their filename UUID must select the matching `session_meta`.

### Evidence provenance

| Evidence | Collection method | Reproducible artifact or check |
| --- | --- | --- |
| Claude top-level and child counts | Enumerated `*.jsonl` under the configured Claude project root and classified nested `subagents` paths separately | Provider discovery acceptance fixture plus a real-corpus inventory command recorded by the benchmark harness |
| Codex thread count and metadata columns | Opened the local Codex state database read-only, inspected `threads` and `thread_spawn_edges`, and cross-checked rollout files | Schema-capability probe in the Codex adapter and captured fixture databases in `tests/Fixtures/Codex` |
| Approximate 8 GB corpus size and largest-file risk | Summed provider JSONL lengths without opening files for write | Real-corpus benchmark report records total bytes, file count, and largest source |
| Resume syntax | Captured local `claude --help` and `codex resume --help` output for the installed versions | Launch argument acceptance tests for both providers, plus a manual no-op command review |
| Active markers | Inspected Claude activity JSON and Codex writer-lock files, then compared stored identities with live processes | Synthetic live, stale, and PID-reuse fixtures in active-state tests |
| Source fields | Streamed representative records read-only and inspected the Codex state schema read-only | Versioned provider fixtures with expected normalized sessions and excluded content |

Counts are a dated target-machine snapshot, not a stable provider contract. The
benchmark harness must recollect them at test time and record the configured
roots, CLI versions, parser versions, counts, and byte totals without recording
transcript bodies.

### Prototype assumptions to validate

- Independent session and directory favorites are a workflow hypothesis based
  on the user's stated wishlist. Manual success means a starred session and a
  separately starred directory both remain one keyboard action away after an
  application restart, without changing each other's state.
- Mixed-provider batch restore is a workflow hypothesis based on the user's
  stated wishlist. Manual success means a chosen Claude Code and Codex session
  open as separate Windows Terminal tabs in their recorded directories, while a
  blocked row is named and skipped.
- These assumptions do not require an external user study for the single-user
  prototype. They remain candidates for revalidation before broader release.

## Constraints

- Provider transcript formats are internal and version-dependent.
- Source files may be large, concurrently appended, locked, malformed, moved,
  archived, or removed by provider retention rules.
- Initial content indexing must process gigabytes without delaying metadata
  search or making the UI unresponsive.
- Search results may contain sensitive transcript content and must remain local.
- Commands must safely handle spaces, apostrophes, Unicode, and missing paths.

## Explicitly out of scope

- Searching cloud-only Claude or Codex histories.
- AI-generated semantic embeddings or network summarization in the prototype.
- Backing up transcripts or overriding provider retention.
- Editing provider session names, archive state, or transcript contents.
- Cross-platform packaging, team sharing, and synchronized favorites.

## Open questions

- The feature plan selected .NET 10 WinForms with direct SQLite FTS5 after
  comparing startup, memory, accessibility, and development risk. No product
  behavior question blocks the prototype.

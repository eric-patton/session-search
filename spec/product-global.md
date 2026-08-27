# Product-global - Local AI Session Search

## Vision

The product is a local session navigator for Windows. It should feel as immediate
as a file finder: open it, type a few characters, recognize the right session,
and continue working.

## Glossary

- **Session** - One resumable top-level Claude Code or Codex conversation.
- **Provider** - The CLI that owns a session, currently Claude Code or Codex.
- **Directory** - The working directory recorded by the provider for a session.
- **Child log** - A subagent or internal transcript associated with a top-level
  session but not independently resumable by the user.
- **Favorite** - An app-owned star on a session or directory.
- **Resumable** - A session whose provider data, CLI, and required working
  directory are available for a supported resume action.
- **Active session** - A session whose provider marker and operating-system
  process identity indicate that it is currently open.
- **Possibly active session** - A session-specific activity marker maps to a live
  provider process but lacks the process-start evidence needed to exclude PID
  reuse. It is blocked from automatic duplicate resume and labeled separately.
- **Unsafe directory** - A recorded path is remote, device-namespaced, relative,
  on non-fixed storage, or crosses a reparse point. The prototype never probes or
  launches it.

## Global non-functional requirements

- Performance: with an existing index, the first usable result list appears
  within 500 ms of process start on the target workstation. Metadata searches
  update within 50 ms at the 95th percentile, and indexed transcript searches
  return their first page within 150 ms at the 95th percentile. First launch
  presents a usable metadata result set within 5 seconds while content indexing
  continues in the background.
- Resource use: steady-state working set is at most 100 MB after indexing,
  excluding operating-system file cache. The UI remains responsive throughout
  indexing and exposes index size and progress.
- Security: source storage is opened read-only, process arguments are passed as
  structured arguments rather than interpolated shell text, and no application
  feature requires elevated privileges. Provider roots, source paths, working
  directories, and executables pass the local-path trust policy before any
  filesystem probe or launch.
- Accessibility: every primary action is keyboard reachable, focus is visible,
  status is never communicated by color alone, screen-reader names are present,
  and the layout remains usable at 200 percent scaling.
- Reliability and availability: one corrupt or locked source file cannot stop
  other sessions from being searchable. Index updates are transactional and a
  failed migration preserves the last usable database.
- Privacy and data handling: transcript and index content stays on the local
  machine. The app-data root has a protected current-user DACL, including every
  database sidecar, diagnostic, temporary index, and benchmark database.
  Diagnostic logging excludes transcript bodies and raw queries by default.

## Product invariants

- Provider-owned session files and databases are never modified.
- Favorites, directory pins, optional display-name overrides, and index state
  live only in the application database.
- The main result list shows every trusted top-level session identity, including
  unavailable rows. A trusted identity requires a recognized provider and an
  immutable session ID. Child logs may contribute search matches, but their
  matches roll up to the owning session.
- Every Ready session has an equivalent copyable PowerShell command. Active and
  unavailable sessions never receive a command that pretends they are runnable.
- A batch launch deduplicates session identities and never silently opens a
  session that is active or possibly active.
- Missing tools, missing directories, archived sessions, previously recognized
  unsupported records, and favorite sessions whose source disappeared remain
  visible with an explicit unavailable reason. A first-seen unknown record with
  no trusted identity appears only in Index status.
- Removed-source transcript text is physically purged from the live app database,
  FTS storage, and sidecars to the defined database-level verification boundary.
  This does not promise forensic erasure from SSD firmware, backups, or other
  same-user processes.

## Cross-cutting constraints

- Discover Codex storage from `CODEX_HOME` when set, otherwise the standard
  user location. Discover Claude Code storage from `CLAUDE_CONFIG_DIR` when set,
  otherwise the standard user location.
- The prototype accepts only canonical absolute paths on a local fixed drive.
  It rejects UNC and device paths, skips reparse-point traversal, validates final
  handle paths inside the trusted root, and never probes a rejected path.
- Claude Code and Codex storage schemas are internal and may change across CLI
  versions. Unknown shapes are skipped with diagnostics and never guessed.
- Resume by immutable session ID. Use the recorded working directory as the
  terminal starting directory only when it passes the local-path trust policy and
  still exists.
- Resolve Windows Terminal and provider CLIs to validated absolute executables
  before applying a session directory. Never search the session directory, run a
  script shim, or fall back to a shell.
- Windows Terminal is the preferred launcher. A Ready session retains its
  PowerShell command-copy action when Windows Terminal is missing or launch
  fails.
- The product targets the current user's Windows 11 workstation and does not
  require administrator access.

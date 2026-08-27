# Delta - Continuous results and resume defaults

> The change expressed against the current feature spec as explicit operations.

## ADDED

- The result list has a logical row for every result in the active query. Blank
  Browse mode and nonblank search both expose the complete total through one
  continuous scrollbar. The application requests fixed-size pages on demand,
  coalesces duplicate page requests, and ignores completed work from an older
  query generation. An unloaded row is a temporary non-actionable loading row,
  not a missing result.
- An indexing or availability refresh keeps the user's query, selected session
  identities, focused session identity, details, and visible anchor stable.
  Rows that no longer exist are removed from selection individually. Refreshing
  loaded data must not clear or rebuild the visible list, move keyboard focus,
  or scroll the user to the top. An intentional query, provider scope, favorite
  directory filter, or Starred scope change starts a new result generation at
  the first row.
- The published executable and main window use a bundled multi-resolution icon
  with useful 16, 32, 48, and 256 pixel representations for Windows Explorer,
  Windows Terminal launch surfaces, and taskbar pinning.
- Provider argument construction has one shared implementation used by both
  structured launch plans and PowerShell command formatting. Claude Code uses
  `--dangerously-skip-permissions --resume <session-id>`. Codex uses
  `--yolo resume <session-id>`.

## MODIFIED

- **Paged query presentation**
  - Was: The query service returned at most 50 result models per page and the
    user navigated pages through Previous and Next controls.
  - Now: The query service still returns bounded 50-row pages, but the virtual
    result surface maps those pages into one continuously scrollable result set
    whose size equals `TotalCount`. Previous and Next controls are removed.
- **Background refresh behavior**
  - Was: Metadata-ready and completed indexing refreshes could replace the
    current 50-row page and clear selection, focus, and scroll position.
  - Now: Background work refreshes loaded pages in place and preserves session
    identity based interaction state. Stale async completions cannot replace a
    newer query.
- **AC-9 structured resume arguments**
  - Was: Claude Code used `--resume <session-id>` and Codex used
    `resume <session-id>`.
  - Now: Claude Code uses `--dangerously-skip-permissions --resume
    <session-id>` and Codex uses `--yolo resume <session-id>`, with each token
    passed through `ProcessStartInfo.ArgumentList` after the verified provider
    executable.
- **AC-10 copied command equivalence**
  - Was: Copied commands contained the provider's resume verb and immutable ID.
  - Now: Copied commands contain the same provider permission-mode arguments,
    resume verb, and immutable ID as direct launch, with identical ordering and
    literal quoting.

## REMOVED

- Manual Previous and Next result-page controls.

# Design - Local AI Session Search

## Subject, audience, and single job

This is a local session navigator for one Windows power user who works across
many repositories with Claude Code and Codex. Its single job is to return the
user to the right session within seconds.

## Aesthetic direction

The interface takes its visual language from a flight recorder and an engineer's
activity log. It is dense, calm, and precise. Search and recency carry the visual
weight; there are no dashboard cards, decorative charts, gradients, or splash
screen. The memorable element is a **recency spine**: a narrow fixed column that
groups results by last activity and pairs an age label with a provider-shaped
tick. Text labels always duplicate color meaning.

### Visual tokens

- **Frost `#F3F6F8`:** primary canvas in light mode.
- **Ink `#17232D`:** primary text and strong rules.
- **Smoke `#4F616D`:** secondary text and inactive metadata.
- **Signal blue `#1E64A8`:** focus, selection, and primary actions.
- **Codex teal `#147A74`:** Codex provider cue.
- **Claude copper `#8E482A`:** Claude Code provider cue.

Windows high-contrast colors replace these tokens when high-contrast mode is
active. Dark mode uses system-derived surfaces while preserving the provider
distinction and contrast ratios. Every normal-size text token above is at least
4.5:1 on Frost. Provider text remains explicit even when provider colors appear.

### Typography

- **Segoe UI Variable:** titles, descriptions, controls, and body text.
- **Bahnschrift SemiCondensed:** compact recency and provider labels.
- **Cascadia Mono:** directories, branches, session IDs, and commands.

### Layout concept

The window opens without a splash screen and places keyboard focus in search.
The favorite-directory rail and details pane can collapse, leaving the result
list as the dominant surface.

```text
+--------------------------------------------------------------------------+
| Search sessions, directories, prompts, errors...      Claude Codex  Index |
+---------------+------------------------------------------+---------------+
| RECENT        | AGE  SESSION                     DIRECTORY| DETAILS       |
| STARRED       | now  Claude  Fix tile loading    loom... | title         |
| DIRECTORIES   | 18m  Codex   Review drag input   todo    | last request  |
|  loom         | 2h   Claude  Package promotion   Reviews | path, branch  |
|  shelf        | 1d   Codex   Browser contrast    ursin   | match, model  |
|               |                                          | command       |
+---------------+------------------------------------------+---------------+
| 3 selected                         Open 3 tabs  Copy commands             |
+--------------------------------------------------------------------------+
```

At narrow widths, the favorite rail collapses to a toolbar button and details
open as a right-side overlay. The selection action bar appears only when one or
more result rows are selected.

### Design self-review

A standard three-pane productivity layout would feel generic and spend too much
space on navigation. The revision makes both side panes optional, removes
summary cards, and uses the recency spine as the one distinctive device. It is
specific to reconstructing a work timeline and improves scanning, rather than
acting as decoration. Motion is limited to a 120 ms details transition and an
index-progress indicator, both disabled when reduced motion is requested.

## Key screens and states

- **Session navigator:** search, filters, favorites, recency spine, virtualized
  results, selection actions, and optional details.
  - States: cached results, first-run metadata scan, background content indexing,
    searching, no results, no sessions, and partial provider failure.
- **Session details:** recognition metadata and the exact available actions for
  one selected session.
  - States: ready, active, possibly active, archived, missing directory, missing
    provider CLI, unsafe directory, unsupported format, source removed,
    transcript match selected.
- **Index status:** compact progress and diagnostics reached from the header.
  - States: current, metadata scan, content indexing, watching, partial, and
    recoverable source error, storage limited, and migration failure.

## Primary flows

### Story: Browse the latest work

1. Launch the application -> search receives focus -> cached sessions appear
   sorted by last activity.
2. Scan the recency spine and result rows -> recognize provider, title, recent
   request, directory, and age without opening details.
3. Select a row -> details show timestamps, branch, model, size, session ID,
   status, match context, and available actions.

### Story: Find a remembered session

1. Type in search -> metadata matches update immediately -> transcript matches
   join when the content query completes.
2. Apply provider, favorite, or directory scope if needed -> results retain the
   query and rerank within that scope.
3. Select a result -> the matching field or transcript excerpt is highlighted
   in details without showing raw system instructions or hidden binary content.

### Story: Favorite sessions and directories

1. Invoke the session star on a row or details pane -> the star fills and the
   session enters the Starred view without changing provider data.
2. Invoke the directory star beside a path -> the directory enters the rail as
   a quick filter without starring its sessions.
3. Invoke either star again -> remove only that app-owned favorite.

### Story: Resume or copy one session

1. Select a ready session and press Enter, double-click its non-interactive row
   area, or invoke Open in terminal -> a Windows Terminal tab starts in the
   recorded directory and runs the provider's resume-by-ID command.
2. Invoke Copy command or press Ctrl+Shift+C -> the equivalent safely quoted
   PowerShell command enters the clipboard and a confirmation names the session.
3. If the session is active or unavailable -> opening is blocked and details
   explain the exact reason and the safe next action.

### Story: Open several sessions as tabs

1. Use standard Ctrl-click or Shift-click selection -> a persistent action bar
   states four categories: Ready, active or possibly active, duplicate, and
   other unavailable.
2. Invoke Open ready tabs -> deduplicate identities -> send one `-w 0 new-tab`
   action per Ready session to the most recently used Windows Terminal window,
   or create a window when none exists.
3. Review the completion notice -> see opened, skipped, and failed counts -> keep
   exact Copy commands available for any Ready launch failures.

### Story: Keep the index current

1. Start on an empty app database -> metadata results appear first -> content
   progress continues without blocking browsing or search.
2. Append or create a provider transcript -> the watcher schedules an incremental
   update -> the session's recency and searchable text advance transactionally.
3. Open Index status -> inspect source roots, counts, progress, index size, last
   update, startup milestones, current and rolling query time, working set,
   skipped files, and parser-version diagnostics.
4. Invoke Rescan -> every provider root is re-enumerated -> new, deleted,
   changed, and failed sources reconcile without clearing committed results.

## Empty and error states

- **Browse latest work**
  - Empty: show the discovered Claude Code and Codex roots, a Rescan action, and
    a short explanation when neither root contains a resumable session.
  - Error: keep any cached results visible, identify the failed provider, and
    offer Rescan rather than replacing the window with an error page.
- **Find a remembered session**
  - Empty: retain the query and scopes, state which content is fully indexed,
    and offer Clear filters. Do not suggest that no session exists while content
    indexing is incomplete.
  - Error: return metadata matches when content search fails, label results
    Partial, and retry content search when the query is entered or edited again.
    An oversized, token-heavy, or NUL-containing query remains visible with the
    exact limit and does not run.
- **Favorite sessions and directories**
  - Empty: the Starred view explains how to star a session or directory and
    keeps Recent one keyboard action away.
  - Error: revert the visual star, preserve the previous stored state, and show
    `Favorite was not saved`; invoking the same control retries the write.
- **Resume or copy one session**
  - Empty: with no selection, details say `Select a session to inspect or open`.
  - Error: missing CLI, missing directory, active, archived, removed source, and
    unsupported format each receive a distinct status and safe action. An unsafe
    directory is rejected before probing and never gets Open or Copy. Missing
    Windows Terminal and terminal-launch failure leave Copy available for Ready
    rows.
    Launch failure never falls back to an interpolated shell command.
- **Open several sessions as tabs**
  - Empty: hide the action bar when no rows are selected.
  - Error: exclude active, duplicate, archived, and unavailable rows before
    launch; report each skip. A failure for one tab does not cancel later tabs.
- **Keep the index current**
  - Empty: first-run progress identifies which provider is being scanned and
    makes already discovered metadata searchable.
  - Error: isolate malformed or locked files, continue other work, retain the
    last usable index, and expose Rescan for full provider-root reconciliation.

## Components

- **Search field:** dominant input with clear button, result count, and current
  scope summary. It never waits synchronously on content indexing.
- **Scope chips:** Claude, Codex, Starred, and content-search state, each with a
  text label and keyboard access.
- **Favorite rail:** Recent, Starred sessions, and independently starred
  directories with counts and missing-path status.
- **Recency spine:** date buckets, relative age, provider tick, and explicit
  provider text available to assistive technology.
- **Session result list:** virtualized multi-select rows with star, provider,
  title, recent-request description, directory, branch, status, and age.
- **Session details:** metadata, match excerpt, exact command preview, Open in
  terminal, Copy command, and favorite controls.
- **Selection action bar:** Ready, active or possibly active, duplicate, and
  other unavailable counts, plus Open ready tabs, Clear selection, and Copy
  commands.
- **Index status:** non-blocking progress, source health, startup and query
  timings, working set, index bytes, sanitized diagnostics, storage limits, and
  Rescan.
- **Status notice:** brief confirmation or actionable failure with no vague
  `Something went wrong` message.

## Interaction assumptions and decisions

- Blank search shows all top-level sessions by last activity, newest first.
- Search ranking favors exact title and directory matches, then title prefixes,
  field-specific metadata classes, transcript relevance, and the stable recency,
  provider, and session-ID tie-breakers from the specification. Favorite state
  is a scope, not an invisible ranking boost.
- The latest provider-supplied explicit session name takes precedence over the
  latest provider AI title regardless of record order across those types. First
  included user-role text and session ID are the remaining fallbacks. The
  description is the latest included user-role text after typed system,
  developer, tool, summary, telemetry, and synthetic metadata exclusions. Text
  over 180 scalars keeps content through the last Unicode whitespace scalar in
  the first 177 and appends three periods, or hard-cuts at 177 when none exists.
- User and assistant text, tool names, commands, paths, errors, and textual tool
  results are searchable. System instructions, encrypted reasoning, image data,
  file snapshots, telemetry, and duplicated metadata are excluded.
- Active status requires a session-specific provider marker or resume command
  line plus matching PID, expected provider executable, and process-start
  fingerprint when available. A live expected provider process whose matching
  marker omits the start fingerprint is Possibly active and remains blocked.
  Stale or different-executable markers do not block. Unmapped Claude processes
  produce a global warning without guessing a session.
- Archived sessions remain searchable but are not launched or unarchived by the
  application. Details tell the user to unarchive them in the provider first.
- A missing source keeps app-owned favorite metadata visible as unavailable,
  but the content index is not treated as a transcript backup. A removed
  non-favorite row disappears after successful reconciliation.
- Command generation uses immutable provider ID and the recorded working
  directory. Display names never become command identifiers.
- The application follows Windows light, dark, high-contrast, scaling, reduced
  motion, and keyboard settings.

## Keyboard, focus, and announcements

| Surface | Keyboard behavior | Focus result |
| --- | --- | --- |
| Global | `Ctrl+L` focuses and selects search; Tab follows visual order | Focus never enters hidden panes |
| Search | Down Arrow moves to the first result; Escape clears the query, then scopes on a second press | Result focus preserves the query |
| Results | Up and Down move focus; Ctrl and Shift extend standard selection; Enter opens one Ready focused row | Focus remains on the originating row after the launch request |
| Session favorite | `Ctrl+Shift+S` or the named details button toggles the focused session | Focus remains on the invoking control; failure restores committed state |
| Directory favorite | `Ctrl+Shift+D` or the named path-adjacent button toggles the focused directory | Focus remains on the invoking control; missing paths stay named |
| Copy | `Ctrl+Shift+C` copies the current Ready selection | Focus stays in results or on the invoking button |
| Details overlay | Escape closes the overlay | Focus returns to the row that opened it |
| Index status | Escape closes the status view; Enter invokes the focused Rescan button | Focus returns to the Index status button |

The result list exposes selected row count, provider, status, and relative age in
programmatic text. Star buttons expose `Add ... to favorites` or `Remove ... from
favorites`, not glyph names. Index progress, favorite-save failure, launch
completion, clipboard confirmation, and Partial search changes update a named
status region and raise an assistive-technology announcement without taking
keyboard focus. Color, provider ticks, and star shape never carry meaning alone.

Automated layout checks inject normal, high-contrast, reduced-motion, 100
percent, and 200 percent preferences without altering global Windows settings.
They verify system-color replacement, transition suppression, side-pane collapse
at the narrow threshold, text visibility, reachable actions, and focus return.

## Action presentation

Details and selection actions follow the specification's state-action matrix.
Ready rows show Open and Copy. Active, possibly active, unsafe, or unavailable
rows show disabled actions, the exact reason, and one safe next action. When
Windows Terminal is missing,
Open is disabled but Copy stays enabled. A launch failure appears in the status
region and preserves the same Copy action. Batch Copy produces commands only for
deduplicated Ready rows and reports every omitted status by name.

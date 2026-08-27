# Proposal - Continuous results and resume defaults

**Trigger:** Real-corpus use exposed three presentation gaps. The header reported
3,122 indexed sessions while the result list exposed only one 50-row page,
background indexing replaced the list and cleared the user's selection, and the
published executable had no distinct taskbar icon. The user's normal provider
launch modes also require Claude Code permission skipping and Codex yolo mode.

**Summary:** Replace manual Previous and Next paging with one continuously
scrollable virtual result surface. Its logical row count equals the complete
query count, while fixed-size pages are requested only when the viewport needs
them. Keep the current query, selected session identities, focused session, and
visible anchor stable while indexing or availability updates arrive. Only an
intentional query, scope, or directory-filter change returns the list to the
top. Bundle a recognizable multi-resolution Windows icon. Add the user's
provider defaults to every structured launch and copied PowerShell command:
`claude --dangerously-skip-permissions --resume <session-id>` and
`codex --yolo resume <session-id>`.

## Blast radius

- Requirements affected: startup and browsing behavior, deterministic paged
  search, safe resume, command copy, accessibility, and release packaging.
- Design decisions affected: result-page caching, refresh coordination,
  selection restoration, header controls, provider argument construction, and
  executable resources.
- Task ownership: new T29 owns the implementation, automated coverage,
  published smoke verification, and canonical fold for this delta.
- Already-built code affected: `MainForm`, resume planning and formatting,
  integration tests, app project resources, README examples, and release output.

## Status

- [x] delta reviewed (analyze)
- [x] implemented and verified
- [x] folded into the canonical feature spec

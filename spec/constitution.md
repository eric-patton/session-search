# Constitution - Local AI Session Search

## Mission

Make every local Claude Code and Codex session quickly findable and safely
resumable from one lightweight Windows application.

## Non-negotiables

- Performance is product behavior. Startup, search latency, indexing progress,
  and resource use must be measured against the real local corpus.
- Claude Code and Codex source data is read-only. The application owns a
  separate index and never edits, moves, or deletes provider transcripts.
- Transcript data stays local. Core indexing, descriptions, and search require
  no network request or hosted AI service.
- User-visible behavior is specified before implementation and verified by an
  acceptance test or a recorded manual check.
- A malformed or newly changed provider record must degrade one result, not the
  entire index or application.
- Authored prose, code, comments, and metadata contain no literal em dash or en
  dash characters.
- If C# is introduced, every multiline string uses a raw string literal.

## Tech and architecture defaults

- Languages and frameworks: native Windows desktop application with no browser
  runtime; the exact native stack is selected in the feature plan.
- Architecture style: one local application with provider adapters, an
  incremental background indexer, a query service, and a native UI that never
  waits on file parsing.
- Data and integration defaults: app-owned SQLite database with full-text
  search; read provider storage and metadata through read-only access; invoke
  supported provider CLIs and Windows Terminal for resume actions.
- Provider-specific parsing stays behind versioned adapters with captured
  fixture tests. Internal data formats are never treated as permanent APIs.

## Security and compliance

- Store the index under the current user's local application data with no
  broader permissions than the source transcripts.
- Never place transcript text, credentials, environment secrets, or resume
  commands containing secrets in diagnostic logs.
- Do not require administrator privileges or modify Claude Code, Codex,
  PowerShell, or Windows Terminal configuration.
- Pin dependencies and review native process-launch arguments for command
  injection and quoting errors.

## Quality bar

- Testing expectation: unit fixtures for each provider record shape,
  integration tests over synthetic corpora, acceptance tests tagged with the
  owning criterion, and read-only benchmarks against the user's real corpus.
- Accessibility, performance, and observability minimums: full keyboard access,
  visible focus, screen-reader names for controls, 200 percent display scaling,
  query and startup telemetry visible locally, and no focus-stealing UI tests.
- Review expectation: the pre-build consistency check must pass before coding;
  after implementation, code must be audited against the canonical spec.

## Out of scope, project-wide

- Cloud or remote session histories that are not already stored locally.
- Editing, deleting, renaming, archiving, or otherwise managing provider-owned
  transcripts from this application.
- Multi-user accounts, cloud synchronization, or a hosted search service.
- Treating the search index as a transcript backup or retention system.

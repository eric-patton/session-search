<!-- CONVERGE LEDGER - APPEND-ONLY. Maintained by /spec-flow:converge (sole writer).
     Never rewrite or delete a prior entry; every state change is a new event in a new run block. -->
# Converge ledger - local-ai-session-search

## run 1 - 2026-08-27
baseline: spec sha256:e0a938f0cd3c · plan sha256:b573c0da6adc · tasks sha256:1553230932d0

implemented: AC-5, AC-8, AC-9, AC-14, AC-18, AC-19, AC-20

- opened gap-001 [partial] spec:"AC-1 provider roots and normalized ownership" -> code:src/SessionSearch.App/AppOptions.cs:17 honors both configured roots, but tests/SessionSearch.IntegrationTests/Indexing/ProviderFixtureIndexingTests.cs:20 supplies explicit roots and does not prove override and fallback selection on the target machine. route: tasks T23
- opened gap-002 [partial] spec:"AC-2 warm and first-run result milestones" -> evidence:artifacts/benchmark-real-final-report.json:12 records first metadata at 4264.913 ms, but the report has no warm existing-index process-start to usable-list measurement. route: tasks T24
- opened gap-003 [partial] spec:"AC-3 metadata and transcript p95 latency" -> evidence:artifacts/benchmark-real-final-report.json:45 records metadata p95 at 14.678 ms, while artifacts/benchmark-real-final-report.json:55 records transcript p95 at 198.35 ms against the 150 ms requirement. route: tasks T24
- opened gap-004 [partial] spec:"AC-4 complete search grammar and limit matrix" -> code:tests/SessionSearch.Core.Tests/QueryParserTests.cs:15 and tests/SessionSearch.IntegrationTests/Search/SessionSearchServiceTests.cs:389 cover the primary grammar and paging bounds, but do not independently exercise every declared numeric guard and representative combined query class. route: tasks T25
- opened gap-005 [partial] spec:"AC-6 complete title and description fallback chain" -> code:tests/SessionSearch.Core.Tests/SessionTextResolverTests.cs:10 proves major precedence and normalization branches, but omits the immutable-ID fallback and exact accepted 180-scalar boundary cases across both provider fixtures. route: tasks T23
- opened gap-006 [partial] spec:"AC-7 favorite persistence and rollback" -> code:tests/SessionSearch.IntegrationTests/Storage/FavoritesRepositoryTests.cs:10 proves independent persistence and path normalization, but has no injected save-failure test that restores the committed star and visible notice. route: tasks T25
- opened gap-007 [partial] spec:"AC-10 exact Ready-row commands for both providers" -> code:tests/SessionSearch.IntegrationTests/Windows/ResumePlannerTests.cs:69 proves Claude apostrophe quoting, but not the complete two-provider spaces, Unicode, shell-metacharacter, display-equals-clipboard matrix. route: tasks T25
- opened gap-008 [partial] spec:"AC-11 complete mixed batch action matrix" -> code:tests/SessionSearch.IntegrationTests/Windows/SessionActionRouterTests.cs:26 proves visible order, deduplication, blocking, and continued launch failure, but does not distinguish Active from Possibly active in the full exact-count matrix. route: tasks T25
- opened gap-009 [partial] spec:"AC-12 complete availability and unknown-record matrix" -> code:tests/SessionSearch.Core.Tests/AvailabilityEvaluatorTests.cs:24 and tests/SessionSearch.IntegrationTests/Indexing/IndexRetentionTests.cs:158 prove precedence and retained unsupported rows, but do not prove a first-seen unidentified shape remains diagnostic-only through the full workflow. route: tasks T25
- opened gap-010 [partial] spec:"AC-13 complete incremental indexing matrix" -> code:src/SessionSearch.Infrastructure/Indexing/IndexingCoordinator.cs:156 processes sources sequentially while spec/features/local-ai-session-search/plan.md:195 specifies a bounded Channel pipeline, and the tests do not cover every watcher, interrupted restart, lock, archive-move, and storage-limit combination. route: tasks T26
- opened gap-011 [partial] spec:"AC-15 idle memory and real UI-thread responsiveness" -> evidence:artifacts/benchmark-real-final-report.json:333 explicitly reports process-scheduler-only coverage, so the 79.6 MB idle result passes while the required WinForms UI-thread probe remains unproven. route: tasks T27
- opened gap-012 [partial] spec:"AC-16 narrow responsive access and full automation matrix" -> code:src/SessionSearch.App/MainForm.cs:756 and src/SessionSearch.App/MainForm.cs:777 collapse favorites and details panes, but no toolbar or drawer restores those surfaces at narrow widths as required by spec/features/local-ai-session-search/design.md:59. route: tasks T27
- opened gap-013 [partial] spec:"AC-17 complete retry and Rescan isolation matrix" -> code:tests/SessionSearch.IntegrationTests/Indexing/IndexRetentionTests.cs:17 and tests/SessionSearch.IntegrationTests/Search/SessionSearchServiceTests.cs:352 preserve committed data and Partial state, but do not prove the full new, deleted, changed, failed, fresh-query retry, and authoritative Rescan matrix. route: tasks T26

verdict: open 13 (missing 0, partial 13, contradicts 0, unrequested 0)

## run 2 - 2026-08-27
baseline: spec sha256:4b3ed2d9817e | plan sha256:f6ea783133bf | tasks sha256:60ecff1355a0

implemented: AC-18

- confirmed gap-001 [partial] spec:"AC-1 provider roots and normalized ownership" -> evidence: provider-root override and fallback selection remain unproven beyond the explicit-root fixture from run 1. route: tasks T23
- confirmed gap-002 [partial] spec:"AC-2 warm and first-run result milestones" -> evidence: the real report still lacks a warm existing-index process-start to usable-list measurement. route: tasks T24
- confirmed gap-003 [partial] spec:"AC-3 metadata and transcript p95 latency" -> evidence: transcript p95 remains 198.35 ms against the 150 ms requirement. route: tasks T24
- confirmed gap-004 [partial] spec:"AC-4 complete search grammar and limit matrix" -> evidence: the full numeric-guard and combined-query matrix remains incomplete. route: tasks T25
- confirmed gap-005 [partial] spec:"AC-6 complete title and description fallback chain" -> evidence: immutable-ID fallback and exact 180-scalar provider boundaries remain incomplete. route: tasks T23
- confirmed gap-006 [partial] spec:"AC-7 favorite persistence and rollback" -> evidence: injected favorite-save failure and visible rollback remain unproven. route: tasks T25
- confirmed gap-007 [partial] spec:"AC-10 exact Ready-row commands for both providers" -> evidence: the complete two-provider quoting and display-equals-clipboard matrix remains incomplete; the new Codex alias proof covers only final-path dispatch. route: tasks T25
- confirmed gap-008 [partial] spec:"AC-11 complete mixed batch action matrix" -> evidence: Active and Possibly active remain incomplete in the full exact-count matrix. route: tasks T25
- confirmed gap-009 [partial] spec:"AC-12 complete availability and unknown-record matrix" -> evidence: a first-seen unidentified record remains unproven through the full diagnostic-only workflow. route: tasks T25
- confirmed gap-010 [partial] spec:"AC-13 complete incremental indexing matrix" -> evidence: the implementation remains sequential and the complete watcher, restart, lock, archive, and storage-limit matrix remains open. route: tasks T26
- confirmed gap-011 [partial] spec:"AC-15 idle memory and real UI-thread responsiveness" -> evidence: idle memory passes while a real WinForms UI-thread probe remains absent. route: tasks T27
- confirmed gap-012 [partial] spec:"AC-16 narrow responsive access and full automation matrix" -> evidence: narrow widths still lack the required toolbar or drawer access to collapsed surfaces. route: tasks T27
- confirmed gap-013 [partial] spec:"AC-17 complete retry and Rescan isolation matrix" -> evidence: the full new, deleted, changed, failed, fresh-query retry, and authoritative Rescan matrix remains incomplete. route: tasks T26

AC-18 delta verification: `CodexInstallerAliasPolicy` reads only the exact two
directory redirect targets and validates a direct reparse-free current release;
`TrustedExecutableResolver` binds WinVerifyTrust, the exact signer subject, file
identity, alias convergence, and final-path-only revalidation. The 181-test
Release gate covers hostile redirects, nested and outside releases, arbitrary
reparse executables, same-display-name wrong signers, retargeting, and command
dispatch. A 0.1.1 target-machine smoke found four visible Ready Codex rows, one
Active Codex row, zero Missing CLI rows, and a selected Ready command containing
the final release path with no installer alias. No real-session UI capture was
persisted.

verdict: open 13 (missing 0, partial 13, contradicts 0, unrequested 0)

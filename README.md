# Session Search

Session Search is a fast, local Windows index for Claude Code and Codex session history. It gives you one place to browse recent work, search session metadata and transcript text, star sessions or directories, copy exact resume commands, and reopen one or many sessions in Windows Terminal tabs.

Everything stays on this computer. The app reads provider storage without modifying it and writes its own protected SQLite index under `%LOCALAPPDATA%\SessionSearch`.

## What it does

- Shows the newest sessions first with provider, title, recent request, directory, branch, model, timestamps, and source status.
- Searches title, description, full directory, branch, model, session ID, parent transcript text, and child or subagent transcript text.
- Publishes matching metadata immediately, then completes transcript search in the background.
- Keeps session favorites and directory favorites independent. A directory favorite acts as a filter without replacing the search query.
- Supports multi-selection. `Open ready tabs` sends each eligible session to a separate tab in the most recently used Windows Terminal window.
- Shows the exact PowerShell resume command before copying it.
- Blocks automatic resume for active, possibly active, archived, removed, unsafe, unsupported, or otherwise unavailable sessions.
- Exposes local timing, memory, index size, source health, and sanitized parser diagnostics through `Index status`.

## Requirements

- Windows 10 or Windows 11, x64.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the framework-dependent release.
- Claude Code and/or Codex installed for resume actions.
- Windows Terminal installed for `Open` and `Open ready tabs`. Command copy still works without Windows Terminal.
- Provider executables and Windows Terminal must resolve to trusted, Authenticode-signed local files. The app does not launch an untrusted PATH result.

Default provider roots are `%USERPROFILE%\.claude` and `%USERPROFILE%\.codex`. `CLAUDE_CONFIG_DIR` and `CODEX_HOME` override those defaults.

## Run it

From a published release folder:

```powershell
.\SessionSearch.exe
```

From source:

```powershell
dotnet run --project .\src\SessionSearch.App\SessionSearch.App.csproj -c Release
```

The first launch makes metadata searchable before transcript indexing completes. Subsequent launches reuse the local index and reconcile changes in the background. Press `F5` or choose `Rescan` to request an authoritative scan.

## Search and keyboard use

- Type ordinary terms for case-insensitive metadata substring matching plus indexed transcript prefix matching.
- Put a phrase in double quotes to require that transcript phrase.
- Choose `All`, `Claude Code`, `Codex`, or `Starred` without losing the current query.
- Select a favorite directory to add an exact directory scope. Choose `All directories` to remove it.
- Press `Down Arrow` in search to move into results.
- Press `Enter` on a result to open it, or `Space` to toggle its session favorite.
- Press `Ctrl+D` to toggle a directory favorite, `Ctrl+L` to return to search, `F5` to rescan, and `Escape` to clear the directory filter or query.
- Use Ctrl or Shift selection in the result list to open or copy multiple ready sessions.

## Resume commands

Claude Code commands use this shape:

```powershell
Set-Location -LiteralPath 'C:\path\to\project'; & 'C:\path\to\claude.exe' '--resume' '00000000-0000-0000-0000-000000000000'
```

Codex commands use this shape:

```powershell
Set-Location -LiteralPath 'C:\path\to\project'; & 'C:\path\to\codex.exe' 'resume' '00000000-0000-0000-0000-000000000000'
```

The displayed preview and copied text are produced from the same command object. Paths use PowerShell literal quoting, including doubled apostrophes. Commands are never passed through a shell automatically. Automatic open uses structured Windows Terminal arguments.

Copied commands request exclusion from Windows clipboard history and cloud clipboard upload. Windows honors these registered formats, but this cannot protect against another process running as the same user and actively monitoring the clipboard.

## Local data and privacy boundary

The default index is `%LOCALAPPDATA%\SessionSearch\session-search.sqlite3`. Its directory and SQLite sidecars receive a current-user-only Windows ACL. Transcript text is stored only in this local index so that full-text search is fast.

The app:

- Opens source transcripts read-only with sharing compatible with active provider writers.
- Bounds candidate counts, JSON depth, JSONL record size, extracted text, stored segments, total index size, and required free disk space.
- Uses snapshot lengths so a continuously growing JSONL file cannot make one scan run forever.
- Keeps prior committed results when a provider read is partial or fails.
- Sanitizes diagnostics before persistence.
- Does not send telemetry, queries, transcript text, session IDs, paths, titles, snippets, or commands over the network.

This is a same-user desktop tool, not a defense against malware or another process already running with your Windows identity.

## Purge and recovery

SQLite secure deletion is enabled and FTS deletion is hardened. The app checkpoints and truncates WAL data after destructive reconciliation when possible. Filesystems and SSD firmware may retain historical blocks outside application control, so uninstalling or deleting the index is not a forensic erasure guarantee.

If WAL truncation cannot complete, the app schedules a clean rebuild for the next launch. That rebuild creates a new protected database, preserves only favorite metadata and favorite directory records, atomically replaces the old index, and removes its app-owned sidecars.

A migration failure leaves the existing database untouched and stops startup safely. Check `Index status` for sanitized diagnostics. If recovery is still required, close Session Search, move the `%LOCALAPPDATA%\SessionSearch` folder to a backup location, and relaunch to create a new index. Do not delete the backup until favorites and expected sessions have been verified.

## Build, test, benchmark, and publish

The complete Release gate uses locked packages, formatting, analyzers, all tests, package vulnerability checks, spec validation, generated document checks, and privacy scans:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
```

Publish the versioned framework-dependent ReadyToRun build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.1.0
```

The publish script refuses to reuse an existing version folder and scans the completed folder before reporting success.

The benchmark harness requires explicit absolute provider roots, a new isolated data directory under LocalAppData, and a sanitized JSON output path. It always removes its SQLite database, WAL, SHM, and journal files. Run `SessionSearch.Benchmarks --help` for the guarded command surface.

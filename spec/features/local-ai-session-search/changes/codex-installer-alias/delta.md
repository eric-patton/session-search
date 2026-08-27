# Delta - Codex installer alias

> The change expressed against the current feature spec as explicit operations.

## ADDED

- The exact official Codex installer alias at
  `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe` may use its two known
  directory redirects only when the installer `bin` target is exactly
  `%USERPROFILE%\.codex\packages\standalone\current\bin` and the `current`
  target is exactly one immediate version directory below
  `%USERPROFILE%\.codex\packages\standalone\releases`. The resolver reads
  redirect metadata without opening the alias executable, validates the derived
  final `codex.exe` as local, fixed-drive, and reparse-free, and requires both
  WinVerifyTrust and signer policy `codex-signer-v1`. That policy compares
  `X509Certificate2.Subject` by ordinal equality and initially accepts only
  `CN="OpenAI OpCo, LLC", O="OpenAI OpCo, LLC", L=San Francisco, S=California, C=US`.
- `ResolvedExecutable.CanonicalPath`, `ProcessStartInfo.ArgumentList`, displayed
  commands, and clipboard commands use only the verified final versioned path.
  They never use the mutable installer alias. Revalidation immediately before
  copy or launch repeats alias convergence, WinVerifyTrust, signer-subject, and
  file-identity checks.

## MODIFIED

- **Executable resolution contract**
  - Was: Every provider executable reparse path is rejected. Only the verified
    Windows Terminal package alias has an exception.
  - Now: Arbitrary provider executable reparse paths remain rejected. The exact
    official Codex installer alias has the narrowly bounded metadata-only
    resolution above, and Windows Terminal retains its existing package-backed
    exception.
- **AC-18 hostile executable coverage**
  - Was: Tests reject reparse executable paths without an accepted provider
    alias case.
  - Now: Tests reject arbitrary and redirected executable aliases and accept
    only the exact Codex installer alias when both expected redirect targets,
    current-release equality, final target shape, filename, reparse-free target,
    WinVerifyTrust result, full signer subject, and identity all verify. Tests
    also prove alias retargeting cannot change the path copied or dispatched.

## REMOVED

- None.

# Proposal - Codex installer alias

**Trigger:** The published target-machine smoke test found the installed Codex
CLI at `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`. Its `bin`
directory redirects to `%USERPROFILE%\.codex\packages\standalone\current\bin`,
and `current` redirects to the active version below
`%USERPROFILE%\.codex\packages\standalone\releases`. The existing blanket
reparse rule therefore labeled every inactive Codex session Missing CLI.

**Summary:** Permit only the exact official Codex installer alias. Read the two
redirect targets as metadata without opening the alias executable. Require the
installer `bin` target to be the exact `current\bin` path and the `current`
target to be one immediate version directory under the known standalone
releases root. Validate the resulting `codex.exe` as a reparse-free local file,
verify WinVerifyTrust plus the versioned OpenAI signer-subject allowlist, and
store only that final versioned path in the resolved executable. Direct launch,
command display, and clipboard text use only the verified final path and never
the mutable alias. Dispatch revalidation repeats alias convergence, signature,
signer subject, and file-identity checks. Arbitrary executable, source, and
working-directory reparse paths remain rejected.

Signer policy `codex-signer-v1` lives in the application composition profile and
initially accepts this full `X509Certificate2.Subject` value by ordinal equality:
`CN="OpenAI OpCo, LLC", O="OpenAI OpCo, LLC", L=San Francisco, S=California, C=US`.
WinVerifyTrust must succeed before the subject comparison and must run again at
dispatch revalidation.

The safety contract does not depend on whether either approved directory
redirect is implemented as a junction or a directory symbolic link because the
application never traverses the alias during resolution or dispatch. Any extra
redirect, redirected target, nested release directory, filename mismatch, or
unapproved signer fails closed. A signer-subject rotation requires an explicit
allowlist and test update. A malicious same-user process that can replace an
already verified executable between the final check and the Windows loader is
outside the prototype threat model and remains an MVP hardening item.

## Blast radius

- Requirements affected: executable resolution and hostile reparse handling in
  AC-18.
- Design decisions affected: safe resume and command copy.
- Task ownership: completed T12 and T18 are baseline dependencies; new T28 owns
  every implementation and verification change in this delta.
- Already-built code affected: `TrustedExecutableResolver`, startup composition,
  executable trust tests, README prerequisites, and published smoke evidence.

## Status

- [x] delta reviewed (analyze)
- [x] implemented and verified
- [x] folded into the canonical feature spec

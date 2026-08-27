<!-- BEGIN spec-flow orientation -->
## Spec-flow orientation for coding agents

This project uses spec-flow, a per-feature specification workflow. Project-specific rules live in
`spec/constitution.md` and `spec/product-global.md`. These workspace invariants always apply:

- `spec/features/<slug>/spec.md` is the source of truth for each feature.
- `spec/product.md`, `spec/dashboard.md`, and `spec/view.html` are generated. Never hand-edit them.
  Refresh them with the scripts under `scripts/`.
- Read `spec/constitution.md` and `spec/product-global.md` before specifying, planning, reviewing, or
  implementing.
- When the next step is unclear, ask the active client to continue spec-flow for the feature. The flow
  orchestrator reads readiness, chooses the next valid stage, and pauses at human gates.
- Feature depth controls assurance. A feature is `prototype`, `mvp`, or `ga`; promotion raises the bar
  and requires outstanding debt, sign-offs, and drift to be resolved.
- Do not change approved behavior only in code. Record a specification delta or a traced defect so the
  canonical specification remains accurate.
- Run converge after implementation to compare code with the canonical documents and maintain the
  append-only drift ledger.
- Tests cite the acceptance criterion they verify as `feat-NNN/AC-N` in the test name or a comment.
<!-- END spec-flow orientation -->

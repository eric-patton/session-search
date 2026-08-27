<!-- BEGIN spec-flow orientation -->
## spec-flow workspace

This project uses **spec-flow** - a per-feature, spec-driven workflow. A few always-true rules so you
don't rediscover them each session (project-specific rules live in `spec/constitution.md` and
`spec/product-global.md`, not here):

- **The per-feature `spec/features/<slug>/spec.md` is the source of truth.** Everything traces back to it.
- **`spec/product.md` and `spec/dashboard.md` are GENERATED - never hand-edit them.** They are pure
  script output (`node scripts/assemble.mjs`, `node scripts/dashboard.mjs`) and a guard hook blocks edits.
  To change the product, edit a feature's `spec.md`; to refresh status, re-run the script.
- **`spec/constitution.md` (non-negotiables) and `spec/product-global.md` (cross-cutting rules) are
  authoritative** - read them before specifying, planning, or reviewing. Both are main-branch-only.
- **When unsure what to do next, run `/spec-flow:flow`.** It reads the workspace, picks the next step,
  and pauses for your decisions - it never bulldozes a gate.
- **Depth is the gate dial.** Each feature is `prototype` (gates warn, with a recorded IOU), `mvp`, or
  `ga` (gates firm). `promote` raises the depth and refuses while any debt (override, sign-off, drift) is unpaid.
- **Don't change behavior by editing code alone** - capture it as a `/spec-flow:change` (a spec delta) or
  a traced regression fix (a defect), so the spec stays true to what the system actually does.
- **Keep code and spec honest with `/spec-flow:converge`** - it audits the real code against the spec and
  keeps an append-only drift ledger. At `mvp`/`ga` a feature cannot finish until a converge run is on
  record with zero contradictions; `flow` offers to run it at the finish line. Its complement,
  `/spec-flow:break`, executes the code where the spec is silent and proposes (never self-applies)
  spec additions or defect fixes from what it observes.
- **Tests cite the acceptance criterion they verify** as `feat-NNN/AC-N` (feature id + criterion id) in
  the test name or a comment; the linter computes coverage from that token, and `spec/.spec-flow.md`'s
  `tests:` globs say where the tests live.
<!-- END spec-flow orientation -->

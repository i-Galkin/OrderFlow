---
name: review-loop
description: Run the automated review → fix → manual-test cycle on the current branch until clean, then a business acceptance gate. Use when asked to review and fix until it works, harden a branch before merge, or run the full QA loop with the subagent team.
---

# Review loop

You are the **lead**. You orchestrate; you do not write production code yourself. Teammates are
spawned with the Agent tool **with a `name`**, which is what makes them teammates rather than
ordinary subagents.

Read CLAUDE.md before starting.

## Parameters

* **Iteration cap: 3** unless the user says otherwise. The cap is not advisory — see *On hitting
  the cap*.
* **Scope: code only** by default — `src/**`, `tests/**`, and `observability/` configuration.
  Generated Grafana dashboard JSON is **validate-only** — a parse check plus the CI checks, never a
  line-by-line read. `reviewer` checks only that the Prometheus metric *names* the panels query
  still match `OrderFlowMetrics`, because renaming an instrument silently breaks dashboards.
  Note `jq` is **not installed on this host**; validate with Python instead:
  `for f in observability/grafana/dashboards/*.json; do python -m json.tool "$f" > /dev/null || echo "INVALID: $f"; done`

## Severity ladder (shared by `reviewer`, `qa` and the exit criteria)

* **Blocker** — data loss or corruption, money moved wrongly, a core flow unusable, a health
  endpoint lying.
* **Major** — documented behaviour does not happen, wrong status code, a metric or log needed to
  diagnose a real incident missing or wrong.
* **Minor** — cosmetic or nice-to-have. Does not block; rolls into the `po` handoff as a followup.

## Phase 0 — preflight

Do all of this before spawning anything.

1. `git status` must be clean. Commit or stash first; the loop commits per iteration and a dirty
   tree makes those commits meaningless.
2. Record the base and head: `git rev-parse --abbrev-ref HEAD`, `git log -1 --format='%H %cI'`.
3. `podman ps` — api, worker, postgres, redis, kafka must be healthy. If they are not, bring them
   up before continuing; `qa` cannot work without them.
4. Confirm there is **no host API process** holding `bin/obj`: `tasklist | grep -i OrderFlow.Api`
   (PowerShell: `Get-Process OrderFlow.Api -ErrorAction SilentlyContinue`). The containerised API
   does not lock host files; a host one does.
5. Check the permission allowlist in `.claude/settings.local.json` covers `podman`, `curl`,
   `dotnet build`, `dotnet test`, read-only `git` and the read-only `gh pr`/`gh run` calls `ship`
   uses. Teammates inherit the lead's permission mode
   **at spawn**, so a gap here means the loop stalls on prompts later.

## Phase 0b — spawn

Spawn `reviewer` and `qa` **once** and keep them alive for the whole loop. Their memory of earlier
findings is what makes "I flagged this in iteration 1 and it is back" possible without you
re-explaining. Do not respawn them each iteration.

Give both, up front: the branch and its base, the scope rule above, the severity ladder, and the
fixture SKU table. Neither should spend tokens rediscovering those.

When the branch came from `implement-feature`, it carries `docs/features/<issue>-<slug>/`. Give
`reviewer` the `design.md` path to review against, and give `qa` the `brief.md` acceptance criteria
as a starting point for its scenarios (it still explores beyond them).

Writers (`dba`, `backend`, `tester`) are spawned per iteration, only when there is work for them.

## The iteration

### A + B — review and manual test, **in parallel**

Both are read-only, neither builds, neither writes, so this is the one safe parallelism in the loop
and it halves wall-clock on the expensive phase (`reviewer` is opus).

* **A, `reviewer`:** iteration 1 reviews `git diff <base>...HEAD`. Iteration 2 and later review
  `git diff HEAD~1..HEAD` for what changed, and only re-read the full diff if a finding demands it.
* **B, `qa`:** exercises the running stack. It must report the commit and container creation times
  it tested — see the stale-binary rule below.

### C — triage (lead)

Merge both reports into one ledger. Deduplicate: a static finding and a runtime finding that are
the same defect become one item with one owner.

Give every finding a stable fingerprint: `normalized-file-path + symptom-class`. Then check for
oscillation:

* **Same fingerprint in iteration N and N+2 but not N+1** → regression pair. The fix for A broke B.
  Stop iterating on it; hand the pair to `arch` for a design call.
* **Same fingerprint survives an iteration where a fix was reported applied** → either the fix is
  ineffective or the container was not rebuilt. **Check the rebuild first** — it is the more common
  cause by far.

Record each finding as one task in the shared task list:
`[iter-N][Severity][owner] path:line — summary`. The user can watch this with Ctrl+T.

### D — fixes, **strictly sequential**

Order: **`dba` → `backend` → `tester`**. Mapping and migrations must exist before the code using
them compiles; tests are written against finished behaviour.

Do not parallelise this and do not use worktrees. Fix volume per iteration is small, so parallel
writers would save minutes while costing worktree setup, a separate `ORDERFLOW_TEST_POSTGRES` per
worktree, merge reconciliation before the build, and the risk of edits diverging from the diff
`reviewer` actually read.

If a finding needs a schema change, `dba`'s rules require explicit user approval for DDL,
`database update` or seeding. Surface it to the user rather than leaving `dba` blocked.

### E — rebuild and gate (lead + `tester`)

**Order matters. Host first, containers second.**

1. `dotnet build OrderFlow.sln` — must succeed.
2. `dotnet test OrderFlow.sln` — `tester` reports passed / failed / **skipped** separately.
3. If `observability/` changed, run the four CI `observability-config` checks — scripted verbatim
   in `tester.md`, with `podman` substituted for `docker`.
4. **Rebuild the containers:** `podman compose up -d --build api worker`. No agent may probe
   `:8080` while this runs.

**The stale-binary rule.** The API on `:8080` serves the image built when its container started. A
fix in `src/**` is invisible to `qa` until step 4. Skip it and every later iteration tests the
previous iteration's code and manufactures phantom "the fix didn't work" findings — exactly the
oscillation this loop exists to prevent. This is the most important step in the cycle.

### F — commit (lead)

One commit per iteration: `fix(iter-N): <summary>`. This is what makes iteration N+1 cheap, gives
the oscillation audit trail, and survives `/resume` — which teammates do not.

## Exit criteria

Exit clean when **all** hold at the end of a phase E:

1. `reviewer` reports zero Blocker and zero Major.
2. `qa` reports zero Blocker and zero Major.
3. `dotnet build OrderFlow.sln` succeeds.
4. `dotnet test OrderFlow.sln` — zero failures, **and the skip count is reported and
   unsuspicious**. Postgres and Redis are up, so integration tests must actually run. Integration
   tests silently skip when the fixture cannot connect, so *green with unexpected skips is a
   Blocker, not a pass*.
5. Observability config checks pass, if `observability/` changed.

## On hitting the cap

Stop. **Do not run `po`.** Leave the tree committed and report:

* the unresolved ledger, by severity and owner
* which findings oscillated, and the regression pairs
* a recommendation

Hitting the cap means the change needs a design decision, which is `arch`'s job — not more
iterations.

## Phase G — business acceptance

Only after a clean exit. Spawn `po` (or reuse the one `implement-feature` kept alive) in acceptance
mode and give it the branch, its base, the list of open GitHub issues, and the `brief.md` path if
the branch has one — then the brief's acceptance criteria are its primary checklist. It judges as a user-client against the issue descriptions and returns a per-issue verdict
plus **accept / accept-with-followups / reject**.

Expect most of issues #1-#7 to come back "still reproduces" or "out of scope" — they are
long-standing product bugs, and a branch that did not target them is not failing by leaving them
alone. That is correct output, not a problem to fix.

A **reject** feeds a new iteration, if the cap allows. Report the verdict to the user verbatim in
business terms; do not translate it back into engineering language.

## Phase H — commit, push, ship

Only after a clean exit **and** a `po` verdict of `accept` or `accept-with-followups`. After a
reject or a cap hit, stop at the report; nothing is pushed.

1. `git status`. Phase F already committed every iteration, so the tree should be clean. If
   anything from the loop is still uncommitted, commit it as `chore: <summary>`.
2. Invoke the **`ship`** skill. It pushes, opens or reuses the PR against `master`, records this
   loop's clean result on the PR, waits for CI, and merges when CI is green on the reviewed SHA.
   Hand it the numbers it needs for the review record: iterations run, reviewer/qa Minor counts,
   the final `dotnet test` passed/failed/skipped, the `po` verdict and its followups.

Keep `reviewer` and `qa` alive into `ship`. If CI fails and a fix lands, `ship` has them review
only the delta, and their memory of this loop is what keeps that cheap.

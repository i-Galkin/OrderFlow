---
name: implement-feature
description: Take a GitHub issue from requirements to an open pull request with the subagent team - po writes a business brief, arch designs, dba and backend implement, tester covers the acceptance criteria, debugger verifies them on the running stack with local data - then hand off to review-loop. Use when asked to implement, build or deliver an issue or feature end to end.
---

# Implement feature

You are the **lead**. You orchestrate, route requests between agents, write the brief and design
files the read-only agents produce, commit, and open the pull request. You do not write production
code or tests yourself.

Read CLAUDE.md before starting.

```
po (brief) → arch (design) → [user checkpoint] → dba ⇄ backend → tester → rebuild
  → debugger (verify on local data) → lead opens draft PR → review-loop → ship
```

## Parameters

* **Input:** a GitHub issue number on `i-Galkin/OrderFlow`. If the user gave a free-text request
  instead, ask whether to create an issue first; the issue is what `po` accepts against at the end.
* **Checkpoint: on** — pause for the user's approval after the design (phase 2). Skip only if the
  user says so.
* **dba ⇄ backend round-trip cap: 3** per implementation phase.
* **Verification fix cap: 2** rounds (phase 6). Separate from review-loop's iteration cap.
* **Artifacts** live on the branch under `docs/features/<issue>-<slug>/`: `brief.md` (po) and
  `design.md` (arch). They survive `/resume`, `reviewer` reads the design, and `po` accepts against
  the brief. `<slug>` is a short kebab-case name from the issue title.

## Why the lead routes everything

Subagents cannot spawn or message each other. When `backend` needs `dba`, or `debugger` needs rows
from the database, the agent stops and reports the exact request; **you** send it to the owner and
send the answer back. Spawn every agent **with a `name`** and continue it with `SendMessage`, so
`backend` resumes with its context instead of re-reading the whole design.

## Keeping the issue current (`tracker`)

`tracker` owns every write to the issue, its labels, milestone and the project board. The lead never
edits issues directly. Spawn it once **with a `name`** at phase 0 and send it one event per state
change with `SendMessage` (`event: <name>` plus the fields listed in `tracker.md`). It runs in the
background: do not wait on it before starting the next phase, but read its report when it lands and
surface any `needs approval` or `errors` line to the user. A tracker failure never blocks delivery.

| When | Event |
| --- | --- |
| phase 0 step 2, branch created | `started` |
| phase 1, brief written | `brief` (with the AC list) |
| phase 2, user approved the design | `design-approved` |
| phase 6, debugger results in | `verified` |
| phase 7, PR opened | `pr-opened` |
| any stop | `blocked` if the user is expected to unblock it soon, else `stopped` |

Keep `tracker` alive into review-loop and ship.

## Phase 0 — preflight

1. `git status` must be clean.
2. `git fetch origin && git switch master && git pull --ff-only`, then branch:
   `git switch -c feat/<issue>-<slug>` (`fix/` when the issue is labelled `bug`).
3. `gh issue view <issue> --repo i-Galkin/OrderFlow` — confirm it exists and is open.
4. `podman ps` — postgres, redis, kafka, api, worker healthy. Integration tests and phase 6 need
   them; without them tests silently skip.
5. No host API process holding `bin/obj`: `Get-Process OrderFlow.Api -ErrorAction SilentlyContinue`.
6. Spawn `tracker` and send `event: started` with the issue and branch.

## Phase 1 — business brief (`po`)

Spawn `po` in **brief mode** with the issue number. It returns, in business language only: the
problem and who has it, the behaviour after the change, numbered acceptance criteria
(`AC-1`, `AC-2`, ... as Given / When / Then), what is out of scope, and open questions.

* Open questions that change behaviour → ask the user (AskUserQuestion) before going on, and send
  the answers back to `po` to fold into the brief.
* Write the final brief verbatim to `docs/features/<issue>-<slug>/brief.md`.
* `tracker`: `event: brief` with the path and the `AC-n` titles.

Keep `po` alive; it runs the acceptance gate at the end of review-loop.

## Phase 2 — design (`arch`)

Spawn `arch` with the brief path and the issue number. On top of its usual design it must include
an **acceptance-criteria trace**: for each `AC-n`, the design element that delivers it and the test
that proves it.

* If `arch` reports a conflict between the brief and the existing design, bring it to the user; do
  not let `backend` pick.
* Write the design verbatim to `docs/features/<issue>-<slug>/design.md`.
* Commit both: `docs(#<issue>): brief and design`.

**Checkpoint.** Show the user the acceptance criteria and a short summary of the design (routes,
schema changes, events, the owner-grouped plan) and wait for approval. Changing a design costs
minutes here and an iteration later. On requested changes, send them to `po` (behaviour) or `arch`
(design), rewrite the file, amend nothing — commit again.

## Phase 3 — implementation (`dba` ⇄ `backend`)

Strictly sequential in this checkout; no worktrees. Follow the design's owner-grouped plan.

1. **`dba` first**, if the design has persistence tasks: mapping in `OrderFlowDbContext` and one
   migration via `dotnet ef migrations add`. It does **not** run `database update` against the dev
   database — the containers apply migrations on startup (`OrderFlow__ApplyMigrationsOnStartup`)
   and `PostgresFixture` migrates the test database. Any write, DDL or seed against `orderflow`
   still needs explicit user approval, per `dba`'s rules.
   Entity classes are `backend`'s, so when a migration needs a new entity or property, `backend`
   adds it first and `dba` follows. The design's plan says which order applies.
2. **`backend`** implements `src/**` from the design and verifies with `dotnet build` and unit
   tests.
3. **Round-trips.** When `backend` stops with a request for `dba` (missing mapping, a data or query
   plan question, raw SQL it may not write), send that exact request to `dba`, then `SendMessage`
   the result to `backend` to continue. Count each round-trip; on hitting the cap, stop and hand the
   open question to `arch`.
4. Commit: `feat(#<issue>): <summary>`.

## Phase 4 — tests (`tester`)

Give `tester` the brief, the design's acceptance-criteria trace, and the files changed. It writes
unit and integration tests so that **every `AC-n` has at least one test**, naming the criterion in a
comment above the test, then runs `dotnet test OrderFlow.sln` and reports passed / failed / skipped.

* Unexpected skips with the stack up are a Blocker — find out why before continuing.
* A test that fails because production code is wrong is not `tester`'s to fix: `debugger`
  diagnoses, the owner fixes (`dba` → `backend`), `tester` re-runs.
* If `observability/` changed, `tester` runs the CI config checks from `tester.md`.
* Commit: `test(#<issue>): <summary>`.

## Phase 5 — rebuild

1. `dotnet build OrderFlow.sln` and `dotnet test OrderFlow.sln` green, skips explained.
2. `podman compose up -d --build api worker`, then wait until `curl -s http://localhost:8080/health/ready`
   is healthy. No agent probes `:8080` while this runs; without it phase 6 tests the old image.
3. **Local data.** Ask `dba` (read-only) whether the dev database is seeded
   (`SELECT count(*) FROM customers`). If it is empty, seeding is a write and needs the user's
   approval before `dba` runs `--seed`.

## Phase 6 — verification on local data (`debugger`)

Spawn `debugger` in **verification mode** with the brief, the design, the commit, and the container
creation times. It walks every `AC-n` on the running stack with seeded data and fixture SKUs, one
`X-Correlation-ID` per criterion (`feat-<issue>-ac<n>`), and returns per criterion:
**pass** (with evidence), **fail** (root cause, `file:line`, owner), or **cannot verify** (what is
missing). Route its database questions to `dba`.

On any **fail**, if the fix cap allows: the owner fixes → phase 5 steps 1–2 → `debugger` re-checks
only the failed criteria. Commit `fix(#<issue>): <summary>`. On hitting the cap, stop before the PR
and report the failing criteria with their diagnoses.

**Cannot verify** does not block the PR but goes into its description, so reviewers see the gap.

`tracker`: `event: verified` with the per-criterion results.

## Phase 7 — pull request (lead)

The lead opens it: it is outward-facing, no agent owns git, and `ship` expects to find the PR
the lead created.

1. `git status` clean, then `git push -u origin HEAD`.
2. `gh pr create --draft --base master --title "<type>(#<issue>): <summary>" --body-file <scratchpad>/pr.md`
   with:
   * `Closes #<issue>`
   * the problem in two sentences, from the brief
   * links to `brief.md` and `design.md`
   * the acceptance-criteria table: `AC-n` | test(s) | `debugger` result
   * `dotnet test` passed / failed / skipped
   * anything **cannot verify**, and open followups

It stays **draft** until `ship` has a clean review and green CI. CI still runs on draft PRs.

3. `tracker`: `event: pr-opened` with the PR number.

## Phase 8 — hand off to review-loop

Invoke **`review-loop`**. Tell it: the branch and base `master`, the PR number, the design path
(for `reviewer`) and the brief path (for `po`'s acceptance gate — judge against the brief's
acceptance criteria, not only the issue text). Pass the still-alive `po` and `tracker` along. review-loop and
then `ship` take it from there; `ship` marks the PR ready right before merging.

## Stop and report

Stop without opening a PR when: the user rejects the design, a round-trip or fix cap is hit, or
the build/tests cannot be made green. Leave the branch committed, send `tracker` `event: blocked`
or `event: stopped` with the phase and reason, and report the phase reached, the open questions or
failing criteria with owners, and a recommendation.

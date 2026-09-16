---
name: ship
description: Push the reviewed branch, open its pull request, run the GitHub Actions pipeline on it manually, and merge it to master once CI is green and the code review is recorded as clean for that exact commit. Use after review-loop exits clean, or when asked to ship, land, or merge a reviewed branch.
---

# Ship

You are the **lead**. This skill runs after `review-loop` has exited clean and `po` has accepted.
It can also be run on its own (for example after `/resume`), in which case the review evidence is
read back from the PR instead of from the conversation.

Read CLAUDE.md before starting.

## CI is manual, and the PR is still mandatory

`.github/workflows/ci.yml` has **only a `workflow_dispatch` trigger**: no push, no pull request, no
merge starts it. The user chose this, so never add a trigger back. This skill starts the run itself
with `gh workflow run` (phase 2), which is the one sanctioned automated dispatch, and only on the
branch being shipped. Waiting for a run nobody dispatched waits forever, and an older run for a
different SHA is not evidence.

A dispatched run builds the **branch head**, not a merge ref. Gate 5 (`origin/master` is an ancestor
of `HEAD`) is what makes that equal to the merged result, so it is not optional. Every ship still
goes through a PR: the review marker lives on it and the merge happens through it.

## Parameters

* **CI fix cap: 2** rounds (see phase 3). Separate from review-loop's iteration cap.
* **Flake reruns: 1** per head SHA.
* **Merge method: `--merge`** (merge commit). The `fix(iter-N)` commits are the oscillation audit
  trail review-loop relies on; squashing would erase them.
* **Branch deletion: no.** The repo has `deleteBranchOnMerge: false`; leave that to the user.

## Gate — all must hold to merge

1. **CI green on the PR head SHA**: every job of the latest dispatched `CI` run for `headRefOid` concluded
   `success` (`build-and-test` and `observability-config`). Skipped or cancelled is not green.
2. **Review clean for the same SHA**: a `review-loop:clean` marker comment (phase 1) exists on the
   PR whose `sha=` equals `headRefOid`. A marker for an older SHA does **not** count, see phase 3.
3. **`po` verdict** is `accept` or `accept-with-followups`. `reject` never ships.
4. **No outstanding human review**: `reviewDecision` is not `CHANGES_REQUESTED`.
5. **Mergeable**: `mergeable` is `MERGEABLE`, and `origin/master` is an ancestor of `HEAD`
   (`git merge-base --is-ancestor origin/master HEAD`). If master moved on, stop and ask. Bringing
   master in changes the code under review, so it would need a fresh review-loop, and that is the
   user's call.

## Phase 0 — preflight

1. `git status` clean. If review-loop left uncommitted changes (it should not), commit them as
   `chore: <summary>` only if they came from the loop; anything else, stop and ask.
2. Current branch is not `master`. Never ship from master.
3. `gh auth status` is logged in.
4. `git fetch origin` and check gate 5 early, before spending CI minutes.

## Phase 1 — push and record the review

1. `git push -u origin HEAD`. **Never** `--force` or `--force-with-lease`. If the push is rejected
   because the remote has commits the local branch lacks, stop and report. Someone else pushed, and
   their commits have not been reviewed.
2. Find or create the PR:
   `gh pr view --json number,url,headRefOid,baseRefName` →
   if none, `gh pr create --base master --fill`, then append the review-loop summary to the body.
   The PR must target `master`. A PR opened by `implement-feature` already exists as a **draft**;
   reuse it and leave it draft until phase 4.
3. Confirm `headRefOid` equals `git rev-parse HEAD`. GitHub can lag the push by a few seconds, so
   re-read until it matches.
4. Post the review record as a PR comment (skip if coming from standalone mode with an existing
   marker for this SHA):

   ```
   <!-- review-loop:clean sha=<HEAD sha> -->
   **Review loop: clean** at `<short sha>`, <N> iteration(s)
   - reviewer: 0 Blocker / 0 Major (<M> Minor)
   - qa: 0 Blocker / 0 Major (<M> Minor)
   - dotnet test: <passed> passed / 0 failed / <skipped> skipped
   - po: <verdict>
   Followups: <po followups and Minor findings, or "none">
   ```

   This comment is what survives `/resume` and what gate 2 checks. Write it only when review-loop
   actually exited clean in this conversation. **Never** write it from memory or to unblock a merge.

## Phase 2 — run the pipeline

1. Dispatch it on the branch, then wait for the run to *exist* for this SHA (`gh run list` lags the
   dispatch by a few seconds, and `gh pr checks` never shows dispatched runs):
   `gh workflow run CI --ref <branch>`, then poll
   `gh run list --workflow CI --commit <sha> --event workflow_dispatch --json databaseId,status,conclusion`
   until a row appears (give up after ~2 minutes and report). Dispatch once per SHA: if a run for
   this SHA already exists (e.g. after `/resume`), watch that one instead.
2. Watch it **in the background**, because the Kafka service container health check alone can
   take minutes and the tool timeout is 10:
   `gh run watch <databaseId> --exit-status` with `run_in_background: true`. Do not poll with sleeps.
3. On completion, read the verdict per job:
   `gh run view <databaseId> --json conclusion,jobs --jq '.jobs[] | [.name, .conclusion]'`.

Green on every job → phase 4. Otherwise → phase 3.

## Phase 3 — pipeline failed

Get the evidence first: `gh run view <databaseId> --log-failed`, plus the `test-results` artifact
(`gh run download <databaseId> -n test-results -D <scratchpad>`) when tests failed.

Classify:

* **Infrastructure flake**: service container never became healthy, runner or network error,
  NuGet or image pull failure, with no test or compiler output implicated. Rerun once:
  `gh run rerun <databaseId> --failed` (a rerun is not a new dispatch), then back to phase 2 step 2. A second failure on the same
  SHA is not a flake; treat it as a real failure.
* **Real failure**: build error, failing test, `promtool`/Loki/Alloy/`jq` check. Common CI-only
  causes worth checking first: an integration test that **skipped locally** (no Postgres/Redis in
  that local run) and runs for real in CI; `Release` configuration; Linux case-sensitive paths;
  locale/timezone.

For a real failure, if the CI fix cap allows:

1. `debugger` diagnoses from the log and trx. It does not fix.
2. The owning writer fixes (`dba` → `backend` → `tester`, sequential, same rules as review-loop D).
3. Local gate as review-loop E steps 1–3 (build, test with skip count, observability checks).
   Skip the container rebuild unless `qa` will be re-run.
4. Commit `fix(ci-N): <summary>`.
5. **Review the delta.** The review marker is now stale for the new SHA. `reviewer` reviews
   `git diff <last marked sha>..HEAD`. If the fix touched runtime behaviour in `src/**`, also
   rebuild containers and have `qa` re-check the affected flow. Zero Blocker/Major → post a fresh
   marker for the new SHA (same format, noting `ci-N`). A Blocker or Major here means the CI fix is
   entangled with the design; stop and hand back to `review-loop`.
6. Back to phase 1 step 1 (push; the existing PR picks it up), then phase 2 dispatches a run for
   the new SHA.

**On hitting the CI fix cap:** stop, do not merge. Report the failing jobs, the classified cause,
what was tried per round, and the PR URL, and send `tracker` `event: blocked` with phase `ship`.

## Phase 4 — merge

1. Re-read everything immediately before merging, since state can change during a long CI wait:
   `gh pr view <number> --json headRefOid,mergeable,reviewDecision,state,comments` and
   `git fetch origin && git merge-base --is-ancestor origin/master HEAD`. Re-check all five gates
   against the fresh values.
2. If the PR is a draft (`isDraft` in `gh pr view --json isDraft`), mark it ready now, after all
   gates hold: `gh pr ready <number>`. A draft cannot be merged.
3. Merge, pinned to the SHA that was verified, so a push that lands mid-merge is refused rather
   than merged unreviewed:
   `gh pr merge <number> --merge --match-head-commit <sha>`
4. Confirm: `gh pr view <number> --json state,mergeCommit` shows `MERGED`.
5. For each issue the PR closes, send `tracker` (reuse or spawn with a `name`) `event: merged`
   with the issue, PR and merge SHA. It confirms the issue closed and moves the board to Done.

## Phase 5 — post-merge

1. No CI runs on `master` after the merge, and do not dispatch one: gate 5 means the merge commit's
   tree is the tree the branch run already tested. If the user asks for a master run, dispatch it
   with `gh workflow run CI --ref master` and report the result; if it fails, **do not revert
   automatically** and do not push to master. Master is shared, and a revert is their decision.
2. Locally: `git switch master && git pull --ff-only`. Leave the feature branch in place.

## Report

To the user, briefly: PR URL, merged SHA, the CI run URL with its conclusion,
number of CI fix rounds and flake reruns, and the `po` followups still open. If the skill stopped
short of merging, say which gate failed and what evidence showed it.

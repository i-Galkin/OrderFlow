---
name: ship
description: Push the reviewed branch, wait for the GitHub Actions pipeline on its pull request, and merge it to master once CI is green and the code review is recorded as clean for that exact commit. Use after review-loop exits clean, or when asked to ship, land, or merge a reviewed branch.
---

# Ship

You are the **lead**. This skill runs after `review-loop` has exited clean and `po` has accepted.
It can also be run on its own (for example after `/resume`), in which case the review evidence is
read back from the PR instead of from the conversation.

Read CLAUDE.md before starting.

## Why a pull request is mandatory

`.github/workflows/ci.yml` triggers on `push` to `master` and on `pull_request` targeting `master`
only. **Pushing a feature branch without an open PR runs no pipeline**, so "wait for CI" would wait
forever or, worse, read an old run as green. Every ship goes through a PR.

The `pull_request` run builds GitHub's merge ref (branch merged into master as of the trigger), so a
green run is evidence about the merged result, not just the branch.

## Parameters

* **CI fix cap: 2** rounds (see phase 3). Separate from review-loop's iteration cap.
* **Flake reruns: 1** per head SHA.
* **Merge method: `--merge`** (merge commit). The `fix(iter-N)` commits are the oscillation audit
  trail review-loop relies on; squashing would erase them.
* **Branch deletion: no.** The repo has `deleteBranchOnMerge: false`; leave that to the user.

## Gate — all must hold to merge

1. **CI green on the PR head SHA**: every job of the latest `CI` run for `headRefOid` concluded
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
   The PR must target `master`.
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

## Phase 2 — wait for the pipeline

1. Wait for the run to *exist* for this SHA. Right after a push, `gh pr checks` can report nothing
   and read as "no failures". Poll:
   `gh run list --workflow CI --commit <sha> --event pull_request --json databaseId,status,conclusion`
   until a row appears (give up after ~2 minutes and check that the PR targets master).
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
  `gh run rerun <databaseId> --failed`, then back to phase 2 step 2. A second failure on the same
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
6. Back to phase 1 step 1 (push; the existing PR picks it up).

**On hitting the CI fix cap:** stop, do not merge. Report the failing jobs, the classified cause,
what was tried per round, and the PR URL.

## Phase 4 — merge

1. Re-read everything immediately before merging, since state can change during a long CI wait:
   `gh pr view <number> --json headRefOid,mergeable,reviewDecision,state,comments` and
   `git fetch origin && git merge-base --is-ancestor origin/master HEAD`. Re-check all five gates
   against the fresh values.
2. Merge, pinned to the SHA that was verified, so a push that lands mid-merge is refused rather
   than merged unreviewed:
   `gh pr merge <number> --merge --match-head-commit <sha>`
3. Confirm: `gh pr view <number> --json state,mergeCommit` shows `MERGED`.

## Phase 5 — post-merge

1. The merge pushes to `master`, which triggers `CI` again (`push` event). Find it with
   `gh run list --workflow CI --branch master --event push --limit 1` and watch it in the
   background.
2. If it fails, **do not revert automatically** and do not push to master. Report it to the user
   with `--log-failed` output. Master is shared, and a revert is their decision.
3. Locally: `git switch master && git pull --ff-only`. Leave the feature branch in place.

## Report

To the user, briefly: PR URL, merged SHA, CI run URLs (PR run and master run with conclusions),
number of CI fix rounds and flake reruns, and the `po` followups still open. If the skill stopped
short of merging, say which gate failed and what evidence showed it.

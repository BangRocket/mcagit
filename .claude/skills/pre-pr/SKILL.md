---
name: pre-pr
description: Run the full pre-PR gate for mcagit - tests, agent reviews mapped from the branch diff, and an aggregated findings block for the PR description. Use before opening any PR, or when asked "is this branch ready for a PR?"
---

Execute the CLAUDE.md pre-PR checklist mechanically. Do not skip steps; do not substitute your own review for the agents'.

## Steps

1. **Tests first.** Run `dotnet test -c Release`. On machines without the .NET 9 runtime (SDK 10 only), prefix with `DOTNET_ROLL_FORWARD=LatestMajor`. If anything fails, stop — fix before continuing. Set `MCAGIT_TEST_REGION` to `compare-worlds/New_World_Older/region/r.0.0.mca` so the real-region test runs.

2. **Map the diff to agents.** Run `git diff main...HEAD --stat` (or `--name-only`) and match touched paths against the delegation rules:
   - `src/McaDiff/Diff/`, `Nbt/`, or `Patch/` → launch `nbt-diff-invariant-reviewer`
   - substantive changes under `Anvil/`, `Patch/`, or `Repo/` → launch `world-roundtrip-gauntlet`
   - anything reachable from untrusted input — `RepoServer`, transports, `PatchApplier` path handling, `Hooks`, region/packfile parsing → launch `trust-boundary-exploit-hunter`

   Launch all matching agents **in parallel**, each with the branch diff summary and the list of touched files in its prompt.

3. **Aggregate.** Collect each agent's findings:
   - **BLOCKER** → must be fixed now. After fixing, re-run the agent that raised it (not just the tests).
   - **WARN** → may ship, but goes in the PR description.
   - Note which agents ran and which were skipped (no matching paths).

4. **Produce the PR description block** and show it to the user:

   ```markdown
   ## Summary
   <one-paragraph change summary>

   ## Verification
   - dotnet test: <N>/<N> passed (<os>, real-region test <on|off>)
   - Agents run: <list> — <M> BLOCKERs (all resolved), <K> WARNs

   ## Known warnings
   - <each WARN, one line, with file:line>
   ```

5. Remind the user that CI re-runs build+tests on ubuntu/windows plus the e2e gauntlet, and that the `lint` job is expected to fail (not required).

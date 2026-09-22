# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --comments`, filtering comments by `jq` and also fetching labels.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

Infer the repo from `git remote -v`; `gh` does this automatically when run inside a clone.

## Pull requests as a triage surface

**PRs as a request surface: no.**

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`. If that returns nothing, see the note under "Wayfinding operations" and read it through `gh api` instead.

## Related repository

This repository consumes `amnesia-tdd-tcp`, maintained at `../amnesia-tdd-tcp` locally and `amnesia-spelos/amnesia-tdd-tcp` on GitHub. Track multiplayer-consumer work here; track changes owned by the protocol library in that repository.

## Wayfinding operations

Wayfinder maps live in this repository. Game- and engine-side tickets are created in `amnesia-tdd-tcp` and attached to the map as cross-repo sub-issues, so one query returns the whole frontier.

- **The map**: an issue labelled `wayfinder:map`. Tickets carry `wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling`, or `wayfinder:task`. All five labels exist in both repositories.
- **Attach a ticket to the map**: `gh api --method POST repos/amnesia-spelos/amnesia-multiplayer/issues/<map>/sub_issues -F sub_issue_id=<database id>`. Works across repositories within the org. Use the issue's **database** `id`, not its `node_id`.
- **Wire a blocking edge**: `gh api --method POST repos/<owner>/<repo>/issues/<blocked>/dependencies/blocked_by -F issue_id=<blocker database id>`. Also works cross-repo, and renders in GitHub's own UI.
- **Read the frontier**: `gh api repos/amnesia-spelos/amnesia-multiplayer/issues/<map>/sub_issues --jq '.[] | select(.state=="open") | "\(.repository.name)#\(.number) \(.title)"'`, then check each one's `dependencies/blocked_by` for open blockers and its assignee for a claim.
- **Claim a ticket**: `gh issue edit <number> --add-assignee petrspelos` before doing any work.
- **Get database ids**: `gh api repos/<owner>/<repo>/issues/<n> --jq .id`.

> [!IMPORTANT]
> Pass these ids with `-F` (typed), never `-f` (string). With `-f` the API returns **HTTP 200 and silently does nothing**, so the call looks like it succeeded. Verify afterwards by listing.

Items are also added to the cross-repo board at https://github.com/orgs/amnesia-spelos/projects/4 with `gh project item-add 4 --owner amnesia-spelos --url <issue url>`. This needs the `project` scope, which is not in `gh`'s default set: `gh auth refresh --hostname github.com -s project`, run interactively.

> [!NOTE]
> `gh issue view` returns empty output in some agent environments (exit 0, no text). `gh api repos/<owner>/<repo>/issues/<n>` is the reliable read.

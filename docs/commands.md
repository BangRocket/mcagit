# Command reference

Every `mcagit` subcommand and its flags. A leading `-C <repo>` selects the repository for any command
(otherwise it's discovered from the current directory). Exit codes follow git: **0** identical/clean,
**1** differences/conflicts/not-found, **2** error.

`mcagit <A> <B>` with no subcommand is shorthand for `diff` when both are existing paths.

## Diff & inspect

| Command | Flags | Notes |
|---|---|---|
| `diff [<A> <B>]` / `diff [<refA>] [<refB>]` | `--json`, `--expand`, `--only <cats>`, `--summary`, `--no-color`, `--cached`/`--staged` | Two paths, or refs inside a repo (defaults to `HEAD` vs worktree). |
| `inspect <x> <y> <z> [<world>]` | `--dim D`, `--json` | Block + biome at a coordinate. |
| `find <entity\|block-entity\|sign> <id> [<world>]` | `--text P` (signs), `--near x,y,z`, `--radius N`, `--dim D`, `--json` | Find by namespaced id (substring) or sign text. |
| `players [<world>]` | `--json` | Last-saved player positions/health/gamemode. |
| `poi [<world>]` | `--type T`, `--near`, `--radius`, `--dim`, `--json` | Points of interest. |
| `where-changed <old> <new>` | `--verbose`, `--json`, `--dim` | Classify + locate block changes (grief detector). |

## Patch & restore

| Command | Flags | Notes |
|---|---|---|
| `extract <base> <target> -o <file>` | `--only <cats>`, `--whole-chunk`, `--whole-file`, `--note T` | Write a `.mcapatch`. |
| `apply <patch> <target> -o <out>` | `--reverse`, `--force`, `--dry-run`, `--only <cats>` | Never mutates the target; 3-way guarded. Exit 1 if conflicts skipped. |

## Repository

| Command | Flags | Notes |
|---|---|---|
| `init [<repo>]` | `--worktree <world>` | Refuses to scatter into a `level.dat` folder without `--worktree`. |
| `add <path>…` | `--world <dir>` | Stage to the index. |
| `commit -m <msg>` | `-S`, `--author A`, `--push [<remote>]`, `--token T`, `--json` | Exits 0 on nothing-to-commit (`--json` `committed` is the signal). |
| `backup [-m <msg>]` | (same as `commit`) | Friendly alias for `commit`; auto-dates the message if none given. |
| `undo` | `-y` | Discard changes since the last backup (restore the worktree to HEAD); confirms first. |
| `status` | — | Worktree vs HEAD (and index). |
| `log [<ref>\|<range>]` | `--oneline`, `-p`, `--stat`, `-n N`, `--author`, `--grep`, `--since`, `--until`, `--merges`, `--no-merges`, `--no-color` | Ranges `A..B` / `A...B`. |
| `show <ref>` | `--no-color` | Commit metadata + diff vs parent. |
| `checkout <ref> [<world-out>]` | `--force`, `-y` | Refuses an open world; confirms before overwriting the worktree. |
| `reset [<ref>]` | `--soft`, `--mixed`, `--hard`, `-y` | Default `--mixed`. `--hard` confirms + refuses an open world. |
| `restore <ref> <path>…` | `--staged`, `--source <ref>`, `--world <dir>` | Restore specific files. |
| `revert <commit>` | `--continue`, `--abort` | New commit undoing one; stops on conflict. |
| `clean` | `-n`/`--dry-run`, `-f`/`--force`, `-d`, `-y`, `--world` | `-d` also removes untracked dirs; confirms unless `-y`. |
| `config [<key> [<value>]]` | `--global`, `--list`/`-l`, `--unset` | `--global` works outside a repo. |

## Branch, tag, merge

| Command | Flags | Notes |
|---|---|---|
| `branch [<name>]` | `-d`, `-D`, `-m`, `-f`, `<start-point>` | List / create / delete / move. |
| `tag [<name>]` | `-a`, `-m <msg>`, `-s`, `-v`, `-d`, `-f`, `-n` | `-a`/`-s` annotated/signed; `-v` requires a trusted signer to exit 0. |
| `merge <branch>` | `--continue`, `--abort`, `--ours`, `--theirs` | Per-NBT-node 3-way; stops on conflict. |
| `cherry-pick <commit>` | `--continue`, `--abort` | Replays one commit; stops on conflict. |
| `rebase [<upstream>]` | `--onto <base>`, `--continue`, `--skip`, `--abort` | Resumable; non-interactive. |
| `stash [push\|pop\|apply\|list\|drop\|clear]` | `-m <msg>` | Shelve/restore the worktree. |
| `bisect <start\|good\|bad\|skip\|reset\|log>` | — | Binary-search for a bad commit. |

## Remotes & maintenance

| Command | Flags | Notes |
|---|---|---|
| `clone <src> <dest>` | `--depth N`, `--token T` | `src`: path / `http(s)://` / `ssh://` / `azure://` / `s3://`. |
| `remote [add\|remove\|rename\|set-url\|get-url …]` | — | Bare = list. |
| `fetch [<remote> [<branch>]]` | `--token T` | Into `refs/remotes/<remote>/*`. |
| `push [<remote> [<branch>]]` | `--force`, `--all`, `--token T` | Fast-forward checked; `--all` non-zero if any branch fails. |
| `ls-remote [<remote>]` | `--token T` | List a remote's refs. |
| `verify-remote [<remote>]` | `--deep`, `--token T` | Offsite integrity check. |
| `serve [<repo>]` | `--port N`, `--host H`, `--allow-push`, `--token T` | Built-in HTTP daemon. |
| `serve-stdio [<repo>]` | `--read-only` | The ssh entry point; `--read-only` pins a key to fetch/clone. |
| `reflog` | — | HEAD movement history. |
| `gc` | `--prune-only` | Repack reachable objects + prune. |
| `fsck` | — | Verify object integrity + reachability. |

## Plumbing

`rev-parse [--short\|--abbrev-ref\|--verify] <rev>…` · `cat-file [--pretty] <obj>` ·
`hash-object <file>` · `ls-tree <ref>`.

## Categories for `--only`

`region`, `entities`, `poi`, `nbt` (comma-separated, repeatable).

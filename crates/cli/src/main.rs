//! `mcagit` — minimal CLI for the Rust port (M3 subset: init/commit/checkout/status/log).

use anyhow::{anyhow, bail};
use clap::{Parser, Subcommand};
use mca_patch::WorldPatch;
use mca_repo::{snapshot, ChangeKind, Repository};
use std::io::IsTerminal;
use std::path::PathBuf;
use std::process::ExitCode;

#[derive(Parser)]
#[command(
    name = "mcagit",
    version,
    about = "semantic git-style VCS for Minecraft worlds (Rust)"
)]
struct Cli {
    /// Run as if mcagit was started in <repo>.
    #[arg(short = 'C', long = "repo", global = true)]
    repo: Option<PathBuf>,
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Create a repo, optionally binding a world as the worktree.
    Init {
        dir: Option<PathBuf>,
        #[arg(long)]
        worktree: Option<PathBuf>,
    },
    /// Snapshot a world (the bound worktree, or a given path).
    Commit {
        #[arg(short = 'm', long)]
        message: String,
        /// Sign the commit with SSH (uses `user.signingkey`).
        #[arg(short = 'S', long)]
        sign: bool,
        /// Snapshot the whole worktree instead of committing the index.
        #[arg(short = 'a', long = "all")]
        all: bool,
        world: Option<PathBuf>,
    },
    /// Stage worktree paths into the index for the next commit.
    Add {
        /// Paths / directories / globs to stage (relative to the worktree root).
        pathspecs: Vec<String>,
        /// Stage all changes across the whole worktree.
        #[arg(short = 'A', long)]
        all: bool,
    },
    /// Materialize a snapshot into a directory (the worktree, or a given path).
    Checkout {
        reff: String,
        out: Option<PathBuf>,
        #[arg(long)]
        force: bool,
        /// Sparse checkout: materialize only these region coordinates (repeatable),
        /// e.g. `--region 0,0 --region -1,2`. Loose files (level.dat etc.) are
        /// always written. On a partial clone, only the needed chunks are fetched.
        #[arg(long = "region", value_name = "X,Z")]
        regions: Vec<String>,
    },
    /// Show changes in the worktree vs HEAD.
    Status,
    /// Get a repo config value, or set it: `config <key> [value]`.
    Config { key: String, value: Option<String> },
    /// Show commit history (filterable).
    Log {
        #[arg(long)]
        oneline: bool,
        /// Only commits whose author contains this substring.
        #[arg(long)]
        author: Option<String>,
        /// Only commits whose message contains this substring.
        #[arg(long)]
        grep: Option<String>,
        /// Only commits at or after this unix timestamp.
        #[arg(long)]
        since: Option<i64>,
        /// Only commits at or before this unix timestamp.
        #[arg(long)]
        until: Option<i64>,
    },
    /// Diff two worlds (semantic). Exits 1 if they differ.
    Diff {
        a: PathBuf,
        b: PathBuf,
        #[arg(long)]
        json: bool,
    },
    /// Extract a patch turning world A into world B.
    Extract {
        a: PathBuf,
        b: PathBuf,
        #[arg(short = 'o', long)]
        out: PathBuf,
    },
    /// Apply a patch to a world, writing a fresh output world.
    Apply {
        patch: PathBuf,
        world: PathBuf,
        #[arg(short = 'o', long)]
        out: PathBuf,
        #[arg(long)]
        reverse: bool,
        #[arg(long)]
        force: bool,
    },
    /// List, create, or delete branches.
    Branch {
        name: Option<String>,
        #[arg(short = 'd', long)]
        delete: bool,
    },
    /// Merge a ref into the current branch (3-way).
    Merge { branch: String },
    /// Verify object integrity + reachability.
    Fsck,
    /// Consolidate objects into one pack and prune unreachable.
    Gc,
    /// Create a new commit that undoes <commit>.
    Revert { commit: String },
    /// Apply <commit>'s change onto HEAD.
    CherryPick { commit: String },
    /// Replay current commits onto <upstream>.
    Rebase { upstream: String },
    /// Shelve/restore the worktree: stash [push|pop|list|drop].
    Stash {
        #[arg(default_value = "push")]
        action: String,
    },
    /// Show a ref's movement log (`HEAD@{n}` / `<branch>@{n}` resolve against it).
    Reflog {
        /// Branch to show (default: HEAD).
        name: Option<String>,
    },
    /// Binary-search history for the first bad commit.
    Bisect {
        #[command(subcommand)]
        action: BisectCmd,
    },
    /// Walk a remote's history verifying object presence (+ integrity with --deep).
    VerifyRemote {
        #[arg(default_value = "origin")]
        remote: String,
        /// Also download and hash-check every leaf object.
        #[arg(long)]
        deep: bool,
    },
    /// Resolve a revision to its object id.
    RevParse { rev: String },
    /// Print an object's raw bytes to stdout.
    CatFile { id: String },
    /// Show a commit: metadata + files changed vs its first parent.
    Show { rev: String },
    /// List the files/regions in a snapshot.
    LsTree { rev: String },
    /// Create, list, verify, or delete tags (-a/-s/-m create annotated tags).
    Tag {
        name: Option<String>,
        rev: Option<String>,
        #[arg(short = 'd', long)]
        delete: bool,
        /// Create an annotated tag object (implied by -m / -s).
        #[arg(short = 'a', long)]
        annotate: bool,
        /// Sign the annotated tag with SSH (uses `user.signingkey`).
        #[arg(short = 's', long)]
        sign: bool,
        /// The annotated tag's message.
        #[arg(short = 'm', long)]
        message: Option<String>,
        /// Verify the named tag's SSH signature (exit 0 only when the signer
        /// matches `gpg.ssh.allowedSignersFile`).
        #[arg(short = 'v', long)]
        verify: bool,
        /// Replace the tag if it already exists.
        #[arg(short = 'f', long)]
        force: bool,
        /// When listing, show each annotated tag's message.
        #[arg(short = 'n')]
        show_message: bool,
    },
    /// Verify a commit's SSH signature (exit 0 only when the signer matches
    /// `gpg.ssh.allowedSignersFile`).
    VerifyCommit { rev: String },
    /// Move the current branch to <rev>. --hard also resets the worktree;
    /// --soft/--mixed move the ref only (mcagit has no staging index).
    Reset {
        rev: String,
        #[arg(long)]
        hard: bool,
        #[arg(long)]
        soft: bool,
        #[arg(long)]
        mixed: bool,
    },
    /// Restore worktree files from a revision, or unstage with --staged.
    Restore {
        /// Paths to restore (worktree files, or index entries with --staged).
        paths: Vec<String>,
        /// Restore the index entry (unstage) instead of the worktree file.
        #[arg(long)]
        staged: bool,
        /// Source revision (default: HEAD).
        #[arg(long, default_value = "HEAD")]
        source: String,
    },
    /// Remove untracked files from the worktree (-n preview, -f remove).
    Clean {
        #[arg(short = 'n')]
        dry_run: bool,
        #[arg(short = 'f')]
        force: bool,
    },
    /// Clone a repository (local path, http(s)://, or ssh://) into <dst>.
    Clone {
        src: String,
        dst: PathBuf,
        /// Shallow clone: fetch at most this many commits per branch
        /// (records a shallow boundary; tags are skipped).
        #[arg(long)]
        depth: Option<usize>,
        /// Partial clone: `--filter blob:none` fetches the commit/tree skeleton
        /// only; leaf chunks are backfilled on demand (e.g. at checkout).
        #[arg(long)]
        filter: Option<String>,
    },
    /// Push a branch to a remote (a configured remote name or a URL/path).
    Push {
        remote: String,
        branch: Option<String>,
    },
    /// Fetch a branch from a remote and fast-forward the worktree.
    Pull {
        remote: String,
        branch: Option<String>,
    },
    /// Fetch a branch from a remote into a remote-tracking ref (no worktree change).
    Fetch {
        remote: String,
        branch: Option<String>,
    },
    /// List the refs a remote advertises.
    LsRemote { remote: String },
    /// Manage named remotes (no subcommand lists them; -v shows URLs).
    Remote {
        #[arg(short = 'v', long)]
        verbose: bool,
        #[command(subcommand)]
        action: Option<RemoteCmd>,
    },
    /// Verify a world reproduces a commit (fast single-sided tree-hash check).
    Verify {
        reff: String,
        world: Option<PathBuf>,
    },
    /// List players (level.dat host + playerdata): position, dimension, health.
    Players {
        #[arg(long)]
        world: Option<PathBuf>,
        #[arg(long)]
        json: bool,
    },
    /// Find entities, block-entities, or signs in a world.
    Find {
        /// What to search for: entity | block-entity | sign
        kind: String,
        /// id to match (e.g. `zombie`, `chest`); omit to list all of that kind.
        id: Option<String>,
        #[arg(long)]
        world: Option<PathBuf>,
        #[arg(long)]
        dim: Option<String>,
        #[arg(long)]
        json: bool,
    },
    /// Inspect the block / biome / block-entity at world coords.
    #[command(allow_negative_numbers = true)]
    Inspect {
        x: i32,
        y: i32,
        z: i32,
        #[arg(long)]
        world: Option<PathBuf>,
        #[arg(long)]
        dim: Option<String>,
        #[arg(long)]
        json: bool,
    },
    /// Report block-level changes between two worlds (the grief detector).
    WhereChanged {
        old: PathBuf,
        new: PathBuf,
        #[arg(long)]
        dim: Option<String>,
        #[arg(long)]
        json: bool,
        #[arg(long)]
        verbose: bool,
    },
    /// Dump a region (.mca) file's per-chunk storage info.
    Region {
        file: PathBuf,
        #[arg(long)]
        json: bool,
    },
    /// List points of interest (villager beds, job sites, …).
    Poi {
        #[arg(long)]
        world: Option<PathBuf>,
        #[arg(long)]
        dim: Option<String>,
        #[arg(long)]
        json: bool,
    },
    /// Serve a directory of repos over HTTP at /r/<name>/ (the hub transport).
    Serve {
        /// Directory holding bare repos (served + auto-created under /r/<name>).
        root: PathBuf,
        #[arg(long, default_value = "127.0.0.1:5080")]
        addr: String,
    },
    /// Serve a single repo over stdin/stdout (the ssh transport's server side).
    ServeStdio { dir: PathBuf },
    /// Render a top-down surface map of a world to a PNG.
    Render {
        world: Option<PathBuf>,
        #[arg(short = 'o', long, default_value = "map.png")]
        out: PathBuf,
        #[arg(long)]
        dim: Option<String>,
        #[arg(long, default_value_t = 10_000)]
        max_chunks: usize,
    },
}

#[derive(Subcommand)]
enum BisectCmd {
    /// Start a session: `bisect start [<bad> [<good>...]]`.
    Start {
        bad: Option<String>,
        good: Vec<String>,
    },
    /// Mark a commit (default HEAD) as bad.
    Bad { rev: Option<String> },
    /// Mark commits (default HEAD) as good.
    Good { revs: Vec<String> },
    /// Skip a commit (default HEAD) — untestable, exclude it.
    Skip { rev: Option<String> },
    /// End the session and return to where you started.
    Reset,
    /// Print the session log.
    Log,
}

#[derive(Subcommand)]
enum RemoteCmd {
    /// Add a remote with a name and URL/path.
    Add { name: String, url: String },
    /// Remove a remote and its tracking refs.
    Remove { name: String },
    /// Rename a remote.
    Rename { old: String, new: String },
    /// Set an existing remote's URL.
    SetUrl { name: String, url: String },
    /// Print a remote's URL.
    GetUrl { name: String },
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    match run(cli) {
        Ok(code) => code,
        Err(e) => {
            eprintln!("mcagit: {e:#}");
            ExitCode::from(2)
        }
    }
}

fn run(cli: Cli) -> anyhow::Result<ExitCode> {
    match &cli.cmd {
        Cmd::Init { dir, worktree } => {
            let dir = dir
                .clone()
                .or_else(|| cli.repo.clone())
                .unwrap_or_else(|| PathBuf::from("."));
            let repo = Repository::init(&dir)?;
            if let Some(w) = worktree {
                let w = std::fs::canonicalize(w).unwrap_or_else(|_| w.clone());
                repo.set_worktree(&w.to_string_lossy())?;
            }
            eprintln!("Initialized empty mcagit repository in {}", dir.display());
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Commit {
            message,
            sign,
            all,
            world,
        } => {
            let repo = open_repo(&cli)?;
            let worktree = repo.worktree().map(PathBuf::from);
            if mca_repo::hooks::run(&repo, "pre-commit") != 0 {
                bail!("pre-commit hook failed; commit aborted");
            }
            let auto = repo
                .config_get("commit.autoStageAll")
                .is_some_and(|v| v.eq_ignore_ascii_case("true"));
            // Whole-worktree snapshot when -a / autoStageAll / an explicit path;
            // otherwise commit the staging index.
            let whole = *all || auto || world.is_some();

            let manifest = if whole {
                let dir = world
                    .clone()
                    .or_else(|| worktree.clone())
                    .ok_or_else(|| anyhow!("no world given and no worktree bound"))?;
                if std::io::stderr().is_terminal() {
                    let m = snapshot::snapshot_with_progress(&repo, &dir, &|done, total| {
                        eprint!("\rsnapshot: {done}/{total} files");
                    })?;
                    eprint!("\r\x1b[K");
                    m
                } else {
                    snapshot::snapshot(&repo, &dir)?
                }
            } else {
                mca_repo::index::effective(&repo)?
            };

            let tree = repo.write_manifest(&manifest)?;
            let head = repo.head_commit();
            let head_tree = match &head {
                Some(h) => Some(repo.read_commit(h)?.tree),
                None => None,
            };

            // Guardrail: is there anything new to commit?
            let nothing_new = head_tree.as_deref() == Some(tree.as_str())
                || (head.is_none() && manifest == mca_repo::Manifest::default());
            if nothing_new {
                if !whole {
                    let dirty = match (&worktree, &head) {
                        (Some(wt), Some(h)) => {
                            !mca_repo::status(&repo, std::path::Path::new(wt), h)?.is_empty()
                        }
                        (Some(wt), None) => std::fs::read_dir(wt)
                            .map(|mut it| it.next().is_some())
                            .unwrap_or(false),
                        _ => false,
                    };
                    if dirty {
                        bail!("nothing staged for commit. use `mcagit add <path>` or `commit -a`.");
                    }
                }
                if head.is_none() {
                    eprintln!("nothing to commit — nothing staged");
                } else {
                    eprintln!("nothing to commit — world matches HEAD");
                }
                return Ok(ExitCode::SUCCESS);
            }

            let parents: Vec<String> = head.clone().into_iter().collect();
            let sign_fn = signer(&repo, *sign)?;
            let commit = repo.create_commit_signed(
                &tree,
                parents,
                message,
                &author(&repo),
                &now_secs(),
                sign_fn.as_deref(),
            )?;
            match repo.current_branch() {
                Some(b) => repo.write_branch(&b, &commit)?,
                None => repo.set_head_detached(&commit)?,
            }
            repo.record_head(head.as_deref(), &commit, &format!("commit: {message}"))?;
            // The commit came from the bound worktree (index, or -a/autoStageAll) →
            // its index is now clean. Don't touch it when committing an explicit
            // external <world> path that has nothing to do with the index.
            if world.is_none() {
                mca_repo::index::clear(&repo)?;
            }
            mca_repo::hooks::run(&repo, "post-commit");
            let files = manifest.regions.len() + manifest.nbt.len() + manifest.blobs.len();
            let chunks: usize = manifest.regions.values().map(|c| c.len()).sum();
            eprintln!(
                "[{} {}] {}  ({files} files, {chunks} chunks)",
                repo.current_branch().unwrap_or_else(|| "detached".into()),
                &commit[..10],
                message
            );
            println!("{commit}"); // stdout: the new commit id (scriptable)
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Add { pathspecs, all } => {
            let repo = open_repo(&cli)?;
            let world = repo
                .worktree()
                .map(PathBuf::from)
                .ok_or_else(|| anyhow!("no worktree bound"))?;
            let specs: Vec<String> = if *all {
                vec![".".to_string()]
            } else {
                pathspecs.clone()
            };
            if specs.is_empty() {
                bail!("nothing specified — give a pathspec or use -A");
            }
            let n = mca_repo::index::add_paths(&repo, &world, &specs)?;
            eprintln!("staged {n} path(s)");
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Checkout {
            reff,
            out,
            force,
            regions,
        } => {
            let repo = open_repo(&cli)?;
            let commit = repo.resolve_ref(reff)?;
            let out = out
                .clone()
                .or_else(|| repo.worktree().map(PathBuf::from))
                .ok_or_else(|| anyhow!("no <world-out> given and no worktree bound"))?;
            let nonempty = out
                .read_dir()
                .map(|mut d| d.next().is_some())
                .unwrap_or(false);
            let is_worktree = repo.worktree().map(PathBuf::from).as_deref() == Some(out.as_path());
            if !force && nonempty && !is_worktree {
                bail!(
                    "output directory is not empty: {} (use --force)",
                    out.display()
                );
            }
            let mut manifest = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
            // Sparse checkout: keep only the requested region coordinates.
            if !regions.is_empty() {
                let want = parse_region_coords(regions)?;
                manifest = manifest.select_regions(&want);
            }
            // Partial clone: backfill the leaf objects this checkout needs.
            if repo.is_partial() {
                let need = mca_repo::fsck::manifest_ids(&manifest);
                let n = mca_repo::remote::backfill(&repo, &need)?;
                if n > 0 {
                    eprintln!("backfilled {n} objects from the promisor remote");
                }
            }
            // A sparse checkout would prune the regions it omits; only prune on a
            // full checkout so `--region` doesn't delete the rest of a worktree.
            let prune = regions.is_empty();
            mca_repo::checkout(&repo, &manifest, &out, prune)?;
            let old_head = repo.head_commit();
            if repo.read_branch(reff).is_some() {
                repo.set_head_to_branch(reff)?;
            } else {
                repo.set_head_detached(&commit)?;
            }
            repo.record_head(
                old_head.as_deref(),
                &commit,
                &format!("checkout: moving to {reff}"),
            )?;
            eprintln!(
                "Checked out {reff} ({}) into {}",
                &commit[..10],
                out.display()
            );
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Status => {
            let repo = open_repo(&cli)?;
            let world = repo
                .worktree()
                .map(PathBuf::from)
                .ok_or_else(|| anyhow!("no worktree bound"))?;
            let r = mca_repo::status_full(&repo, &world)?;
            if r.staged.is_empty() && r.unstaged.is_empty() && r.untracked.is_empty() {
                eprintln!("nothing to commit, working tree clean");
                return Ok(ExitCode::SUCCESS);
            }
            let tag = |k: &ChangeKind| match k {
                ChangeKind::Added => "A",
                ChangeKind::Modified => "M",
                ChangeKind::Removed => "D",
            };
            // Porcelain-style listing goes to stdout (scriptable); the clean-tree
            // note above is a stderr message.
            if !r.staged.is_empty() {
                println!("Changes staged for commit:");
                for c in &r.staged {
                    println!("  {} {}", tag(&c.kind), c.path);
                }
            }
            if !r.unstaged.is_empty() {
                println!("Changes not staged for commit:");
                for c in &r.unstaged {
                    println!("  {} {}", tag(&c.kind), c.path);
                }
            }
            if !r.untracked.is_empty() {
                println!("Untracked files:");
                for p in &r.untracked {
                    println!("  {p}");
                }
            }
            Ok(ExitCode::from(1))
        }

        Cmd::Log {
            oneline,
            author,
            grep,
            since,
            until,
        } => {
            let repo = open_repo(&cli)?;
            let mut cur = repo.head_commit();
            while let Some(h) = cur {
                let c = repo.read_commit(&h)?;
                let ts = c.time.parse::<i64>().ok();
                let keep = author.as_deref().is_none_or(|a| c.author.contains(a))
                    && grep.as_deref().is_none_or(|g| c.message.contains(g))
                    && since.is_none_or(|s| ts.is_none_or(|t| t >= s))
                    && until.is_none_or(|u| ts.is_none_or(|t| t <= u));
                if keep {
                    if *oneline {
                        println!("{} {}", &h[..10], c.message.lines().next().unwrap_or(""));
                    } else {
                        println!(
                            "commit {h}\nAuthor: {}\nDate:   {}\n\n    {}\n",
                            c.author, c.time, c.message
                        );
                    }
                }
                cur = repo.parents_of(&h)?.into_iter().next();
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Config { key, value } => {
            let repo = open_repo(&cli)?;
            match value {
                Some(v) => repo.config_set(key, v)?,
                None => match repo.config_get(key) {
                    Some(v) => println!("{v}"),
                    None => return Ok(ExitCode::from(1)),
                },
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Show { rev } => {
            let repo = open_repo(&cli)?;
            let h = repo.resolve_ref(rev)?;
            let c = repo.read_commit(&h)?;
            println!(
                "commit {h}\nAuthor: {}\nDate:   {}\n\n    {}\n",
                c.author, c.time, c.message
            );
            let m = repo.read_manifest(&c.tree)?;
            let parent = match repo.parents_of(&h)?.first() {
                Some(p) => Some(repo.read_manifest(&repo.read_commit(p)?.tree)?),
                None => None,
            };
            for (st, path) in manifest_changes(&m, parent.as_ref()) {
                println!("  {st} {path}");
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Diff { a, b, json } => {
            let wd = mca_diff::world::diff(a, b)?;
            if *json {
                let files: Vec<_> = wd
                    .files
                    .iter()
                    .map(|f| {
                        serde_json::json!({
                            "path": f.path,
                            "status": format!("{:?}", f.status),
                            "chunks": f.chunks.iter().map(|c| serde_json::json!({
                                "x": c.x, "z": c.z,
                                "status": format!("{:?}", c.status),
                                "changes": c.changes.len(),
                                "blockEdits": c.block_edits.iter().map(|e| serde_json::json!({
                                    "x": e.x, "y": e.y, "z": e.z, "old": e.old, "new": e.new,
                                })).collect::<Vec<_>>(),
                            })).collect::<Vec<_>>(),
                            "nodeChanges": f.changes.iter().map(|c| serde_json::json!({
                                "path": c.path, "kind": format!("{:?}", c.kind),
                            })).collect::<Vec<_>>(),
                        })
                    })
                    .collect();
                println!(
                    "{}",
                    serde_json::to_string_pretty(&serde_json::json!({ "files": files }))?
                );
            } else {
                print!("{}", mca_diff::render(&wd));
            }
            Ok(if wd.is_empty() {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            })
        }

        Cmd::Extract { a, b, out } => {
            let patch = mca_patch::extract(a, b)?;
            std::fs::write(out, patch.to_json()?)?;
            eprintln!(
                "wrote patch ({} file entries) to {}",
                patch.files.len(),
                out.display()
            );
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Apply {
            patch,
            world,
            out,
            reverse,
            force,
        } => {
            let wp = WorldPatch::from_json(&std::fs::read_to_string(patch)?)?;
            let report = mca_patch::apply(&wp, world, out, *reverse, *force)?;
            if report.conflicts.is_empty() {
                eprintln!("applied {} ops into {}", report.applied, out.display());
                Ok(ExitCode::SUCCESS)
            } else {
                eprintln!("applied with {} conflicts:", report.conflicts.len());
                for c in &report.conflicts {
                    eprintln!("  {c}");
                }
                Ok(ExitCode::from(1))
            }
        }

        Cmd::Branch { name, delete } => {
            let repo = open_repo(&cli)?;
            match (name, *delete) {
                (Some(n), true) => {
                    repo.delete_branch(n)?;
                    eprintln!("Deleted branch {n}.");
                }
                (Some(n), false) => {
                    let head = repo
                        .head_commit()
                        .ok_or_else(|| anyhow!("no commit to branch from"))?;
                    repo.write_branch(n, &head)?;
                    eprintln!("Created branch {n} at {}", &head[..10]);
                }
                (None, _) => {
                    let cur = repo.current_branch();
                    for b in repo.branches() {
                        let mark = if Some(&b) == cur.as_ref() { "*" } else { " " };
                        println!("{mark} {b}");
                    }
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Merge { branch } => {
            let repo = open_repo(&cli)?;
            let ours = repo
                .head_commit()
                .ok_or_else(|| anyhow!("no HEAD to merge into"))?;
            let theirs = repo.resolve_ref(branch)?;
            let outcome = mca_repo::merge(
                &repo,
                &ours,
                &theirs,
                &format!("Merge {branch}"),
                &author(&repo),
                &now_secs(),
            )?;
            use mca_repo::MergeOutcome::*;
            match outcome {
                UpToDate => {
                    eprintln!("Already up to date.");
                    Ok(ExitCode::SUCCESS)
                }
                FastForward(t) | Merged(t) => {
                    match repo.current_branch() {
                        Some(b) => repo.write_branch(&b, &t)?,
                        None => repo.set_head_detached(&t)?,
                    }
                    repo.record_head(Some(&ours), &t, &format!("merge {branch}"))?;
                    if let Some(wt) = repo.worktree() {
                        let m = repo.read_manifest(&repo.read_commit(&t)?.tree)?;
                        mca_repo::checkout(&repo, &m, std::path::Path::new(&wt), true)?;
                    }
                    eprintln!("Merge complete -> {}", &t[..10]);
                    Ok(ExitCode::SUCCESS)
                }
                Conflicts(c) => {
                    eprintln!("CONFLICT ({} paths):", c.len());
                    for p in &c {
                        eprintln!("  {p}");
                    }
                    Ok(ExitCode::from(1))
                }
            }
        }

        Cmd::Fsck => {
            let repo = open_repo(&cli)?;
            let r = mca_repo::fsck(&repo)?;
            eprintln!(
                "checked {} objects — {} corrupt, {} missing, {} unreachable",
                r.checked,
                r.corrupt.len(),
                r.missing.len(),
                r.unreachable
            );
            if r.promised > 0 {
                eprintln!("  {} leaf objects promised (partial clone)", r.promised);
            }
            Ok(if r.is_clean() {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            })
        }

        Cmd::Gc => {
            let repo = open_repo(&cli)?;
            let r = mca_repo::gc(&repo)?;
            eprintln!("gc: kept {} objects, pruned {}", r.kept, r.pruned);
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Revert { commit } => {
            let repo = open_repo(&cli)?;
            let head = repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?;
            let target = repo.resolve_ref(commit)?;
            match mca_repo::revert(&repo, &head, &target, &author(&repo), &now_secs())? {
                mca_repo::ReplayOutcome::Done(c) => {
                    advance(&repo, &c, &format!("revert: {commit}"))?;
                    eprintln!("Reverted -> {}", &c[..10]);
                    Ok(ExitCode::SUCCESS)
                }
                mca_repo::ReplayOutcome::Conflicts(x) => {
                    print_conflicts(&x);
                    Ok(ExitCode::from(1))
                }
            }
        }

        Cmd::CherryPick { commit } => {
            let repo = open_repo(&cli)?;
            let head = repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?;
            let pick = repo.resolve_ref(commit)?;
            match mca_repo::cherry_pick(&repo, &head, &pick, &now_secs())? {
                mca_repo::ReplayOutcome::Done(c) => {
                    advance(&repo, &c, &format!("cherry-pick: {commit}"))?;
                    eprintln!("Cherry-picked -> {}", &c[..10]);
                    Ok(ExitCode::SUCCESS)
                }
                mca_repo::ReplayOutcome::Conflicts(x) => {
                    print_conflicts(&x);
                    Ok(ExitCode::from(1))
                }
            }
        }

        Cmd::Rebase { upstream } => {
            let repo = open_repo(&cli)?;
            let head = repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?;
            let up = repo.resolve_ref(upstream)?;
            match mca_repo::rebase(&repo, &up, &head, &now_secs())? {
                mca_repo::ReplayOutcome::Done(c) => {
                    advance(&repo, &c, &format!("rebase onto {upstream}"))?;
                    eprintln!("Rebased -> {}", &c[..10]);
                    Ok(ExitCode::SUCCESS)
                }
                mca_repo::ReplayOutcome::Conflicts(x) => {
                    print_conflicts(&x);
                    Ok(ExitCode::from(1))
                }
            }
        }

        Cmd::Stash { action } => {
            let repo = open_repo(&cli)?;
            match action.as_str() {
                "list" => {
                    for s in mca_repo::stash::list(&repo) {
                        println!("{}", &s[..s.len().min(10)]);
                    }
                    Ok(ExitCode::SUCCESS)
                }
                "pop" => {
                    let wt = repo
                        .worktree()
                        .ok_or_else(|| anyhow!("no worktree bound"))?;
                    match mca_repo::stash::pop(&repo, std::path::Path::new(&wt))? {
                        Some(s) => eprintln!("popped {}", &s[..10]),
                        None => eprintln!("stash empty"),
                    }
                    Ok(ExitCode::SUCCESS)
                }
                "push" => {
                    let wt = repo
                        .worktree()
                        .ok_or_else(|| anyhow!("no worktree bound"))?;
                    match mca_repo::stash::push(
                        &repo,
                        std::path::Path::new(&wt),
                        &author(&repo),
                        &now_secs(),
                    )? {
                        Some(s) => eprintln!("stashed {}", &s[..10]),
                        None => eprintln!("nothing to stash"),
                    }
                    Ok(ExitCode::SUCCESS)
                }
                "drop" => {
                    match mca_repo::stash::drop_top(&repo)? {
                        Some(s) => eprintln!("dropped {}", &s[..10]),
                        None => eprintln!("stash empty"),
                    }
                    Ok(ExitCode::SUCCESS)
                }
                other => Err(anyhow!("unknown stash action: {other}")),
            }
        }

        Cmd::Reflog { name } => {
            let repo = open_repo(&cli)?;
            let (what, entries) = match name.as_deref() {
                Some(b) => (b, repo.branch_reflog(b)),
                None => ("HEAD", repo.reflog()),
            };
            for (n, line) in entries.iter().enumerate() {
                // "<from> <to> <message>"
                let mut parts = line.splitn(3, ' ');
                let _from = parts.next().unwrap_or("");
                let to = parts.next().unwrap_or("");
                let msg = parts.next().unwrap_or("");
                println!("{} {what}@{{{n}}}: {msg}", &to[..10.min(to.len())]);
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Bisect { action } => bisect_cmd(&cli, action),

        Cmd::VerifyRemote { remote, deep } => {
            let repo = open_repo(&cli)?;
            let url = mca_repo::remote::resolve(&repo, remote);
            let t = mca_repo::connect(&url)?;
            let r = mca_repo::verify_remote(t.as_ref(), *deep)?;
            eprintln!(
                "{remote}: {} branch(es), {} commit(s), {} object(s) checked{}",
                r.branches,
                r.commits,
                r.objects,
                if *deep { " (deep)" } else { "" }
            );
            for m in &r.missing {
                eprintln!("  missing: {m}");
            }
            for c in &r.corrupt {
                eprintln!("  corrupt: {c}");
            }
            if r.is_ok() {
                eprintln!("{remote}: ok");
                Ok(ExitCode::SUCCESS)
            } else {
                eprintln!(
                    "{remote}: {} missing, {} corrupt",
                    r.missing.len(),
                    r.corrupt.len()
                );
                Ok(ExitCode::from(1))
            }
        }

        Cmd::RevParse { rev } => {
            let repo = open_repo(&cli)?;
            println!("{}", repo.resolve_ref(rev)?);
            Ok(ExitCode::SUCCESS)
        }

        Cmd::CatFile { id } => {
            use std::io::Write;
            let repo = open_repo(&cli)?;
            // A tag name resolves to the tag ref's own target (the tag object
            // for an annotated tag) so the object itself can be inspected;
            // everything else goes through normal (peeling) resolution.
            let resolved = repo
                .read_tag(id)
                .or_else(|| repo.resolve_ref(id).ok())
                .unwrap_or_else(|| id.clone());
            let bytes = repo.objects().read(&resolved)?;
            std::io::stdout().write_all(&bytes)?;
            Ok(ExitCode::SUCCESS)
        }

        Cmd::LsTree { rev } => {
            let repo = open_repo(&cli)?;
            let commit = repo.resolve_ref(rev)?;
            let m = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
            for (rel, chunks) in &m.regions {
                println!("region {rel} ({} chunks)", chunks.len());
            }
            for rel in m.nbt.keys() {
                println!("nbt    {rel}");
            }
            for rel in m.blobs.keys() {
                println!("blob   {rel}");
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Tag {
            name,
            rev,
            delete,
            annotate,
            sign,
            message,
            verify,
            force,
            show_message,
        } => {
            let repo = open_repo(&cli)?;
            if *delete {
                let n = name
                    .as_ref()
                    .ok_or_else(|| anyhow!("usage: tag -d <name>"))?;
                repo.delete_tag(n)?;
                eprintln!("Deleted tag {n}.");
                return Ok(ExitCode::SUCCESS);
            }
            if *verify {
                let n = name
                    .as_ref()
                    .ok_or_else(|| anyhow!("usage: tag -v <name>"))?;
                let tag = repo
                    .read_annotated_tag(n)
                    .ok_or_else(|| anyhow!("{n} is not an annotated tag"))?;
                return Ok(report_signature(
                    &repo,
                    n,
                    &tag.signable_payload()?,
                    tag.signature.as_deref(),
                ));
            }
            let Some(n) = name else {
                for t in repo.tags() {
                    match (*show_message, repo.read_annotated_tag(&t)) {
                        (true, Some(at)) => {
                            println!("{t:<15} {}", at.message.lines().next().unwrap_or(""))
                        }
                        _ => println!("{t}"),
                    }
                }
                return Ok(ExitCode::SUCCESS);
            };
            let commit = match rev {
                Some(r) => repo.resolve_ref(r)?,
                None => repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?,
            };
            if repo.read_tag(n).is_some() && !force {
                bail!("tag already exists: {n} (use -f to overwrite)");
            }
            let annotated = *sign || *annotate || message.is_some();
            if !annotated {
                repo.write_tag(n, &commit)?;
                eprintln!("tag {n} -> {}", &commit[..10]);
                return Ok(ExitCode::SUCCESS);
            }
            let msg = message
                .as_ref()
                .ok_or_else(|| anyhow!("annotated tag requires -m <message>"))?;
            let mut tag = mca_repo::TagObject {
                object: commit.clone(),
                kind: "commit".into(),
                tag: n.clone(),
                tagger: author(&repo),
                time: now_secs(),
                message: msg.clone(),
                signature: None,
            };
            if *sign {
                let sign_fn = signer(&repo, true)?.expect("signer present when sign=true");
                tag.signature = Some(sign_fn(&tag.signable_payload()?)?);
            }
            let h = repo.write_annotated_tag(&tag)?;
            eprintln!(
                "Created {}annotated tag {n} at {} (tag {})",
                if *sign { "signed " } else { "" },
                &commit[..10],
                &h[..10]
            );
            Ok(ExitCode::SUCCESS)
        }

        Cmd::VerifyCommit { rev } => {
            let repo = open_repo(&cli)?;
            let h = repo.resolve_ref(rev)?;
            let c = repo.read_commit(&h)?;
            Ok(report_signature(
                &repo,
                &h[..10],
                &c.signable_payload()?,
                c.signature.as_deref(),
            ))
        }

        Cmd::Reset {
            rev, hard, soft, ..
        } => {
            let repo = open_repo(&cli)?;
            let target = repo.resolve_ref(rev)?;
            let old_head = repo.head_commit();
            match repo.current_branch() {
                Some(b) => repo.write_branch(&b, &target)?,
                None => repo.set_head_detached(&target)?,
            }
            repo.record_head(
                old_head.as_deref(),
                &target,
                &format!("reset: moving to {rev}"),
            )?;
            if !*soft {
                mca_repo::index::clear(&repo)?; // mixed/hard reset the index to HEAD
            }
            if *hard {
                if let Some(wt) = repo.worktree() {
                    let m = repo.read_manifest(&repo.read_commit(&target)?.tree)?;
                    mca_repo::checkout(&repo, &m, std::path::Path::new(&wt), true)?;
                }
            }
            eprintln!("reset to {}", &target[..10]);
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Restore {
            paths,
            staged,
            source,
        } => {
            let repo = open_repo(&cli)?;
            let commit = repo.resolve_ref(source)?;
            let full = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
            if *staged {
                // Unstage: reset these index entries to their <source> state.
                let mut idx = mca_repo::index::effective(&repo)?;
                for p in paths {
                    idx.regions.remove(p);
                    idx.nbt.remove(p);
                    idx.blobs.remove(p);
                    if let Some(c) = full.regions.get(p) {
                        idx.regions.insert(p.clone(), c.clone());
                    }
                    if let Some(h) = full.nbt.get(p) {
                        idx.nbt.insert(p.clone(), h.clone());
                    }
                    if let Some(h) = full.blobs.get(p) {
                        idx.blobs.insert(p.clone(), h.clone());
                    }
                }
                // If unstaging left the index equal to HEAD, the index is clean —
                // represent that as the file's absence, not a HEAD-equal file.
                if idx == mca_repo::index::head_tree(&repo)? {
                    mca_repo::index::clear(&repo)?;
                } else {
                    mca_repo::index::write(&repo, &idx)?;
                }
                eprintln!("unstaged {} path(s)", paths.len());
            } else {
                let wt = repo
                    .worktree()
                    .ok_or_else(|| anyhow!("no worktree bound"))?;
                let mut sub = mca_repo::Manifest::default();
                for p in paths {
                    if let Some(c) = full.regions.get(p) {
                        sub.regions.insert(p.clone(), c.clone());
                    }
                    if let Some(h) = full.nbt.get(p) {
                        sub.nbt.insert(p.clone(), h.clone());
                    }
                    if let Some(h) = full.blobs.get(p) {
                        sub.blobs.insert(p.clone(), h.clone());
                    }
                }
                mca_repo::checkout(&repo, &sub, std::path::Path::new(&wt), false)?;
                eprintln!("restored {} path(s) from {}", paths.len(), &commit[..10]);
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Clean { dry_run, force } => {
            let repo = open_repo(&cli)?;
            let wt = repo
                .worktree()
                .ok_or_else(|| anyhow!("no worktree bound"))?;
            let untracked = mca_repo::status_full(&repo, std::path::Path::new(&wt))?.untracked;
            if untracked.is_empty() {
                eprintln!("nothing to clean");
                return Ok(ExitCode::SUCCESS);
            }
            for p in &untracked {
                if *force && !*dry_run {
                    let _ = std::fs::remove_file(std::path::Path::new(&wt).join(p));
                    println!("removed {p}");
                } else {
                    println!("would remove {p}");
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Clone {
            src,
            dst,
            depth,
            filter,
        } => {
            let suffix = match filter.as_deref() {
                Some("blob:none") => {
                    if depth.is_some() {
                        bail!("--filter and --depth cannot be combined");
                    }
                    mca_repo::remote::clone_partial(src, dst)?;
                    " (partial: blob:none)".to_string()
                }
                Some(other) => bail!("unsupported filter: {other} (only blob:none)"),
                None => {
                    mca_repo::remote::clone_depth(src, dst, depth.unwrap_or(0))?;
                    depth.map(|d| format!(" (depth {d})")).unwrap_or_default()
                }
            };
            eprintln!("Cloned {src} -> {}{suffix}", dst.display());
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Push { remote, branch } => {
            let repo = open_repo(&cli)?;
            let branch = branch
                .clone()
                .or_else(|| repo.current_branch())
                .ok_or_else(|| anyhow!("no branch to push"))?;
            let url = mca_repo::remote::resolve(&repo, remote);
            let t = mca_repo::connect(&url)?;
            let copied = mca_repo::remote::push(&repo, t.as_ref(), &branch)?;
            eprintln!("pushed {branch} -> {remote} ({copied} objects)");
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Fetch { remote, branch } => {
            let repo = open_repo(&cli)?;
            let branch = branch
                .clone()
                .or_else(|| repo.current_branch())
                .ok_or_else(|| anyhow!("no branch to fetch"))?;
            let url = mca_repo::remote::resolve(&repo, remote);
            let t = mca_repo::connect(&url)?;
            let (tip, copied) = mca_repo::remote::fetch(&repo, t.as_ref(), &branch)?;
            repo.write_remote_ref(remote, &branch, &tip)?;
            eprintln!(
                "fetched {branch} from {remote} -> refs/remotes/{remote}/{branch} ({copied} objects)"
            );
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Pull { remote, branch } => {
            let repo = open_repo(&cli)?;
            let branch = branch
                .clone()
                .or_else(|| repo.current_branch())
                .ok_or_else(|| anyhow!("no branch to pull"))?;
            let url = mca_repo::remote::resolve(&repo, remote);
            let t = mca_repo::connect(&url)?;
            let (tip, copied) = mca_repo::remote::fetch(&repo, t.as_ref(), &branch)?;
            repo.write_remote_ref(remote, &branch, &tip)?;
            // Fast-forward the local branch and, if it's current, the worktree.
            let old_head = repo.head_commit();
            repo.write_branch(&branch, &tip)?;
            if repo.current_branch().as_deref() == Some(branch.as_str()) {
                repo.record_head(old_head.as_deref(), &tip, &format!("pull: {remote}"))?;
            }
            if repo.current_branch().as_deref() == Some(branch.as_str()) {
                if let Some(wt) = repo.worktree() {
                    let m = repo.read_manifest(&repo.read_commit(&tip)?.tree)?;
                    mca_repo::checkout(&repo, &m, std::path::Path::new(&wt), true)?;
                }
            }
            eprintln!("pulled {branch} from {remote} ({copied} objects)");
            Ok(ExitCode::SUCCESS)
        }

        Cmd::LsRemote { remote } => {
            let repo = open_repo(&cli)?;
            let url = mca_repo::remote::resolve(&repo, remote);
            let t = mca_repo::connect(&url)?;
            for (refname, hash) in t.list_refs()? {
                println!("{hash}\t{refname}");
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Remote { verbose, action } => {
            let repo = open_repo(&cli)?;
            match action {
                None => {
                    for name in repo.remotes() {
                        if *verbose {
                            let url = repo.remote_url(&name).unwrap_or_default();
                            println!("{name}\t{url}");
                        } else {
                            println!("{name}");
                        }
                    }
                }
                Some(RemoteCmd::Add { name, url }) => repo.set_remote_url(name, url)?,
                Some(RemoteCmd::Remove { name }) => repo.remove_remote(name)?,
                Some(RemoteCmd::Rename { old, new }) => repo.rename_remote(old, new)?,
                Some(RemoteCmd::SetUrl { name, url }) => repo.set_remote_url(name, url)?,
                Some(RemoteCmd::GetUrl { name }) => {
                    let url = repo
                        .remote_url(name)
                        .ok_or_else(|| anyhow!("no such remote: {name}"))?;
                    println!("{url}");
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Verify { reff, world } => {
            let repo = open_repo(&cli)?;
            let commit = repo.resolve_ref(reff)?;
            let world = world
                .clone()
                .or_else(|| repo.worktree().map(PathBuf::from))
                .ok_or_else(|| anyhow!("no world given and no worktree bound"))?;
            let (ok, candidate, target) = mca_repo::verify_commit(&repo, &world, &commit)?;
            if ok {
                eprintln!(
                    "OK — {} reproduces {} (tree {})",
                    world.display(),
                    &commit[..10],
                    &target[..10]
                );
                Ok(ExitCode::SUCCESS)
            } else {
                eprintln!(
                    "MISMATCH — world tree {} != commit {} tree {}",
                    &candidate[..10],
                    &commit[..10],
                    &target[..10]
                );
                Ok(ExitCode::from(1))
            }
        }

        Cmd::Players { world, json } => {
            let world = resolve_world(&cli, world)?;
            let players = mca_query::WorldQuery::new(&world).players()?;
            if *json {
                println!("{}", serde_json::to_string_pretty(&players)?);
            } else {
                for p in &players {
                    let pos = p
                        .pos
                        .map(|[x, y, z]| format!("({x:.1}, {y:.1}, {z:.1})"))
                        .unwrap_or_else(|| "?".into());
                    let dim = p.dimension.as_deref().unwrap_or("?");
                    let hp = p
                        .health
                        .map(|h| format!("{h:.0}"))
                        .unwrap_or_else(|| "?".into());
                    println!("{}  {pos}  {dim}  hp={hp}", p.id);
                }
                eprintln!("{} player(s)", players.len());
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Find {
            kind,
            id,
            world,
            dim,
            json,
        } => {
            let world = resolve_world(&cli, world)?;
            let q = mca_query::WorldQuery::new(&world);
            let dim = dim.as_deref();
            match kind.as_str() {
                "entity" | "e" => {
                    let hits = q.find_entities(dim, id.as_deref())?;
                    if *json {
                        println!("{}", serde_json::to_string_pretty(&hits)?);
                    } else {
                        for h in &hits {
                            let at = h
                                .pos
                                .map(|[x, y, z]| format!(" at ({x:.1}, {y:.1}, {z:.1})"))
                                .unwrap_or_default();
                            println!("{}{at}", h.id);
                        }
                        eprintln!("{} entity(ies)", hits.len());
                    }
                }
                "block-entity" | "block_entity" | "be" => {
                    print_block_entities(q.find_block_entities(dim, id.as_deref())?, *json)?;
                }
                "sign" | "signs" => {
                    print_block_entities(q.find_signs(dim)?, *json)?;
                }
                other => {
                    return Err(anyhow!(
                        "unknown find kind '{other}' (use: entity | block-entity | sign)"
                    ))
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Inspect {
            x,
            y,
            z,
            world,
            dim,
            json,
        } => {
            let world = resolve_world(&cli, world)?;
            let r = mca_query::WorldQuery::new(&world).inspect(dim.as_deref(), *x, *y, *z)?;
            if *json {
                println!("{}", serde_json::to_string_pretty(&r)?);
            } else {
                println!("({}, {}, {})", r.x, r.y, r.z);
                println!("  block: {}", r.block.as_deref().unwrap_or("(unknown)"));
                if !r.properties.is_empty() {
                    let p: Vec<String> = r
                        .properties
                        .iter()
                        .map(|(k, v)| format!("{k}={v}"))
                        .collect();
                    println!("  props: {}", p.join(", "));
                }
                println!("  biome: {}", r.biome.as_deref().unwrap_or("(unknown)"));
                if let Some(be) = &r.block_entity {
                    println!("  block-entity: {be}");
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::WhereChanged {
            old,
            new,
            dim,
            json,
            verbose,
        } => {
            let changes = mca_query::where_changed(old, new, dim.as_deref())?;
            if *json {
                println!("{}", serde_json::to_string_pretty(&changes)?);
            } else {
                let limit = if *verbose { changes.len() } else { 20 };
                for c in changes.iter().take(limit) {
                    println!(
                        "({}, {}, {})  {} -> {}",
                        c.x,
                        c.y,
                        c.z,
                        c.old.as_deref().unwrap_or("-"),
                        c.new.as_deref().unwrap_or("-")
                    );
                }
                if changes.len() > limit {
                    eprintln!("... and {} more (use --verbose)", changes.len() - limit);
                }
                eprintln!("{} block change(s)", changes.len());
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Region { file, json } => {
            let chunks = mca_query::region_info(file)?;
            if *json {
                println!("{}", serde_json::to_string_pretty(&chunks)?);
            } else {
                for c in &chunks {
                    println!(
                        "chunk {},{}  comp={} {} bytes{}  ts={}",
                        c.x,
                        c.z,
                        c.compression,
                        c.bytes,
                        if c.external { " (external)" } else { "" },
                        c.timestamp
                    );
                }
                eprintln!("{} chunk(s)", chunks.len());
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Poi { world, dim, json } => {
            let world = resolve_world(&cli, world)?;
            let pois = mca_query::WorldQuery::new(&world).poi(dim.as_deref())?;
            if *json {
                println!("{}", serde_json::to_string_pretty(&pois)?);
            } else {
                for p in &pois {
                    println!("{}  at ({}, {}, {})", p.kind, p.x, p.y, p.z);
                }
                eprintln!("{} point(s) of interest", pois.len());
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Serve { root, addr } => {
            mca_repo::serve(root, addr)?;
            Ok(ExitCode::SUCCESS)
        }

        Cmd::ServeStdio { dir } => {
            mca_repo::serve_stdio(dir)?;
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Render {
            world,
            out,
            dim,
            max_chunks,
        } => {
            let world = resolve_world(&cli, world)?;
            let (png, info) = mca_query::render_map(&world, dim.as_deref(), *max_chunks)?;
            std::fs::write(out, &png)?;
            eprintln!(
                "rendered {}x{} ({} chunks{}) -> {}",
                info.width,
                info.height,
                info.chunks,
                if info.truncated { ", truncated" } else { "" },
                out.display()
            );
            Ok(ExitCode::SUCCESS)
        }
    }
}

/// Top-level paths changed between a manifest and its parent (A/D/M), sorted.
fn manifest_changes(
    new: &mca_repo::Manifest,
    old: Option<&mca_repo::Manifest>,
) -> Vec<(char, String)> {
    use std::collections::BTreeSet;
    let ident = |m: &mca_repo::Manifest, p: &str| -> Option<String> {
        if let Some(r) = m.regions.get(p) {
            return Some(format!("r:{r:?}"));
        }
        if let Some(h) = m.nbt.get(p) {
            return Some(format!("n:{h}"));
        }
        m.blobs.get(p).map(|h| format!("b:{h}"))
    };
    let mut all: BTreeSet<String> = BTreeSet::new();
    for m in std::iter::once(new).chain(old) {
        all.extend(m.regions.keys().cloned());
        all.extend(m.nbt.keys().cloned());
        all.extend(m.blobs.keys().cloned());
    }
    let mut out = Vec::new();
    for p in all {
        match (old.and_then(|m| ident(m, &p)), ident(new, &p)) {
            (None, Some(_)) => out.push(('A', p)),
            (Some(_), None) => out.push(('D', p)),
            (Some(a), Some(b)) if a != b => out.push(('M', p)),
            _ => {}
        }
    }
    out
}

/// Resolve the world to inspect: an explicit `--world`, else the bound worktree.
fn resolve_world(cli: &Cli, world: &Option<PathBuf>) -> anyhow::Result<PathBuf> {
    if let Some(w) = world {
        return Ok(w.clone());
    }
    let repo = open_repo(cli)?;
    repo.worktree()
        .map(PathBuf::from)
        .ok_or_else(|| anyhow!("no world given and no worktree bound; pass --world <path>"))
}

/// Parse `--region X,Z` values into a coordinate set for a sparse checkout.
fn parse_region_coords(specs: &[String]) -> anyhow::Result<std::collections::HashSet<(i32, i32)>> {
    specs
        .iter()
        .map(|s| {
            s.split_once(',')
                .and_then(|(x, z)| Some((x.trim().parse().ok()?, z.trim().parse().ok()?)))
                .ok_or_else(|| anyhow!("bad --region {s:?}: expected X,Z (e.g. 0,0 or -1,2)"))
        })
        .collect()
}

fn print_block_entities(hits: Vec<mca_query::BlockEntityHit>, json: bool) -> anyhow::Result<()> {
    if json {
        println!("{}", serde_json::to_string_pretty(&hits)?);
    } else {
        for h in &hits {
            print!("{} at {},{},{}", h.id, h.x, h.y, h.z);
            if h.text.is_empty() {
                println!();
            } else {
                println!("  [{}]", h.text.join(" | "));
            }
        }
        eprintln!("{} block-entity(ies)", hits.len());
    }
    Ok(())
}

fn advance(repo: &Repository, target: &str, log_message: &str) -> anyhow::Result<()> {
    let old_head = repo.head_commit();
    match repo.current_branch() {
        Some(b) => repo.write_branch(&b, target)?,
        None => repo.set_head_detached(target)?,
    }
    repo.record_head(old_head.as_deref(), target, log_message)?;
    if let Some(wt) = repo.worktree() {
        let m = repo.read_manifest(&repo.read_commit(target)?.tree)?;
        mca_repo::checkout(repo, &m, std::path::Path::new(&wt), true)?;
    }
    Ok(())
}

fn print_conflicts(paths: &[String]) {
    eprintln!("CONFLICT ({} paths):", paths.len());
    for p in paths {
        eprintln!("  {p}");
    }
}

fn open_repo(cli: &Cli) -> anyhow::Result<Repository> {
    match &cli.repo {
        Some(d) => Ok(Repository::open(d)?),
        None => Ok(Repository::discover(&std::env::current_dir()?)?),
    }
}

fn bisect_cmd(cli: &Cli, action: &BisectCmd) -> anyhow::Result<ExitCode> {
    use mca_repo::bisect;
    let repo = open_repo(cli)?;
    let require_session = || -> anyhow::Result<()> {
        if !bisect::in_bisect(&repo) {
            bail!("not bisecting (run `bisect start`)");
        }
        Ok(())
    };
    match action {
        BisectCmd::Start { bad, good } => {
            let original = repo
                .current_branch()
                .or_else(|| repo.head_commit())
                .ok_or_else(|| anyhow!("no commits to bisect"))?;
            bisect::start(&repo, &original)?;
            bisect::append_log(&repo, "# bisect start")?;
            if let Some(b) = bad {
                let b = repo.resolve_ref(b)?;
                bisect::set_bad(&repo, &b)?;
                bisect::append_log(&repo, &format!("bad {b}"))?;
            }
            for g in good {
                let g = repo.resolve_ref(g)?;
                bisect::add_good(&repo, &g)?;
                bisect::append_log(&repo, &format!("good {g}"))?;
            }
            bisect_advance(&repo)
        }
        BisectCmd::Bad { rev } => {
            require_session()?;
            let b = match rev {
                Some(r) => repo.resolve_ref(r)?,
                None => repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?,
            };
            bisect::set_bad(&repo, &b)?;
            bisect::append_log(&repo, &format!("bad {b}"))?;
            bisect_advance(&repo)
        }
        BisectCmd::Good { revs } => {
            require_session()?;
            let goods: Vec<String> = if revs.is_empty() {
                vec![repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?]
            } else {
                revs.iter()
                    .map(|r| repo.resolve_ref(r))
                    .collect::<Result<_, _>>()?
            };
            for g in goods {
                bisect::add_good(&repo, &g)?;
                bisect::append_log(&repo, &format!("good {g}"))?;
            }
            bisect_advance(&repo)
        }
        BisectCmd::Skip { rev } => {
            require_session()?;
            let s = match rev {
                Some(r) => repo.resolve_ref(r)?,
                None => repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?,
            };
            bisect::add_skip(&repo, &s)?;
            bisect::append_log(&repo, &format!("skip {s}"))?;
            bisect_advance(&repo)
        }
        BisectCmd::Reset => {
            require_session()?;
            let orig = bisect::original(&repo).ok_or_else(|| anyhow!("no bisect start point"))?;
            // Restore HEAD to the original branch (or detached commit) and the
            // worktree to match.
            let commit = match repo.read_branch(&orig) {
                Some(tip) => {
                    repo.set_head_to_branch(&orig)?;
                    tip
                }
                None => {
                    repo.set_head_detached(&orig)?;
                    orig.clone()
                }
            };
            if let Some(wt) = repo.worktree() {
                let m = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
                mca_repo::checkout(&repo, &m, std::path::Path::new(&wt), true)?;
            }
            bisect::clear(&repo)?;
            eprintln!("Bisect reset; back at {orig}.");
            Ok(ExitCode::SUCCESS)
        }
        BisectCmd::Log => {
            for line in bisect::log_lines(&repo) {
                println!("{line}");
            }
            Ok(ExitCode::SUCCESS)
        }
    }
}

/// Recompute the bisect state and check out the next suspect (detached).
fn bisect_advance(repo: &Repository) -> anyhow::Result<ExitCode> {
    use mca_repo::bisect;
    match bisect::compute(repo)? {
        bisect::State::NeedMarks => {
            eprintln!(
                "bisect: mark at least one bad and one good commit (`bisect bad` / `bisect good`)."
            );
            Ok(ExitCode::SUCCESS)
        }
        bisect::State::Done { first_bad } => {
            let c = repo.read_commit(&first_bad)?;
            println!("{first_bad} is the first bad commit");
            println!("    {}", c.message);
            Ok(ExitCode::SUCCESS)
        }
        bisect::State::Step { next, remaining } => {
            if let Some(wt) = repo.worktree() {
                let m = repo.read_manifest(&repo.read_commit(&next)?.tree)?;
                mca_repo::checkout(repo, &m, std::path::Path::new(&wt), true)?;
                let old_head = repo.head_commit();
                repo.set_head_detached(&next)?;
                repo.record_head(old_head.as_deref(), &next, "bisect: testing")?;
            }
            let steps = (remaining.max(1) as f64).log2().floor() as usize;
            eprintln!(
                "Bisecting: {remaining} revisions left to test after this (roughly {steps} steps); testing {}",
                &next[..10]
            );
            Ok(ExitCode::SUCCESS)
        }
    }
}

/// A signing closure when signing is requested (`-S` or `commit.gpgsign`),
/// else `None`. Errors if signing is requested without `user.signingkey`.
#[allow(clippy::type_complexity)]
fn signer(
    repo: &Repository,
    flag: bool,
) -> anyhow::Result<Option<Box<dyn Fn(&str) -> mca_repo::Result<String>>>> {
    let want = flag
        || repo
            .config_get("commit.gpgsign")
            .is_some_and(|v| v.eq_ignore_ascii_case("true"));
    if !want {
        return Ok(None);
    }
    let key = repo
        .config_get("user.signingkey")
        .ok_or_else(|| anyhow!("signing requested but user.signingkey is not set"))?;
    Ok(Some(Box::new(move |payload: &str| {
        mca_repo::sign::sign(payload, &key)
    })))
}

/// Verify an SSH signature with the repo's allowed-signers config and report
/// like git: exit 0 ONLY when the signer is verified against an
/// allowed-signers file. A merely well-formed signature (check-novalidate) is
/// NOT trust — exiting 0 there would fool a `… && deploy` gate into trusting
/// any throwaway key.
fn report_signature(
    repo: &Repository,
    what: &str,
    payload: &str,
    signature: Option<&str>,
) -> ExitCode {
    let Some(sig) = signature else {
        eprintln!("{what}: not signed");
        return ExitCode::from(1);
    };
    let allowed = repo.config_get("gpg.ssh.allowedSignersFile");
    let r = mca_repo::sign::verify(payload, sig, allowed.as_deref());
    eprintln!("{what}: {}", r.detail);
    if r.valid && !r.signer_verified {
        eprintln!(
            "{what}: signer NOT verified — set gpg.ssh.allowedSignersFile to establish trust; treating as unverified."
        );
    }
    if r.signer_verified {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}

fn author(repo: &Repository) -> String {
    match (repo.config_get("user.name"), repo.config_get("user.email")) {
        (Some(n), Some(e)) => format!("{n} <{e}>"),
        (Some(n), None) => n,
        _ => std::env::var("USER").unwrap_or_else(|_| "unknown".into()),
    }
}

fn now_secs() -> String {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs().to_string())
        .unwrap_or_default()
}

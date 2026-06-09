//! `mcagit` — minimal CLI for the Rust port (M3 subset: init/commit/checkout/status/log).

use anyhow::{anyhow, bail};
use clap::{Parser, Subcommand};
use mca_patch::WorldPatch;
use mca_repo::{snapshot, ChangeKind, Repository};
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
        world: Option<PathBuf>,
    },
    /// Materialize a snapshot into a directory (the worktree, or a given path).
    Checkout {
        reff: String,
        out: Option<PathBuf>,
        #[arg(long)]
        force: bool,
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
    /// Shelve/restore the worktree: stash [push|pop|list].
    Stash {
        #[arg(default_value = "push")]
        action: String,
    },
    /// Resolve a revision to its object id.
    RevParse { rev: String },
    /// Print an object's raw bytes to stdout.
    CatFile { id: String },
    /// Show a commit: metadata + files changed vs its first parent.
    Show { rev: String },
    /// List the files/regions in a snapshot.
    LsTree { rev: String },
    /// Create, list, or delete tags.
    Tag {
        name: Option<String>,
        rev: Option<String>,
        #[arg(short = 'd', long)]
        delete: bool,
    },
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
    /// Restore specific files from <rev> into the worktree.
    Restore { rev: String, paths: Vec<String> },
    /// Remove untracked files from the worktree (-n preview, -f remove).
    Clean {
        #[arg(short = 'n')]
        dry_run: bool,
        #[arg(short = 'f')]
        force: bool,
    },
    /// Clone a repository into <dst> (local path; http/ssh/cloud not yet implemented).
    Clone { src: String, dst: PathBuf },
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

        Cmd::Commit { message, world } => {
            let repo = open_repo(&cli)?;
            let world = world
                .clone()
                .or_else(|| repo.worktree().map(PathBuf::from))
                .ok_or_else(|| anyhow!("no world given and no worktree bound"))?;
            let manifest = snapshot::snapshot(&repo, &world)?;
            let tree = repo.write_manifest(&manifest)?;
            let head = repo.head_commit();
            if let Some(h) = &head {
                if repo.read_commit(h)?.tree == tree {
                    eprintln!("nothing to commit — world matches HEAD");
                    return Ok(ExitCode::SUCCESS);
                }
            }
            let parents: Vec<String> = head.clone().into_iter().collect();
            let commit =
                repo.create_commit(&tree, parents, message, &author(&repo), &now_secs())?;
            match repo.current_branch() {
                Some(b) => repo.write_branch(&b, &commit)?,
                None => repo.set_head_detached(&commit)?,
            }
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

        Cmd::Checkout { reff, out, force } => {
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
            let manifest = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
            mca_repo::checkout(&repo, &manifest, &out, true)?;
            if repo.read_branch(reff).is_some() {
                repo.set_head_to_branch(reff)?;
            } else {
                repo.set_head_detached(&commit)?;
            }
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
            let head = repo
                .head_commit()
                .ok_or_else(|| anyhow!("no commits yet"))?;
            let changes = mca_repo::status(&repo, &world, &head)?;
            if changes.is_empty() {
                eprintln!("clean — no changes vs HEAD");
                return Ok(ExitCode::SUCCESS);
            }
            for c in &changes {
                let tag = match c.kind {
                    ChangeKind::Added => "A",
                    ChangeKind::Modified => "M",
                    ChangeKind::Removed => "D",
                };
                println!("{tag} {}", c.path);
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
                cur = c.parents.into_iter().next();
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
            let parent = match c.parents.first() {
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
                    advance(&repo, &c)?;
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
                    advance(&repo, &c)?;
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
                    advance(&repo, &c)?;
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
                other => Err(anyhow!("unknown stash action: {other}")),
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
            let resolved = repo.resolve_ref(id).unwrap_or_else(|_| id.clone());
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

        Cmd::Tag { name, rev, delete } => {
            let repo = open_repo(&cli)?;
            match (name, rev, *delete) {
                (Some(n), _, true) => {
                    repo.delete_tag(n)?;
                    eprintln!("Deleted tag {n}.");
                }
                (Some(n), Some(r), false) => {
                    let c = repo.resolve_ref(r)?;
                    repo.write_tag(n, &c)?;
                    eprintln!("tag {n} -> {}", &c[..10]);
                }
                (Some(n), None, false) => {
                    let c = repo.head_commit().ok_or_else(|| anyhow!("no HEAD"))?;
                    repo.write_tag(n, &c)?;
                    eprintln!("tag {n} -> {}", &c[..10]);
                }
                (None, _, _) => {
                    for t in repo.tags() {
                        println!("{t}");
                    }
                }
            }
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Reset { rev, hard, .. } => {
            let repo = open_repo(&cli)?;
            let target = repo.resolve_ref(rev)?;
            match repo.current_branch() {
                Some(b) => repo.write_branch(&b, &target)?,
                None => repo.set_head_detached(&target)?,
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

        Cmd::Restore { rev, paths } => {
            let repo = open_repo(&cli)?;
            let wt = repo
                .worktree()
                .ok_or_else(|| anyhow!("no worktree bound"))?;
            let commit = repo.resolve_ref(rev)?;
            let full = repo.read_manifest(&repo.read_commit(&commit)?.tree)?;
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
            Ok(ExitCode::SUCCESS)
        }

        Cmd::Clean { dry_run, force } => {
            let repo = open_repo(&cli)?;
            let wt = repo
                .worktree()
                .ok_or_else(|| anyhow!("no worktree bound"))?;
            let head = repo
                .head_commit()
                .ok_or_else(|| anyhow!("no commits yet"))?;
            let changes = mca_repo::status(&repo, std::path::Path::new(&wt), &head)?;
            let untracked: Vec<&String> = changes
                .iter()
                .filter(|c| c.kind == ChangeKind::Added)
                .map(|c| &c.path)
                .collect();
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

        Cmd::Clone { src, dst } => {
            mca_repo::remote::clone(src, dst)?;
            eprintln!("Cloned {src} -> {}", dst.display());
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
            repo.write_branch(&branch, &tip)?;
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

fn advance(repo: &Repository, target: &str) -> anyhow::Result<()> {
    match repo.current_branch() {
        Some(b) => repo.write_branch(&b, target)?,
        None => repo.set_head_detached(target)?,
    }
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

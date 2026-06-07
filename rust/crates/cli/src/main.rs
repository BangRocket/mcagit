//! `mcagit` — minimal CLI for the Rust port (M3 subset: init/commit/checkout/status/log).

use anyhow::{anyhow, bail};
use clap::{Parser, Subcommand};
use mca_repo::{snapshot, ChangeKind, Repository};
use std::path::PathBuf;
use std::process::ExitCode;

#[derive(Parser)]
#[command(
    name = "mcagit",
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
    /// Show commit history.
    Log {
        #[arg(long)]
        oneline: bool,
    },
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

        Cmd::Log { oneline } => {
            let repo = open_repo(&cli)?;
            let mut cur = repo.head_commit();
            while let Some(h) = cur {
                let c = repo.read_commit(&h)?;
                if *oneline {
                    println!("{} {}", &h[..10], c.message.lines().next().unwrap_or(""));
                } else {
                    println!(
                        "commit {h}\nAuthor: {}\nDate:   {}\n\n    {}\n",
                        c.author, c.time, c.message
                    );
                }
                cur = c.parents.into_iter().next();
            }
            Ok(ExitCode::SUCCESS)
        }
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

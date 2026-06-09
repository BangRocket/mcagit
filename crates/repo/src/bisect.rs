//! Binary search over history for the first bad commit. The suspect set is the
//! commits reachable from `bad` but exonerated by no `good`; each step picks
//! the suspect that most evenly halves that set, until one remains.

use crate::repository::Repository;
use crate::Result;
use std::collections::HashSet;
use std::path::PathBuf;

/// Where the bisect session stands after recomputing.
#[derive(Debug)]
pub enum State {
    /// Mark at least one bad and one good commit first.
    NeedMarks,
    /// The first bad commit has been isolated.
    Done { first_bad: String },
    /// Test `next` (the best halving candidate); `remaining` candidates left.
    Step { next: String, remaining: usize },
}

fn path(repo: &Repository, name: &str) -> PathBuf {
    repo.dir().join(name)
}

fn read_lines(repo: &Repository, name: &str) -> Vec<String> {
    std::fs::read_to_string(path(repo, name))
        .unwrap_or_default()
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .map(str::to_string)
        .collect()
}

fn append_unique(repo: &Repository, name: &str, value: &str) -> Result<()> {
    let mut lines = read_lines(repo, name);
    if !lines.iter().any(|l| l == value) {
        lines.push(value.to_string());
        std::fs::write(path(repo, name), lines.join("\n") + "\n")?;
    }
    Ok(())
}

/// Begin a session, remembering where HEAD was (a branch name or commit).
pub fn start(repo: &Repository, original: &str) -> Result<()> {
    clear(repo)?;
    std::fs::write(path(repo, "BISECT_START"), format!("{original}\n"))?;
    Ok(())
}

pub fn in_bisect(repo: &Repository) -> bool {
    path(repo, "BISECT_START").is_file()
}

/// The branch/commit HEAD was on when the session started.
pub fn original(repo: &Repository) -> Option<String> {
    read_lines(repo, "BISECT_START").into_iter().next()
}

pub fn set_bad(repo: &Repository, commit: &str) -> Result<()> {
    std::fs::write(path(repo, "BISECT_BAD"), format!("{commit}\n"))?;
    Ok(())
}
pub fn bad(repo: &Repository) -> Option<String> {
    read_lines(repo, "BISECT_BAD").into_iter().next()
}
pub fn add_good(repo: &Repository, commit: &str) -> Result<()> {
    append_unique(repo, "BISECT_GOOD", commit)
}
pub fn good(repo: &Repository) -> Vec<String> {
    read_lines(repo, "BISECT_GOOD")
}
pub fn add_skip(repo: &Repository, commit: &str) -> Result<()> {
    append_unique(repo, "BISECT_SKIP", commit)
}
pub fn skip(repo: &Repository) -> Vec<String> {
    read_lines(repo, "BISECT_SKIP")
}
pub fn append_log(repo: &Repository, line: &str) -> Result<()> {
    use std::io::Write;
    let mut f = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path(repo, "BISECT_LOG"))?;
    writeln!(f, "{line}")?;
    Ok(())
}
pub fn log_lines(repo: &Repository) -> Vec<String> {
    read_lines(repo, "BISECT_LOG")
}

/// Remove all session state.
pub fn clear(repo: &Repository) -> Result<()> {
    for f in [
        "BISECT_START",
        "BISECT_BAD",
        "BISECT_GOOD",
        "BISECT_SKIP",
        "BISECT_LOG",
    ] {
        let _ = std::fs::remove_file(path(repo, f));
    }
    Ok(())
}

/// Recompute the session state from the marks.
pub fn compute(repo: &Repository) -> Result<State> {
    let Some(bad) = bad(repo) else {
        return Ok(State::NeedMarks);
    };
    let goods = good(repo);
    if goods.is_empty() {
        return Ok(State::NeedMarks);
    }

    let mut suspects = ancestors_incl(repo, &bad)?;
    for g in &goods {
        for a in ancestors_incl(repo, g)? {
            suspects.remove(&a);
        }
    }

    let skipped: HashSet<String> = skip(repo).into_iter().collect();
    let mut candidates: Vec<&String> = suspects
        .iter()
        .filter(|c| **c != bad && !skipped.contains(*c))
        .collect();
    if candidates.is_empty() {
        return Ok(State::Done { first_bad: bad });
    }

    // Pick the candidate whose suspect-ancestor count is closest to half the
    // set (ties broken by hash for determinism).
    candidates.sort();
    let mut best: Option<(&String, usize)> = None;
    for c in candidates.iter() {
        let mut anc = ancestors_incl(repo, c)?;
        anc.retain(|a| suspects.contains(a));
        let n = anc.len();
        let score = n.min(suspects.len() - n);
        if best.map(|(_, s)| score > s).unwrap_or(true) {
            best = Some((c, score));
        }
    }
    Ok(State::Step {
        next: best.expect("non-empty candidates").0.to_string(),
        remaining: candidates.len(),
    })
}

fn ancestors_incl(repo: &Repository, commit: &str) -> Result<HashSet<String>> {
    let mut set = HashSet::new();
    let mut stack = vec![commit.to_string()];
    while let Some(h) = stack.pop() {
        if !set.insert(h.clone()) {
            continue;
        }
        for p in repo.parents_of(&h)? {
            stack.push(p);
        }
    }
    Ok(set)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Manifest;

    /// A linear chain of n commits on main; returns their hashes oldest-first.
    fn chain(repo: &Repository, n: usize) -> Vec<String> {
        let mut out = Vec::new();
        let mut parent: Option<String> = None;
        for i in 0..n {
            // distinct trees so commits are distinct objects
            let mut m = Manifest::default();
            m.blobs.insert(format!("f{i}"), format!("{i:064}"));
            let tree = repo.write_manifest(&m).unwrap();
            let parents: Vec<String> = parent.iter().cloned().collect();
            let c = repo
                .create_commit(&tree, parents, &format!("c{i}"), "me", "t")
                .unwrap();
            repo.write_branch("main", &c).unwrap();
            parent = Some(c.clone());
            out.push(c);
        }
        out
    }

    #[test]
    fn bisect_isolates_first_bad_commit() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let commits = chain(&repo, 8);
        let first_bad = 5; // c5 introduced the regression

        start(&repo, "main").unwrap();
        set_bad(&repo, commits.last().unwrap()).unwrap();
        add_good(&repo, &commits[0]).unwrap();

        // Drive the search like the CLI would: test = "is index >= first_bad".
        let mut steps = 0;
        loop {
            match compute(&repo).unwrap() {
                State::NeedMarks => panic!("marks were set"),
                State::Done { first_bad: fb } => {
                    assert_eq!(fb, commits[first_bad]);
                    break;
                }
                State::Step { next, .. } => {
                    steps += 1;
                    assert!(steps < 20, "bisect did not converge");
                    let idx = commits.iter().position(|c| *c == next).unwrap();
                    if idx >= first_bad {
                        set_bad(&repo, &next).unwrap();
                    } else {
                        add_good(&repo, &next).unwrap();
                    }
                }
            }
        }
        // log2(8) ≈ 3 steps expected, allow a little slack
        assert!(steps <= 4, "took {steps} steps");

        clear(&repo).unwrap();
        assert!(!in_bisect(&repo));
    }

    #[test]
    fn skip_excludes_candidates() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let commits = chain(&repo, 3);

        start(&repo, "main").unwrap();
        set_bad(&repo, &commits[2]).unwrap();
        add_good(&repo, &commits[0]).unwrap();
        // only candidate is c1; skipping it ends the search at the bad mark
        add_skip(&repo, &commits[1]).unwrap();
        match compute(&repo).unwrap() {
            State::Done { first_bad } => assert_eq!(first_bad, commits[2]),
            other => panic!("expected Done, got {other:?}"),
        }
    }
}

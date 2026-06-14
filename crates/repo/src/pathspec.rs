//! Pathspec matching for `add`: which worktree-relative paths a set of
//! user-supplied pathspecs selects. Specs are interpreted relative to the
//! worktree root (mcagit is bare/external — no cwd-relative surprises).
//!
//! A spec matches a path when it is `.`/empty (the whole worktree), an exact
//! path, a directory prefix (recursive), or a `*`/`?` wildcard matched
//! segment-by-segment (a `*` never crosses `/`).

/// True if any spec selects `rel` (a worktree-relative, `/`-separated path).
pub fn matches_any(specs: &[String], rel: &str) -> bool {
    specs.iter().any(|s| matches_one(s, rel))
}

/// True if a single `spec` selects `rel`.
pub fn matches_one(spec: &str, rel: &str) -> bool {
    let spec = spec.trim_end_matches('/');
    if spec.is_empty() || spec == "." {
        return true; // the whole worktree
    }
    if rel == spec {
        return true; // exact file
    }
    // directory prefix (recursive): `playerdata` matches `playerdata/uuid.dat`
    if rel.starts_with(spec) && rel.as_bytes().get(spec.len()) == Some(&b'/') {
        return true;
    }
    glob_match(spec, rel)
}

/// Segment-wise glob: `pat` and `path` must have the same number of `/`
/// segments, and each segment matches with `*` (any run of non-`/`) / `?`
/// (one non-`/` char). So `region/*` matches `region/r.0.0.mca` but not
/// `region/sub/r.0.0.mca`, and `*.dat` matches only top-level `.dat` files.
fn glob_match(pat: &str, path: &str) -> bool {
    let p: Vec<&str> = pat.split('/').collect();
    let x: Vec<&str> = path.split('/').collect();
    if p.len() != x.len() {
        return false;
    }
    p.iter()
        .zip(&x)
        .all(|(ps, xs)| seg_match(ps.as_bytes(), xs.as_bytes()))
}

/// fnmatch one path segment with `*` and `?` (two-pointer, backtracking on `*`).
fn seg_match(pat: &[u8], s: &[u8]) -> bool {
    let (mut pi, mut si) = (0usize, 0usize);
    let (mut star, mut mark) = (None, 0usize);
    while si < s.len() {
        if pi < pat.len() && (pat[pi] == b'?' || pat[pi] == s[si]) {
            pi += 1;
            si += 1;
        } else if pi < pat.len() && pat[pi] == b'*' {
            star = Some(pi);
            mark = si;
            pi += 1;
        } else if let Some(sp) = star {
            pi = sp + 1;
            mark += 1;
            si = mark;
        } else {
            return false;
        }
    }
    while pi < pat.len() && pat[pi] == b'*' {
        pi += 1;
    }
    pi == pat.len()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_dir_and_dot() {
        assert!(matches_one("level.dat", "level.dat"));
        assert!(!matches_one("level.dat", "level.dat_old"));
        assert!(matches_one("playerdata", "playerdata/uuid.dat"));
        assert!(matches_one("playerdata/", "playerdata/uuid.dat"));
        assert!(!matches_one("playerdata", "playerdataX/uuid.dat"));
        assert!(matches_one(".", "anything/at/all.mca"));
        assert!(matches_one("", "anything"));
    }

    #[test]
    fn wildcards_are_segment_scoped() {
        assert!(matches_one("region/*", "region/r.0.0.mca"));
        assert!(!matches_one("region/*", "region/sub/r.0.0.mca"));
        assert!(matches_one("region/r.*.mca", "region/r.-1.2.mca"));
        assert!(matches_one("*.dat", "level.dat"));
        assert!(!matches_one("*.dat", "playerdata/uuid.dat"));
        assert!(matches_one("playerdata/*.dat", "playerdata/uuid.dat"));
        assert!(matches_one("region/r.?.?.mca", "region/r.0.0.mca"));
        assert!(!matches_one("region/r.?.?.mca", "region/r.-1.0.mca"));
    }

    #[test]
    fn matches_any_is_or() {
        let specs = vec!["level.dat".to_string(), "region/*".to_string()];
        assert!(matches_any(&specs, "level.dat"));
        assert!(matches_any(&specs, "region/r.0.0.mca"));
        assert!(!matches_any(&specs, "playerdata/uuid.dat"));
    }
}

//! Plain-text rendering of a [`WorldDiff`].

use crate::world::{ChunkStatus, FileStatus, WorldDiff};

/// Render a world diff as text. Empty diff → `"No differences.\n"`.
pub fn render(d: &WorldDiff) -> String {
    if d.files.is_empty() {
        return "No differences.\n".to_string();
    }
    let mut s = String::new();
    for f in &d.files {
        match f.status {
            FileStatus::Added => s.push_str(&format!("+ {}\n", f.path)),
            FileStatus::Removed => s.push_str(&format!("- {}\n", f.path)),
            FileStatus::Modified => {
                s.push_str(&format!("~ {}\n", f.path));
                for c in &f.changes {
                    s.push_str(&format!("    {} ({:?})\n", c.path, c.kind));
                }
                for ch in &f.chunks {
                    let tag = match ch.status {
                        ChunkStatus::Added => "+",
                        ChunkStatus::Removed => "-",
                        ChunkStatus::Modified => "~",
                    };
                    s.push_str(&format!("    {tag} chunk {},{}", ch.x, ch.z));
                    if !ch.changes.is_empty() {
                        s.push_str(&format!(" ({} changes)", ch.changes.len()));
                    }
                    s.push('\n');
                }
            }
        }
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_diff_renders_no_differences() {
        assert_eq!(render(&WorldDiff::default()), "No differences.\n");
    }

    #[test]
    fn renders_added_file() {
        let d = WorldDiff {
            files: vec![crate::world::FileDiff {
                path: "extra.txt".into(),
                status: FileStatus::Added,
                changes: vec![],
                chunks: vec![],
            }],
        };
        assert_eq!(render(&d), "+ extra.txt\n");
    }
}

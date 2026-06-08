//! Display rendering of the tree walk: flat, path-sorted change rows.

use crate::comparer::{walk, DiffSink};
use mca_nbt::NbtValue;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChangeKind {
    Added,
    Removed,
    Modified,
    TypeChanged,
}

/// A single leaf-level difference, addressed by a dotted/bracketed path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NbtChange {
    pub path: String,
    pub kind: ChangeKind,
    pub old: Option<String>,
    pub new: Option<String>,
}

#[derive(Default)]
pub struct ChangeSink {
    pub changes: Vec<NbtChange>,
}

impl DiffSink for ChangeSink {
    fn added(&mut self, path: &str, value: &NbtValue) {
        self.changes.push(NbtChange {
            path: path.to_string(),
            kind: ChangeKind::Added,
            old: None,
            new: Some(repr(value)),
        });
    }
    fn removed(&mut self, path: &str, value: &NbtValue) {
        self.changes.push(NbtChange {
            path: path.to_string(),
            kind: ChangeKind::Removed,
            old: Some(repr(value)),
            new: None,
        });
    }
    fn modified(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.changes.push(NbtChange {
            path: path.to_string(),
            kind: ChangeKind::Modified,
            old: Some(repr(a)),
            new: Some(repr(b)),
        });
    }
    fn type_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.changes.push(NbtChange {
            path: path.to_string(),
            kind: ChangeKind::TypeChanged,
            old: Some(repr(a)),
            new: Some(repr(b)),
        });
    }
    fn array_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue) {
        self.changes.push(NbtChange {
            path: path.to_string(),
            kind: ChangeKind::Modified,
            old: Some(repr(a)),
            new: Some(repr(b)),
        });
    }
}

/// Compare two NBT trees into a flat, path-sorted change list.
pub fn compare(a: &NbtValue, b: &NbtValue) -> Vec<NbtChange> {
    let mut sink = ChangeSink::default();
    walk(a, b, &mut sink);
    sink.changes.sort_by(|x, y| x.path.cmp(&y.path));
    sink.changes
}

/// A short human-readable rendering of a value for a diff row.
fn repr(v: &NbtValue) -> String {
    match v {
        NbtValue::Byte(x) => x.to_string(),
        NbtValue::Short(x) => x.to_string(),
        NbtValue::Int(x) => x.to_string(),
        NbtValue::Long(x) => x.to_string(),
        NbtValue::Float(x) => x.to_string(),
        NbtValue::Double(x) => x.to_string(),
        NbtValue::String(s) => format!("{s:?}"),
        NbtValue::ByteArray(b) => format!("[{} bytes]", b.len()),
        NbtValue::IntArray(a) => format!("[{} ints]", a.len()),
        NbtValue::LongArray(a) => format!("[{} longs]", a.len()),
        NbtValue::List(l) => format!("[{} items]", l.len()),
        NbtValue::Compound(m) => format!("{{{} keys}}", m.len()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_nbt::Compound;

    fn comp(pairs: &[(&str, NbtValue)]) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert((*k).into(), v.clone());
        }
        NbtValue::Compound(m)
    }

    #[test]
    fn renders_change_rows() {
        let a = comp(&[
            ("hp", NbtValue::Int(20)),
            ("name", NbtValue::String("old".into())),
            ("gone", NbtValue::Byte(1)),
        ]);
        let b = comp(&[
            ("hp", NbtValue::Int(18)),
            ("name", NbtValue::String("old".into())),
            ("added", NbtValue::Int(7)),
        ]);
        let ch = compare(&a, &b);
        assert_eq!(ch.len(), 3);
        // sorted by path: added, gone, hp
        assert_eq!(ch[0].path, "added");
        assert_eq!(ch[0].kind, ChangeKind::Added);
        assert_eq!(ch[0].new.as_deref(), Some("7"));
        assert_eq!(ch[1].path, "gone");
        assert_eq!(ch[1].kind, ChangeKind::Removed);
        assert_eq!(ch[2].path, "hp");
        assert_eq!(ch[2].kind, ChangeKind::Modified);
        assert_eq!(ch[2].old.as_deref(), Some("20"));
        assert_eq!(ch[2].new.as_deref(), Some("18"));
    }

    #[test]
    fn identical_is_empty() {
        let a = comp(&[("x", NbtValue::Int(1))]);
        assert!(compare(&a, &a).is_empty());
    }
}

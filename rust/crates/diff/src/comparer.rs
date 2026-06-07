//! The one NBT tree-walk. Compounds match by key; lists match by identity when
//! every element has a unique identity key, else by index. Leaf decisions are
//! reported to a [`DiffSink`].

use mca_nbt::{identity_key, tag_id, NbtValue};
use std::collections::{HashMap, HashSet};

/// Receives the leaf-level decisions of the tree walk.
pub trait DiffSink {
    fn added(&mut self, path: &str, value: &NbtValue);
    fn removed(&mut self, path: &str, value: &NbtValue);
    fn modified(&mut self, path: &str, a: &NbtValue, b: &NbtValue);
    fn type_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue);
    fn array_changed(&mut self, path: &str, a: &NbtValue, b: &NbtValue);
}

/// Walk two NBT trees from the root path "".
pub fn walk(a: &NbtValue, b: &NbtValue, sink: &mut dyn DiffSink) {
    compare("", a, b, sink); // root path is ""
}

fn compare(path: &str, a: &NbtValue, b: &NbtValue, sink: &mut dyn DiffSink) {
    if tag_id(a) != tag_id(b) {
        sink.type_changed(path, a, b);
        return;
    }
    match (a, b) {
        (NbtValue::Compound(ma), NbtValue::Compound(mb)) => compare_compound(path, ma, mb, sink),
        (NbtValue::List(la), NbtValue::List(lb)) => compare_list(path, la, lb, sink),
        (NbtValue::ByteArray(_), _)
        | (NbtValue::IntArray(_), _)
        | (NbtValue::LongArray(_), _) => {
            if a != b {
                sink.array_changed(path, a, b);
            }
        }
        _ => {
            if a != b {
                sink.modified(path, a, b);
            }
        }
    }
}

fn compare_compound(
    path: &str,
    ma: &mca_nbt::Compound,
    mb: &mca_nbt::Compound,
    sink: &mut dyn DiffSink,
) {
    for (k, va) in ma {
        let child = child_path(path, k);
        match mb.get(k) {
            Some(vb) => compare(&child, va, vb, sink),
            None => sink.removed(&child, va),
        }
    }
    // Added keys in sorted order so an extracted patch is reproducible.
    let mut added: Vec<&String> = mb.keys().filter(|k| !ma.contains_key(*k)).collect();
    added.sort();
    for k in added {
        sink.added(&child_path(path, k), &mb[k]);
    }
}

fn compare_list(path: &str, la: &[NbtValue], lb: &[NbtValue], sink: &mut dyn DiffSink) {
    let keys_a = list_keys(la);
    let keys_b = if keys_a.is_some() { list_keys(lb) } else { None };
    if let (Some(ka), Some(kb)) = (keys_a, keys_b) {
        compare_keyed(path, la, &ka, lb, &kb, sink);
        return;
    }
    let common = la.len().min(lb.len());
    for i in 0..common {
        compare(&format!("{path}[{i}]"), &la[i], &lb[i], sink);
    }
    for (i, v) in la.iter().enumerate().skip(common) {
        sink.removed(&format!("{path}[{i}]"), v);
    }
    for (i, v) in lb.iter().enumerate().skip(common) {
        sink.added(&format!("{path}[{i}]"), v);
    }
}

fn compare_keyed(
    path: &str,
    la: &[NbtValue],
    ka: &[String],
    lb: &[NbtValue],
    kb: &[String],
    sink: &mut dyn DiffSink,
) {
    let mut b_index: HashMap<&str, usize> =
        kb.iter().enumerate().map(|(i, k)| (k.as_str(), i)).collect();
    for (i, k) in ka.iter().enumerate() {
        let label = format!("{path}[{k}]");
        match b_index.remove(k.as_str()) {
            Some(j) => compare(&label, &la[i], &lb[j], sink),
            None => sink.removed(&label, &la[i]),
        }
    }
    // Remaining b-only keys = added; sorted for reproducibility.
    let mut added: Vec<(&str, usize)> = b_index.into_iter().collect();
    added.sort_by(|x, y| x.0.cmp(y.0));
    for (k, j) in added {
        sink.added(&format!("{path}[{k}]"), &lb[j]);
    }
}

/// Identity keys for every element, or `None` if any element lacks an identity
/// or two collide (→ caller falls back to index alignment).
fn list_keys(list: &[NbtValue]) -> Option<Vec<String>> {
    if list.is_empty() {
        return None;
    }
    let mut keys = Vec::with_capacity(list.len());
    let mut seen = HashSet::with_capacity(list.len());
    for el in list {
        let k = identity_key(el)?;
        if !seen.insert(k.clone()) {
            return None;
        }
        keys.push(k);
    }
    Some(keys)
}

fn child_path(path: &str, name: &str) -> String {
    if path.is_empty() {
        name.to_string()
    } else {
        format!("{path}.{name}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_nbt::Compound;

    #[derive(Default)]
    struct Rec {
        ev: Vec<String>,
    }
    impl DiffSink for Rec {
        fn added(&mut self, p: &str, _v: &NbtValue) {
            self.ev.push(format!("+{p}"));
        }
        fn removed(&mut self, p: &str, _v: &NbtValue) {
            self.ev.push(format!("-{p}"));
        }
        fn modified(&mut self, p: &str, _a: &NbtValue, _b: &NbtValue) {
            self.ev.push(format!("~{p}"));
        }
        fn type_changed(&mut self, p: &str, _a: &NbtValue, _b: &NbtValue) {
            self.ev.push(format!("T{p}"));
        }
        fn array_changed(&mut self, p: &str, _a: &NbtValue, _b: &NbtValue) {
            self.ev.push(format!("A{p}"));
        }
    }

    fn comp(pairs: &[(&str, NbtValue)]) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert((*k).into(), v.clone());
        }
        NbtValue::Compound(m)
    }

    fn run(a: &NbtValue, b: &NbtValue) -> Vec<String> {
        let mut r = Rec::default();
        walk(a, b, &mut r);
        r.ev.sort();
        r.ev
    }

    #[test]
    fn compound_add_modify_remove() {
        let a = comp(&[("x", NbtValue::Int(1)), ("y", NbtValue::Int(2)), ("gone", NbtValue::Byte(1))]);
        let b = comp(&[("x", NbtValue::Int(1)), ("y", NbtValue::Int(3)), ("z", NbtValue::Int(4))]);
        assert_eq!(run(&a, &b), vec!["+z", "-gone", "~y"]);
    }

    #[test]
    fn nested_recurse() {
        let a = comp(&[("p", comp(&[("q", NbtValue::Int(1))]))]);
        let b = comp(&[("p", comp(&[("q", NbtValue::Int(2))]))]);
        assert_eq!(run(&a, &b), vec!["~p.q"]);
    }

    #[test]
    fn list_by_identity_ignores_reorder() {
        let a = NbtValue::List(vec![
            comp(&[("id", NbtValue::String("a".into()))]),
            comp(&[("id", NbtValue::String("b".into()))]),
        ]);
        let b = NbtValue::List(vec![
            comp(&[("id", NbtValue::String("b".into()))]),
            comp(&[("id", NbtValue::String("a".into()))]),
        ]);
        assert!(run(&a, &b).is_empty());
    }

    #[test]
    fn list_by_identity_reports_field_change() {
        let a = comp(&[("e", NbtValue::List(vec![comp(&[("id", NbtValue::String("a".into()))])]))]);
        let b = comp(&[(
            "e",
            NbtValue::List(vec![comp(&[
                ("id", NbtValue::String("a".into())),
                ("hp", NbtValue::Int(5)),
            ])]),
        )]);
        assert_eq!(run(&a, &b), vec!["+e[id:a].hp"]);
    }

    #[test]
    fn list_by_index_when_no_identity() {
        let a = NbtValue::List(vec![NbtValue::Int(1), NbtValue::Int(2)]);
        let b = NbtValue::List(vec![NbtValue::Int(1), NbtValue::Int(3), NbtValue::Int(5)]);
        assert_eq!(run(&a, &b), vec!["+[2]", "~[1]"]);
    }

    #[test]
    fn type_change_and_array_change() {
        let a = comp(&[("v", NbtValue::Int(1)), ("arr", NbtValue::IntArray(vec![1, 2]))]);
        let b = comp(&[("v", NbtValue::String("1".into())), ("arr", NbtValue::IntArray(vec![1, 3]))]);
        assert_eq!(run(&a, &b), vec!["Aarr", "Tv"]);
    }
}

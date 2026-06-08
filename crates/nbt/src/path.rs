//! The NBT path language: addressing nodes by key, list index, or identity.

use crate::identity::identity_key;
use crate::value::NbtValue;
use crate::{NbtError, Result};

/// One step in a path.
#[derive(Debug, Clone, PartialEq)]
enum Seg {
    Key(String),
    Index(usize),
    Ident(String),
}

/// A parsed path. Build with [`NbtPath::parse`].
#[derive(Debug, Clone, PartialEq)]
pub struct NbtPath {
    segs: Vec<Seg>,
}

impl NbtPath {
    /// Parse a path string like `Data.Player.Pos[0]` or `E[uuid:1,2,3,4].id`.
    pub fn parse(s: &str) -> Result<Self> {
        let mut segs = Vec::new();
        let bytes = s.as_bytes();
        let mut i = 0usize;

        while i < bytes.len() {
            match bytes[i] as char {
                '.' => {
                    i += 1; // skip separator
                }
                '[' => {
                    let start = i + 1;
                    let end = s[start..]
                        .find(']')
                        .map(|off| start + off)
                        .ok_or_else(|| NbtError::InvalidPath(format!("unclosed '[' in {s:?}")))?;
                    let inner = &s[start..end];
                    if inner.is_empty() {
                        return Err(NbtError::InvalidPath(format!("empty [] in {s:?}")));
                    }
                    if let Ok(n) = inner.parse::<usize>() {
                        segs.push(Seg::Index(n));
                    } else {
                        segs.push(Seg::Ident(inner.to_string()));
                    }
                    i = end + 1;
                }
                _ => {
                    // a key: read until the next '.' or '['
                    let rest = &s[i..];
                    let stop = rest.find(['.', '[']).unwrap_or(rest.len());
                    segs.push(Seg::Key(rest[..stop].to_string()));
                    i += stop;
                }
            }
        }
        if segs.is_empty() {
            return Err(NbtError::InvalidPath(format!("empty path {s:?}")));
        }
        Ok(NbtPath { segs })
    }

    /// Borrow the node at this path, if present.
    pub fn get<'v>(&self, root: &'v NbtValue) -> Option<&'v NbtValue> {
        let mut cur = root;
        for seg in &self.segs {
            cur = step(cur, seg)?;
        }
        Some(cur)
    }

    /// Mutably borrow the node at this path, if present.
    pub fn get_mut<'v>(&self, root: &'v mut NbtValue) -> Option<&'v mut NbtValue> {
        let mut cur = root;
        for seg in &self.segs {
            cur = step_mut(cur, seg)?;
        }
        Some(cur)
    }

    /// Set the node at this path to `value`. The parent must exist; if the final
    /// step is a `Key` on a compound, it is inserted or replaced. Returns
    /// `false` if the parent path does not resolve or the final step is not
    /// applicable.
    pub fn set(&self, root: &mut NbtValue, value: NbtValue) -> bool {
        let (last, parents) = match self.segs.split_last() {
            Some(x) => x,
            None => return false,
        };
        let mut cur = root;
        for seg in parents {
            match step_mut(cur, seg) {
                Some(next) => cur = next,
                None => return false,
            }
        }
        match (last, cur) {
            (Seg::Key(k), NbtValue::Compound(m)) => {
                m.insert(k.clone(), value);
                true
            }
            // `i < len` replaces; `i == len` appends (the comparer expresses an
            // added list element as an op at the new trailing index).
            (Seg::Index(i), NbtValue::List(items)) if *i <= items.len() => {
                if *i == items.len() {
                    items.push(value);
                } else {
                    items[*i] = value;
                }
                true
            }
            (Seg::Ident(id), NbtValue::List(items)) => {
                if let Some(slot) = items
                    .iter_mut()
                    .find(|e| identity_key(e).as_deref() == Some(id.as_str()))
                {
                    *slot = value;
                    true
                } else {
                    false
                }
            }
            _ => false,
        }
    }

    /// Remove the node at this path. Returns the removed value, or `None`.
    pub fn remove(&self, root: &mut NbtValue) -> Option<NbtValue> {
        let (last, parents) = self.segs.split_last()?;
        let mut cur = root;
        for seg in parents {
            cur = step_mut(cur, seg)?;
        }
        match (last, cur) {
            (Seg::Key(k), NbtValue::Compound(m)) => m.shift_remove(k),
            (Seg::Index(i), NbtValue::List(items)) if *i < items.len() => Some(items.remove(*i)),
            (Seg::Ident(id), NbtValue::List(items)) => {
                let pos = items
                    .iter()
                    .position(|e| identity_key(e).as_deref() == Some(id.as_str()))?;
                Some(items.remove(pos))
            }
            _ => None,
        }
    }
}

fn step<'v>(cur: &'v NbtValue, seg: &Seg) -> Option<&'v NbtValue> {
    match (seg, cur) {
        (Seg::Key(k), NbtValue::Compound(m)) => m.get(k),
        (Seg::Index(i), NbtValue::List(items)) => items.get(*i),
        (Seg::Ident(id), NbtValue::List(items)) => items
            .iter()
            .find(|e| identity_key(e).as_deref() == Some(id.as_str())),
        _ => None,
    }
}

fn step_mut<'v>(cur: &'v mut NbtValue, seg: &Seg) -> Option<&'v mut NbtValue> {
    match (seg, cur) {
        (Seg::Key(k), NbtValue::Compound(m)) => m.get_mut(k),
        (Seg::Index(i), NbtValue::List(items)) => items.get_mut(*i),
        (Seg::Ident(id), NbtValue::List(items)) => items
            .iter_mut()
            .find(|e| identity_key(e).as_deref() == Some(id.as_str())),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    fn world() -> NbtValue {
        // { Data: { Player: { Pos: [1.0, 2.0, 3.0] } },
        //   Entities: [ {UUID:[1,2,3,4], id:"zombie"} ] }
        let pos = vec![
            NbtValue::Double(1.0),
            NbtValue::Double(2.0),
            NbtValue::Double(3.0),
        ];
        let mut player = Compound::new();
        player.insert("Pos".into(), NbtValue::List(pos));
        let mut data = Compound::new();
        data.insert("Player".into(), NbtValue::Compound(player));

        let mut ent = Compound::new();
        ent.insert("UUID".into(), NbtValue::IntArray(vec![1, 2, 3, 4]));
        ent.insert("id".into(), NbtValue::String("zombie".into()));

        let mut root = Compound::new();
        root.insert("Data".into(), NbtValue::Compound(data));
        root.insert(
            "Entities".into(),
            NbtValue::List(vec![NbtValue::Compound(ent)]),
        );
        NbtValue::Compound(root)
    }

    #[test]
    fn get_by_key_and_index() {
        let w = world();
        let p = NbtPath::parse("Data.Player.Pos[1]").unwrap();
        assert_eq!(p.get(&w), Some(&NbtValue::Double(2.0)));
    }

    #[test]
    fn get_by_identity() {
        let w = world();
        let p = NbtPath::parse("Entities[uuid:1,2,3,4].id").unwrap();
        assert_eq!(p.get(&w), Some(&NbtValue::String("zombie".into())));
    }

    #[test]
    fn set_replaces_value() {
        let mut w = world();
        let p = NbtPath::parse("Data.Player.Pos[1]").unwrap();
        assert!(p.set(&mut w, NbtValue::Double(99.0)));
        assert_eq!(p.get(&w), Some(&NbtValue::Double(99.0)));
    }

    #[test]
    fn set_appends_at_end_index() {
        let mut w = world();
        // Pos has 3 elements [0..2]; index 3 == len should append, not no-op.
        let p = NbtPath::parse("Data.Player.Pos[3]").unwrap();
        assert!(p.set(&mut w, NbtValue::Double(4.0)));
        assert_eq!(
            NbtPath::parse("Data.Player.Pos[3]").unwrap().get(&w),
            Some(&NbtValue::Double(4.0))
        );
        // but a gap (index 5 when len is now 4) still fails.
        assert!(!NbtPath::parse("Data.Player.Pos[5]")
            .unwrap()
            .set(&mut w, NbtValue::Double(9.0)));
    }

    #[test]
    fn set_inserts_new_key() {
        let mut w = world();
        let p = NbtPath::parse("Data.Player.Health").unwrap();
        assert!(p.set(&mut w, NbtValue::Float(20.0)));
        assert_eq!(p.get(&w), Some(&NbtValue::Float(20.0)));
    }

    #[test]
    fn remove_by_identity() {
        let mut w = world();
        let p = NbtPath::parse("Entities[uuid:1,2,3,4]").unwrap();
        assert!(p.remove(&mut w).is_some());
        let count = NbtPath::parse("Entities").unwrap().get(&w);
        let NbtValue::List(items) = count.unwrap() else {
            panic!()
        };
        assert!(items.is_empty());
    }

    #[test]
    fn missing_path_returns_none() {
        let w = world();
        assert_eq!(NbtPath::parse("Data.Nope").unwrap().get(&w), None);
    }

    #[test]
    fn empty_path_errors() {
        assert!(NbtPath::parse("").is_err());
    }
}

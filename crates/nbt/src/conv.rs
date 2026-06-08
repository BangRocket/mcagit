//! Bridge between valence_nbt's typed model (used for battle-tested binary
//! read/write) and our uniform [`NbtValue`] tree (used by the comparer, path,
//! identity, canonical and JSON layers). Conversions are total and lossless for
//! Java NBT; the only representational note is that `ByteArray` is `u8` here and
//! `i8` in valence (a reinterpret cast, not a value change).

use crate::value::{Compound, NbtValue};
use valence_nbt::{Compound as VCompound, List as VList, Value as VValue};

/// valence value → our `NbtValue`.
pub fn from_value(v: VValue) -> NbtValue {
    match v {
        VValue::Byte(x) => NbtValue::Byte(x),
        VValue::Short(x) => NbtValue::Short(x),
        VValue::Int(x) => NbtValue::Int(x),
        VValue::Long(x) => NbtValue::Long(x),
        VValue::Float(x) => NbtValue::Float(x),
        VValue::Double(x) => NbtValue::Double(x),
        VValue::ByteArray(b) => NbtValue::ByteArray(b.into_iter().map(|x| x as u8).collect()),
        VValue::String(s) => NbtValue::String(s),
        VValue::IntArray(a) => NbtValue::IntArray(a),
        VValue::LongArray(a) => NbtValue::LongArray(a),
        VValue::List(l) => NbtValue::List(from_list(l)),
        VValue::Compound(c) => NbtValue::Compound(from_compound(c)),
    }
}

/// valence compound → our ordered `Compound`.
pub fn from_compound(c: VCompound) -> Compound {
    let mut m = Compound::with_capacity(c.len());
    for (k, v) in c {
        m.insert(k, from_value(v));
    }
    m
}

fn from_list(l: VList) -> Vec<NbtValue> {
    match l {
        VList::End => Vec::new(),
        VList::Byte(v) => v.into_iter().map(NbtValue::Byte).collect(),
        VList::Short(v) => v.into_iter().map(NbtValue::Short).collect(),
        VList::Int(v) => v.into_iter().map(NbtValue::Int).collect(),
        VList::Long(v) => v.into_iter().map(NbtValue::Long).collect(),
        VList::Float(v) => v.into_iter().map(NbtValue::Float).collect(),
        VList::Double(v) => v.into_iter().map(NbtValue::Double).collect(),
        VList::ByteArray(v) => v
            .into_iter()
            .map(|b| NbtValue::ByteArray(b.into_iter().map(|x| x as u8).collect()))
            .collect(),
        VList::String(v) => v.into_iter().map(NbtValue::String).collect(),
        VList::List(v) => v
            .into_iter()
            .map(|i| NbtValue::List(from_list(i)))
            .collect(),
        VList::Compound(v) => v
            .into_iter()
            .map(|c| NbtValue::Compound(from_compound(c)))
            .collect(),
        VList::IntArray(v) => v.into_iter().map(NbtValue::IntArray).collect(),
        VList::LongArray(v) => v.into_iter().map(NbtValue::LongArray).collect(),
    }
}

/// our `NbtValue` → valence value. When `sort`, compound keys are emitted in
/// sorted order at every level (canonical form).
pub fn to_value(v: &NbtValue, sort: bool) -> VValue {
    match v {
        NbtValue::Byte(x) => VValue::Byte(*x),
        NbtValue::Short(x) => VValue::Short(*x),
        NbtValue::Int(x) => VValue::Int(*x),
        NbtValue::Long(x) => VValue::Long(*x),
        NbtValue::Float(x) => VValue::Float(*x),
        NbtValue::Double(x) => VValue::Double(*x),
        NbtValue::ByteArray(b) => VValue::ByteArray(b.iter().map(|&x| x as i8).collect()),
        NbtValue::String(s) => VValue::String(s.clone()),
        NbtValue::IntArray(a) => VValue::IntArray(a.clone()),
        NbtValue::LongArray(a) => VValue::LongArray(a.clone()),
        NbtValue::List(items) => VValue::List(to_list(items, sort)),
        NbtValue::Compound(m) => VValue::Compound(to_compound(m, sort)),
    }
}

/// our `Compound` → valence compound (sorted keys when `sort`).
pub fn to_compound(m: &Compound, sort: bool) -> VCompound {
    let mut c = VCompound::new();
    if sort {
        let mut keys: Vec<&String> = m.keys().collect();
        keys.sort();
        for k in keys {
            c.insert(k.clone(), to_value(&m[k], sort));
        }
    } else {
        for (k, v) in m {
            c.insert(k.clone(), to_value(v, sort));
        }
    }
    c
}

/// our homogeneous `Vec<NbtValue>` → valence's typed `List`. The element variant
/// is derived from the first element (empty → `List::End`, matching the writer's
/// historical behavior of an End element type for empty lists).
fn to_list(items: &[NbtValue], sort: bool) -> VList {
    macro_rules! collect_as {
        ($variant:ident, $pat:pat => $bind:expr) => {
            VList::$variant(
                items
                    .iter()
                    .map(|it| match it {
                        $pat => $bind,
                        _ => Default::default(),
                    })
                    .collect(),
            )
        };
    }
    match items.first() {
        None => VList::End,
        Some(NbtValue::Byte(_)) => collect_as!(Byte, NbtValue::Byte(x) => *x),
        Some(NbtValue::Short(_)) => collect_as!(Short, NbtValue::Short(x) => *x),
        Some(NbtValue::Int(_)) => collect_as!(Int, NbtValue::Int(x) => *x),
        Some(NbtValue::Long(_)) => collect_as!(Long, NbtValue::Long(x) => *x),
        Some(NbtValue::Float(_)) => collect_as!(Float, NbtValue::Float(x) => *x),
        Some(NbtValue::Double(_)) => collect_as!(Double, NbtValue::Double(x) => *x),
        Some(NbtValue::ByteArray(_)) => VList::ByteArray(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::ByteArray(b) => b.iter().map(|&x| x as i8).collect(),
                    _ => Vec::new(),
                })
                .collect(),
        ),
        Some(NbtValue::String(_)) => VList::String(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::String(s) => s.clone(),
                    _ => String::new(),
                })
                .collect(),
        ),
        Some(NbtValue::IntArray(_)) => VList::IntArray(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::IntArray(a) => a.clone(),
                    _ => Vec::new(),
                })
                .collect(),
        ),
        Some(NbtValue::LongArray(_)) => VList::LongArray(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::LongArray(a) => a.clone(),
                    _ => Vec::new(),
                })
                .collect(),
        ),
        Some(NbtValue::List(_)) => VList::List(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::List(l) => to_list(l, sort),
                    _ => VList::End,
                })
                .collect(),
        ),
        Some(NbtValue::Compound(_)) => VList::Compound(
            items
                .iter()
                .map(|it| match it {
                    NbtValue::Compound(m) => to_compound(m, sort),
                    _ => VCompound::new(),
                })
                .collect(),
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn conv_roundtrips_every_type() {
        let mut m = Compound::new();
        m.insert("k".into(), NbtValue::Int(7));
        let cases = vec![
            NbtValue::Byte(-1),
            NbtValue::Short(-32768),
            NbtValue::Int(i32::MIN),
            NbtValue::Long(i64::MIN),
            NbtValue::Float(0.1 + 0.2),
            NbtValue::Double(-1.8179372026061453),
            NbtValue::ByteArray(vec![0, 127, 128, 255]), // high bytes exercise u8<->i8
            NbtValue::String("modified\u{0}utf8".into()),
            NbtValue::IntArray(vec![1, -2, i32::MAX]),
            NbtValue::LongArray(vec![1, -2, 1 << 60]),
            NbtValue::List(vec![]), // empty -> List::End
            NbtValue::List(vec![NbtValue::Byte(1), NbtValue::Byte(2)]),
            NbtValue::List(vec![NbtValue::Compound(m.clone())]),
            NbtValue::Compound(m),
        ];
        for v in cases {
            assert_eq!(from_value(to_value(&v, false)), v, "roundtrip {v:?}");
        }
    }
}

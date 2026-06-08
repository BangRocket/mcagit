//! NBT binary writer — converts our [`NbtValue`] into valence_nbt's model and
//! serializes via its Java-edition encoder. When `sort` is true, compound keys
//! are emitted in sorted order at every level (canonical form).

use crate::conv::to_value;
use crate::value::NbtValue;
use valence_nbt::{Compound as VCompound, Value as VValue};

/// Write a complete NBT document with the given root `name`. The NBT root is a
/// compound; a non-compound root (not produced by any real world) is written as
/// an empty document.
pub fn write_named(name: &str, v: &NbtValue, sort: bool) -> Vec<u8> {
    let compound = match to_value(v, sort) {
        VValue::Compound(c) => c,
        _ => VCompound::new(),
    };
    let mut out = Vec::new();
    valence_nbt::to_binary(&compound, &mut out, name).expect("writing NBT to a Vec cannot fail");
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::read::read;
    use crate::value::Compound;

    fn sample() -> NbtValue {
        let mut inner = Compound::new();
        inner.insert(
            "Pos".into(),
            NbtValue::List(vec![NbtValue::Double(1.0), NbtValue::Double(2.0)]),
        );
        inner.insert("Health".into(), NbtValue::Float(20.0));
        inner.insert("Name".into(), NbtValue::String("Steve".into()));
        inner.insert("Inv".into(), NbtValue::ByteArray(vec![1, 2, 3]));
        inner.insert("Cells".into(), NbtValue::LongArray(vec![1, -2, 3]));
        inner.insert("Empty".into(), NbtValue::List(vec![]));
        NbtValue::Compound(inner)
    }

    #[test]
    fn write_then_read_roundtrips() {
        let v = sample();
        let bytes = write_named("root", &v, false);
        let (name, back) = read(&bytes).unwrap();
        assert_eq!(name, "root");
        assert_eq!(back, v);
    }

    #[test]
    fn empty_list_roundtrips_as_empty() {
        let mut c = Compound::new();
        c.insert("e".into(), NbtValue::List(vec![]));
        let v = NbtValue::Compound(c);
        let (_n, back) = read(&write_named("", &v, false)).unwrap();
        let NbtValue::Compound(m) = back else {
            panic!("compound")
        };
        assert_eq!(m.get("e"), Some(&NbtValue::List(vec![])));
    }
}

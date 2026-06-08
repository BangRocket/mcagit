//! Deterministic canonical byte form: compound keys are emitted sorted, with a
//! fixed empty root name, so equal content hashes identically regardless of
//! original key order or on-disk compression.

use crate::value::NbtValue;
use crate::write::write_named;

/// Canonical bytes for a value (sorted compounds, empty root name).
pub fn canonical_bytes(v: &NbtValue) -> Vec<u8> {
    write_named("", v, true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    #[test]
    fn key_order_does_not_affect_canonical_bytes() {
        let mut a = Compound::new();
        a.insert("b".into(), NbtValue::Int(2));
        a.insert("a".into(), NbtValue::Int(1));

        let mut b = Compound::new();
        b.insert("a".into(), NbtValue::Int(1));
        b.insert("b".into(), NbtValue::Int(2));

        assert_eq!(
            canonical_bytes(&NbtValue::Compound(a)),
            canonical_bytes(&NbtValue::Compound(b))
        );
    }

    #[test]
    fn nested_compounds_are_sorted_too() {
        let mut inner1 = Compound::new();
        inner1.insert("z".into(), NbtValue::Byte(1));
        inner1.insert("y".into(), NbtValue::Byte(2));
        let mut outer1 = Compound::new();
        outer1.insert("c".into(), NbtValue::Compound(inner1));

        let mut inner2 = Compound::new();
        inner2.insert("y".into(), NbtValue::Byte(2));
        inner2.insert("z".into(), NbtValue::Byte(1));
        let mut outer2 = Compound::new();
        outer2.insert("c".into(), NbtValue::Compound(inner2));

        assert_eq!(
            canonical_bytes(&NbtValue::Compound(outer1)),
            canonical_bytes(&NbtValue::Compound(outer2))
        );
    }
}

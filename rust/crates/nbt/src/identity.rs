//! Stable identity key for matching list elements across versions.

use crate::value::NbtValue;

/// Derive a stable identity key for a list element, or `None` to fall back to
/// positional index matching.
pub fn identity_key(v: &NbtValue) -> Option<String> {
    let NbtValue::Compound(m) = v else {
        return None;
    };

    // Modern entity UUID: IntArray of length 4 under "UUID".
    if let Some(NbtValue::IntArray(a)) = m.get("UUID") {
        if a.len() == 4 {
            return Some(format!("uuid:{},{},{},{}", a[0], a[1], a[2], a[3]));
        }
    }
    // Legacy entity UUID: paired longs.
    if let (Some(NbtValue::Long(hi)), Some(NbtValue::Long(lo))) =
        (m.get("UUIDMost"), m.get("UUIDLeast"))
    {
        return Some(format!("uuid:{}:{}", hi, lo));
    }
    // Block entity coords.
    if let (Some(NbtValue::Int(x)), Some(NbtValue::Int(y)), Some(NbtValue::Int(z))) =
        (m.get("x"), m.get("y"), m.get("z"))
    {
        return Some(format!("block:{},{},{}", x, y, z));
    }
    // Inventory slot.
    if let Some(NbtValue::Byte(s)) = m.get("Slot") {
        return Some(format!("slot:{}", s));
    }
    // Generic string id.
    if let Some(NbtValue::String(id)) = m.get("id") {
        return Some(format!("id:{}", id));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    fn compound(pairs: &[(&str, NbtValue)]) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert((*k).into(), v.clone());
        }
        NbtValue::Compound(m)
    }

    #[test]
    fn modern_uuid_wins() {
        let v = compound(&[
            ("UUID", NbtValue::IntArray(vec![1, 2, 3, 4])),
            ("id", NbtValue::String("zombie".into())),
        ]);
        assert_eq!(identity_key(&v).as_deref(), Some("uuid:1,2,3,4"));
    }

    #[test]
    fn block_coords() {
        let v = compound(&[
            ("x", NbtValue::Int(10)),
            ("y", NbtValue::Int(64)),
            ("z", NbtValue::Int(-3)),
        ]);
        assert_eq!(identity_key(&v).as_deref(), Some("block:10,64,-3"));
    }

    #[test]
    fn slot_then_id() {
        assert_eq!(
            identity_key(&compound(&[("Slot", NbtValue::Byte(2))])).as_deref(),
            Some("slot:2")
        );
        assert_eq!(
            identity_key(&compound(&[(
                "id",
                NbtValue::String("minecraft:stone".into())
            )]))
            .as_deref(),
            Some("id:minecraft:stone")
        );
    }

    #[test]
    fn no_identity_returns_none() {
        assert_eq!(identity_key(&compound(&[("foo", NbtValue::Int(1))])), None);
        assert_eq!(identity_key(&NbtValue::Int(5)), None);
    }
}

//! The NBT data model.

use indexmap::IndexMap;

/// A compound's key→value map, preserving insertion order on read.
pub type Compound = IndexMap<String, NbtValue>;

/// An NBT tag value. `ByteArray` holds raw bytes; `List` is homogeneous by
/// construction (the writer derives the element type from the first element).
#[derive(Debug, Clone, PartialEq)]
pub enum NbtValue {
    Byte(i8),
    Short(i16),
    Int(i32),
    Long(i64),
    Float(f32),
    Double(f64),
    ByteArray(Vec<u8>),
    String(String),
    List(Vec<NbtValue>),
    Compound(Compound),
    IntArray(Vec<i32>),
    LongArray(Vec<i64>),
}

/// The NBT tag-type byte for a value (TAG_End = 0 is never a value).
pub fn tag_id(v: &NbtValue) -> u8 {
    match v {
        NbtValue::Byte(_) => 1,
        NbtValue::Short(_) => 2,
        NbtValue::Int(_) => 3,
        NbtValue::Long(_) => 4,
        NbtValue::Float(_) => 5,
        NbtValue::Double(_) => 6,
        NbtValue::ByteArray(_) => 7,
        NbtValue::String(_) => 8,
        NbtValue::List(_) => 9,
        NbtValue::Compound(_) => 10,
        NbtValue::IntArray(_) => 11,
        NbtValue::LongArray(_) => 12,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tag_ids_match_spec() {
        assert_eq!(tag_id(&NbtValue::Byte(0)), 1);
        assert_eq!(tag_id(&NbtValue::Compound(Compound::new())), 10);
        assert_eq!(tag_id(&NbtValue::LongArray(vec![])), 12);
    }
}

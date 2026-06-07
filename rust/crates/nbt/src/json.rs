//! Lossless, type-tagged JSON encoding of NBT (longs as strings).

use crate::value::{Compound, NbtValue};
use crate::{NbtError, Result};
use serde_json::{Map, Value as J};

/// Encode an NBT value to its type-tagged JSON form.
pub fn to_json(v: &NbtValue) -> J {
    fn obj(tag: &str, val: J) -> J {
        let mut m = Map::new();
        m.insert(tag.to_string(), val);
        J::Object(m)
    }
    match v {
        NbtValue::Byte(x) => obj("byte", J::from(*x)),
        NbtValue::Short(x) => obj("short", J::from(*x)),
        NbtValue::Int(x) => obj("int", J::from(*x)),
        NbtValue::Long(x) => obj("long", J::from(x.to_string())),
        NbtValue::Float(x) => obj("float", J::from(*x)),
        NbtValue::Double(x) => obj("double", J::from(*x)),
        NbtValue::ByteArray(b) => obj("byteArray", J::from(b.clone())),
        NbtValue::String(s) => obj("string", J::from(s.clone())),
        NbtValue::List(items) => obj("list", J::Array(items.iter().map(to_json).collect())),
        NbtValue::Compound(m) => {
            let mut o = Map::new();
            for (k, val) in m {
                o.insert(k.clone(), to_json(val));
            }
            obj("compound", J::Object(o))
        }
        NbtValue::IntArray(a) => obj("intArray", J::Array(a.iter().map(|x| J::from(*x)).collect())),
        NbtValue::LongArray(a) => obj(
            "longArray",
            J::Array(a.iter().map(|x| J::from(x.to_string())).collect()),
        ),
    }
}

/// Decode a type-tagged JSON value back into NBT.
pub fn from_json(j: &J) -> Result<NbtValue> {
    let map = j
        .as_object()
        .ok_or_else(|| NbtError::InvalidJson("expected object".into()))?;
    let (tag, val) = map
        .iter()
        .next()
        .ok_or_else(|| NbtError::InvalidJson("empty object".into()))?;
    if map.len() != 1 {
        return Err(NbtError::InvalidJson(format!(
            "expected single tag, got {}",
            map.len()
        )));
    }
    let bad = |what: &str| NbtError::InvalidJson(format!("bad {what}"));
    Ok(match tag.as_str() {
        "byte" => NbtValue::Byte(val.as_i64().ok_or_else(|| bad("byte"))? as i8),
        "short" => NbtValue::Short(val.as_i64().ok_or_else(|| bad("short"))? as i16),
        "int" => NbtValue::Int(val.as_i64().ok_or_else(|| bad("int"))? as i32),
        "long" => NbtValue::Long(
            val.as_str()
                .ok_or_else(|| bad("long"))?
                .parse()
                .map_err(|_| bad("long"))?,
        ),
        "float" => NbtValue::Float(val.as_f64().ok_or_else(|| bad("float"))? as f32),
        "double" => NbtValue::Double(val.as_f64().ok_or_else(|| bad("double"))?),
        "byteArray" => {
            let arr = val.as_array().ok_or_else(|| bad("byteArray"))?;
            let mut b = Vec::with_capacity(arr.len());
            for x in arr {
                b.push(x.as_u64().ok_or_else(|| bad("byteArray"))? as u8);
            }
            NbtValue::ByteArray(b)
        }
        "string" => NbtValue::String(val.as_str().ok_or_else(|| bad("string"))?.to_string()),
        "list" => {
            let arr = val.as_array().ok_or_else(|| bad("list"))?;
            let mut items = Vec::with_capacity(arr.len());
            for x in arr {
                items.push(from_json(x)?);
            }
            NbtValue::List(items)
        }
        "compound" => {
            let o = val.as_object().ok_or_else(|| bad("compound"))?;
            let mut m = Compound::new();
            for (k, v) in o {
                m.insert(k.clone(), from_json(v)?);
            }
            NbtValue::Compound(m)
        }
        "intArray" => {
            let arr = val.as_array().ok_or_else(|| bad("intArray"))?;
            let mut a = Vec::with_capacity(arr.len());
            for x in arr {
                a.push(x.as_i64().ok_or_else(|| bad("intArray"))? as i32);
            }
            NbtValue::IntArray(a)
        }
        "longArray" => {
            let arr = val.as_array().ok_or_else(|| bad("longArray"))?;
            let mut a = Vec::with_capacity(arr.len());
            for x in arr {
                a.push(
                    x.as_str()
                        .ok_or_else(|| bad("longArray"))?
                        .parse()
                        .map_err(|_| bad("longArray"))?,
                );
            }
            NbtValue::LongArray(a)
        }
        other => return Err(NbtError::InvalidJson(format!("unknown tag {other:?}"))),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    #[test]
    fn large_long_survives_roundtrip() {
        let big = NbtValue::Long(9_007_199_254_740_993); // 2^53 + 1
        let j = to_json(&big);
        assert_eq!(j["long"], J::from("9007199254740993"));
        assert_eq!(from_json(&j).unwrap(), big);
    }

    #[test]
    fn nested_roundtrips() {
        let mut m = Compound::new();
        m.insert("name".into(), NbtValue::String("x".into()));
        m.insert("cells".into(), NbtValue::LongArray(vec![1, -2, 1 << 60]));
        m.insert(
            "kids".into(),
            NbtValue::List(vec![NbtValue::Byte(1), NbtValue::Byte(2)]),
        );
        let v = NbtValue::Compound(m);
        assert_eq!(from_json(&to_json(&v)).unwrap(), v);
    }

    #[test]
    fn rejects_multi_key_object() {
        let mut m = Map::new();
        m.insert("int".into(), J::from(1));
        m.insert("extra".into(), J::from(2));
        assert!(from_json(&J::Object(m)).is_err());
    }
}

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
        // Floats/doubles are string-encoded (like longs) so the value round-trips
        // bit-for-bit: serde_json's default float *parser* is up to 1 ULP lossy,
        // and NaN/Inf have no JSON-number form. Rust's f32/f64 <-> string is exact.
        NbtValue::Float(x) => obj("float", J::from(x.to_string())),
        NbtValue::Double(x) => obj("double", J::from(x.to_string())),
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
        NbtValue::IntArray(a) => obj(
            "intArray",
            J::Array(a.iter().map(|x| J::from(*x)).collect()),
        ),
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
        // Prefer the exact string form; accept a legacy JSON-number form too.
        "float" => NbtValue::Float(match val.as_str() {
            Some(s) => s.parse::<f32>().map_err(|_| bad("float"))?,
            // legacy JSON-number form: parsed as f64 then narrowed (sub-ULP loss
            // possible, acceptable for back-compat reads of pre-fix patches).
            None => val.as_f64().ok_or_else(|| bad("float"))? as f32,
        }),
        "double" => NbtValue::Double(match val.as_str() {
            Some(s) => s.parse::<f64>().map_err(|_| bad("double"))?,
            None => val.as_f64().ok_or_else(|| bad("double"))?,
        }),
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

    #[test]
    fn double_survives_file_roundtrip() {
        for x in [
            -1.8179372026061453_f64,
            8.5,
            -7.064525711408038,
            f64::MIN_POSITIVE,
        ] {
            let v = NbtValue::Double(x);
            let s = serde_json::to_string(&to_json(&v)).unwrap();
            let j2: J = serde_json::from_str(&s).unwrap();
            assert_eq!(from_json(&j2).unwrap(), v, "double {x} serialized as {s}");
        }
    }

    #[test]
    fn nonfinite_double_survives() {
        for x in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            let v = NbtValue::Double(x);
            let s = serde_json::to_string(&to_json(&v)).unwrap();
            let j2: J = serde_json::from_str(&s).unwrap();
            let got = from_json(&j2).unwrap();
            match (&v, &got) {
                (NbtValue::Double(a), NbtValue::Double(b)) => {
                    assert!(
                        a == b || (a.is_nan() && b.is_nan()),
                        "double {x} -> {s} -> {got:?}"
                    )
                }
                _ => panic!("type changed: {x} -> {s} -> {got:?}"),
            }
        }
    }

    #[test]
    fn nonfinite_float_survives() {
        for x in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            let v = NbtValue::Float(x);
            let s = serde_json::to_string(&to_json(&v)).unwrap();
            let j2: J = serde_json::from_str(&s).unwrap();
            let got = from_json(&j2).unwrap();
            match (&v, &got) {
                (NbtValue::Float(a), NbtValue::Float(b)) => {
                    assert!(
                        a == b || (a.is_nan() && b.is_nan()),
                        "float {x} -> {s} -> {got:?}"
                    )
                }
                _ => panic!("type changed: {x} -> {s} -> {got:?}"),
            }
        }
    }
}

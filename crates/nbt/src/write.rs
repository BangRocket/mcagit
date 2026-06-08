//! Big-endian NBT binary writer.

use crate::mutf8;
use crate::value::{tag_id, NbtValue};

fn write_string(out: &mut Vec<u8>, s: &str) {
    let enc = mutf8::encode(s);
    out.extend_from_slice(&(enc.len() as u16).to_be_bytes());
    out.extend_from_slice(&enc);
}

fn write_payload(out: &mut Vec<u8>, v: &NbtValue, sort: bool) {
    match v {
        NbtValue::Byte(x) => out.push(*x as u8),
        NbtValue::Short(x) => out.extend_from_slice(&x.to_be_bytes()),
        NbtValue::Int(x) => out.extend_from_slice(&x.to_be_bytes()),
        NbtValue::Long(x) => out.extend_from_slice(&x.to_be_bytes()),
        NbtValue::Float(x) => out.extend_from_slice(&x.to_be_bytes()),
        NbtValue::Double(x) => out.extend_from_slice(&x.to_be_bytes()),
        NbtValue::ByteArray(b) => {
            out.extend_from_slice(&(b.len() as i32).to_be_bytes());
            out.extend_from_slice(b);
        }
        NbtValue::String(s) => write_string(out, s),
        NbtValue::List(items) => {
            let elem = items.first().map(tag_id).unwrap_or(0);
            out.push(elem);
            out.extend_from_slice(&(items.len() as i32).to_be_bytes());
            for it in items {
                write_payload(out, it, sort);
            }
        }
        NbtValue::Compound(m) => {
            if sort {
                let mut keys: Vec<&String> = m.keys().collect();
                keys.sort();
                for k in keys {
                    let val = &m[k];
                    out.push(tag_id(val));
                    write_string(out, k);
                    write_payload(out, val, sort);
                }
            } else {
                for (k, val) in m {
                    out.push(tag_id(val));
                    write_string(out, k);
                    write_payload(out, val, sort);
                }
            }
            out.push(0); // TAG_End
        }
        NbtValue::IntArray(a) => {
            out.extend_from_slice(&(a.len() as i32).to_be_bytes());
            for x in a {
                out.extend_from_slice(&x.to_be_bytes());
            }
        }
        NbtValue::LongArray(a) => {
            out.extend_from_slice(&(a.len() as i32).to_be_bytes());
            for x in a {
                out.extend_from_slice(&x.to_be_bytes());
            }
        }
    }
}

/// Write a complete NBT document with the given root `name`. When `sort` is
/// true, compound keys are emitted in sorted order (used for canonical form).
pub fn write_named(name: &str, v: &NbtValue, sort: bool) -> Vec<u8> {
    let mut out = Vec::new();
    out.push(tag_id(v));
    write_string(&mut out, name);
    write_payload(&mut out, v, sort);
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
    fn empty_list_writes_end_element_type() {
        let v = NbtValue::List(vec![]);
        let bytes = write_named("", &v, false);
        // tag(9), name-len(0,0), elem-type(0), len(0,0,0,0)
        assert_eq!(bytes, vec![9, 0, 0, 0, 0, 0, 0, 0]);
    }
}

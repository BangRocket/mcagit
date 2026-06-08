//! Big-endian NBT binary reader.

use crate::value::{Compound, NbtValue};
use crate::{mutf8, NbtError, Result};

struct Reader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let end = self.pos.checked_add(n).ok_or(NbtError::UnexpectedEof)?;
        let slice = self.buf.get(self.pos..end).ok_or(NbtError::UnexpectedEof)?;
        self.pos = end;
        Ok(slice)
    }

    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    fn i16(&mut self) -> Result<i16> {
        Ok(i16::from_be_bytes(self.take(2)?.try_into().unwrap()))
    }
    fn i32(&mut self) -> Result<i32> {
        Ok(i32::from_be_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn i64(&mut self) -> Result<i64> {
        Ok(i64::from_be_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn f32(&mut self) -> Result<f32> {
        Ok(f32::from_be_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn f64(&mut self) -> Result<f64> {
        Ok(f64::from_be_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn len(&mut self) -> Result<usize> {
        usize::try_from(self.i32()?).map_err(|_| NbtError::NegativeLength)
    }
    fn string(&mut self) -> Result<String> {
        let n = u16::from_be_bytes(self.take(2)?.try_into().unwrap()) as usize;
        mutf8::decode(self.take(n)?)
    }

    fn payload(&mut self, tag: u8) -> Result<NbtValue> {
        Ok(match tag {
            1 => NbtValue::Byte(self.u8()? as i8),
            2 => NbtValue::Short(self.i16()?),
            3 => NbtValue::Int(self.i32()?),
            4 => NbtValue::Long(self.i64()?),
            5 => NbtValue::Float(self.f32()?),
            6 => NbtValue::Double(self.f64()?),
            7 => {
                let n = self.len()?;
                NbtValue::ByteArray(self.take(n)?.to_vec())
            }
            8 => NbtValue::String(self.string()?),
            9 => {
                let elem = self.u8()?;
                let n = self.len()?;
                let mut items = Vec::with_capacity(n.min(1024));
                for _ in 0..n {
                    items.push(self.payload(elem)?);
                }
                NbtValue::List(items)
            }
            10 => {
                let mut map = Compound::new();
                loop {
                    let t = self.u8()?;
                    if t == 0 {
                        break;
                    }
                    let name = self.string()?;
                    let val = self.payload(t)?;
                    map.insert(name, val);
                }
                NbtValue::Compound(map)
            }
            11 => {
                let n = self.len()?;
                let mut a = Vec::with_capacity(n.min(1024));
                for _ in 0..n {
                    a.push(self.i32()?);
                }
                NbtValue::IntArray(a)
            }
            12 => {
                let n = self.len()?;
                let mut a = Vec::with_capacity(n.min(1024));
                for _ in 0..n {
                    a.push(self.i64()?);
                }
                NbtValue::LongArray(a)
            }
            other => return Err(NbtError::UnknownTag(other)),
        })
    }
}

/// Read a complete NBT document: returns the root tag's name and value.
pub fn read(buf: &[u8]) -> Result<(String, NbtValue)> {
    let mut r = Reader::new(buf);
    let tag = r.u8()?;
    if tag == 0 {
        return Err(NbtError::UnknownTag(0));
    }
    let name = r.string()?;
    let value = r.payload(tag)?;
    Ok((name, value))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_named_compound_with_int() {
        // tag=10 (Compound), name="root", child tag=3 (Int) name="n" value=5, End
        let bytes = [
            10, // compound
            0, 4, b'r', b'o', b'o', b't', // name "root"
            3, 0, 1, b'n', 0, 0, 0, 5, // int "n" = 5
            0, // end
        ];
        let (name, val) = read(&bytes).unwrap();
        assert_eq!(name, "root");
        let NbtValue::Compound(m) = val else {
            panic!("expected compound")
        };
        assert_eq!(m.get("n"), Some(&NbtValue::Int(5)));
    }

    #[test]
    fn truncated_input_errors() {
        assert_eq!(
            read(&[10, 0, 4, b'r']).unwrap_err(),
            NbtError::UnexpectedEof
        );
    }
}

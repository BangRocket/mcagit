//! Java "modified UTF-8" (CESU-8 + U+0000 as 0xC0 0x80) used for NBT strings.

use crate::{NbtError, Result};

/// Decode modified-UTF8 bytes into a Rust `String`.
pub fn decode(bytes: &[u8]) -> Result<String> {
    cesu8::from_java_cesu8(bytes)
        .map(|cow| cow.into_owned())
        .map_err(|_| NbtError::InvalidString)
}

/// Encode a Rust string into modified-UTF8 bytes.
pub fn encode(s: &str) -> Vec<u8> {
    cesu8::to_java_cesu8(s).into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ascii_roundtrips() {
        let bytes = encode("Pos");
        assert_eq!(bytes, b"Pos");
        assert_eq!(decode(&bytes).unwrap(), "Pos");
    }

    #[test]
    fn embedded_null_uses_two_bytes() {
        // Modified UTF-8 encodes U+0000 as 0xC0 0x80, never a bare 0x00.
        let bytes = encode("a\0b");
        assert_eq!(bytes, vec![b'a', 0xC0, 0x80, b'b']);
        assert_eq!(decode(&bytes).unwrap(), "a\0b");
    }

    #[test]
    fn unicode_roundtrips() {
        let s = "résumé☃";
        assert_eq!(decode(&encode(s)).unwrap(), s);
    }

    #[test]
    fn invalid_bytes_error() {
        // 0xFF is never a valid lead byte in (modified) UTF-8.
        // (cesu8 is lenient about a bare 0x00, so we test a truly malformed byte.)
        assert!(decode(&[0xFF]).is_err());
    }
}

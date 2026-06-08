//! NBT binary reader — delegates parsing of (untrusted) Java NBT bytes to the
//! battle-tested `valence_nbt`, then converts into our uniform [`NbtValue`].

use crate::conv::from_compound;
use crate::value::NbtValue;
use crate::{NbtError, Result};

/// Read a complete NBT document: returns the root tag's name and value. The NBT
/// root is always a compound.
pub fn read(buf: &[u8]) -> Result<(String, NbtValue)> {
    let mut slice = buf;
    let (compound, name) = valence_nbt::from_binary::<String>(&mut slice)
        .map_err(|e| NbtError::Binary(e.to_string()))?;
    Ok((name, NbtValue::Compound(from_compound(compound))))
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
        assert!(read(&[10, 0, 4, b'r']).is_err());
    }
}

//! One-shot wire packs: a push of N new objects over http/ssh becomes one
//! request instead of N round-trips. Body layout (everything length-checked on
//! parse — a received pack is untrusted input):
//!
//! ```text
//! [u32 BE idx-len][idx JSON: { id: [offset, len] }][pack: concatenated zstd bodies]
//! ```
//!
//! Every object is hash-verified on parse (`blake3(content) == id`), so a
//! tampered pack can't poison a store, and every inflate is size-bounded so a
//! crafted pack can't decompression-bomb the server.

use crate::{RepoError, Result};
use std::collections::BTreeMap;
use std::io::Read;

/// Cap on a single decompressed object (matches the framed-message body cap).
const MAX_OBJECT_SIZE: u64 = 512 * 1024 * 1024;

/// Build a framed pack body from `(id, content)` pairs.
pub fn build(objects: &[(String, Vec<u8>)]) -> Result<Vec<u8>> {
    let mut pack: Vec<u8> = Vec::new();
    let mut idx: BTreeMap<&str, (u64, u64)> = BTreeMap::new();
    for (id, content) in objects {
        if idx.contains_key(id.as_str()) {
            continue;
        }
        let packed = zstd::encode_all(&content[..], 0)?;
        idx.insert(id, (pack.len() as u64, packed.len() as u64));
        pack.extend_from_slice(&packed);
    }
    let idx_json = serde_json::to_vec(&idx)?;
    let mut body = Vec::with_capacity(4 + idx_json.len() + pack.len());
    body.extend_from_slice(&(idx_json.len() as u32).to_be_bytes());
    body.extend_from_slice(&idx_json);
    body.extend_from_slice(&pack);
    Ok(body)
}

/// Parse a framed pack body, returning hash-verified `(id, content)` pairs.
/// Rejects bad framing, out-of-range offsets, oversized objects, and any
/// object whose content does not hash to its claimed id.
pub fn parse(body: &[u8]) -> Result<Vec<(String, Vec<u8>)>> {
    if body.len() < 4 {
        return Err(RepoError::Other("truncated pack body".into()));
    }
    let idx_len = u32::from_be_bytes([body[0], body[1], body[2], body[3]]) as usize;
    if 4 + idx_len > body.len() {
        return Err(RepoError::Other("bad pack-body framing".into()));
    }
    let idx: BTreeMap<String, (u64, u64)> = serde_json::from_slice(&body[4..4 + idx_len])
        .map_err(|e| RepoError::Other(format!("bad pack index: {e}")))?;
    let pack = &body[4 + idx_len..];

    let mut out = Vec::with_capacity(idx.len());
    for (id, (off, len)) in idx {
        let (off, len) = (off as usize, len as usize);
        let end = off
            .checked_add(len)
            .ok_or_else(|| RepoError::Other("pack entry overflows".into()))?;
        if end > pack.len() {
            return Err(RepoError::Other(format!(
                "pack entry out of range: {id} ({off}+{len} > {})",
                pack.len()
            )));
        }
        let content = bounded_decode(&pack[off..end])?;
        if blake3::hash(&content).to_hex().as_str() != id {
            return Err(RepoError::Other(format!("pack object hash mismatch: {id}")));
        }
        out.push((id, content));
    }
    Ok(out)
}

/// Decompress one zstd body with a hard output cap (anti decompression-bomb).
fn bounded_decode(packed: &[u8]) -> Result<Vec<u8>> {
    let dec = zstd::stream::read::Decoder::new(packed)
        .map_err(|e| RepoError::Other(format!("bad zstd body: {e}")))?;
    let mut out = Vec::new();
    dec.take(MAX_OBJECT_SIZE + 1)
        .read_to_end(&mut out)
        .map_err(|e| RepoError::Other(format!("bad zstd body: {e}")))?;
    if out.len() as u64 > MAX_OBJECT_SIZE {
        return Err(RepoError::Other("pack object too large".into()));
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn obj(content: &[u8]) -> (String, Vec<u8>) {
        (blake3::hash(content).to_hex().to_string(), content.to_vec())
    }

    #[test]
    fn build_parse_roundtrip() {
        let objects = vec![obj(b"alpha"), obj(b"beta beta beta"), obj(b"")];
        let body = build(&objects).unwrap();
        let mut parsed = parse(&body).unwrap();
        parsed.sort();
        let mut want = objects.clone();
        want.sort();
        assert_eq!(parsed, want);
    }

    #[test]
    fn tampered_pack_is_rejected() {
        let objects = vec![obj(b"payload-bytes-here")];
        let mut body = build(&objects).unwrap();
        let n = body.len();
        body[n - 1] ^= 0xff; // flip a byte in the compressed stream
        assert!(parse(&body).is_err());
    }

    #[test]
    fn wrong_id_is_rejected() {
        let (_, content) = obj(b"honest content");
        let lie = ("f".repeat(64), content);
        let body = build(&[lie]).unwrap();
        assert!(parse(&body).is_err(), "claimed id must match content hash");
    }

    #[test]
    fn malformed_framing_is_rejected() {
        assert!(parse(b"").is_err());
        assert!(parse(&[0xff, 0xff, 0xff, 0xff, 1, 2]).is_err());
        // valid length prefix but garbage index JSON
        let mut body = 5u32.to_be_bytes().to_vec();
        body.extend_from_slice(b"not j");
        assert!(parse(&body).is_err());
        // index pointing past the pack
        let idx = br#"{"aa":[0,999]}"#;
        let mut body = (idx.len() as u32).to_be_bytes().to_vec();
        body.extend_from_slice(idx);
        body.extend_from_slice(b"short");
        assert!(parse(&body).is_err());
    }
}

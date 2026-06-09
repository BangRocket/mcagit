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

/// Cap on the *total* decompressed bytes in one pack. Without this, a pack with
/// N entries each near [`MAX_OBJECT_SIZE`] forces N × 512 MiB of allocation
/// from a tiny (well-compressing) wire body — an amplification bomb. Legitimate
/// push batches are bounded far below this on the client (~128 MiB raw), so the
/// cap only ever trips on a hostile pack. Also the ceiling a fetch client allows for a
/// `getpack` HTTP response body — the actual decompression guard is [`for_each`] on ingest.
pub(crate) const MAX_PACK_TOTAL: u64 = 512 * 1024 * 1024;

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

/// Stream a framed pack body: decode + hash-verify each object and hand it to
/// `sink` **one at a time**, never holding more than one object plus the body.
/// Rejects bad framing, out-of-range offsets, oversized objects, any object
/// whose content doesn't hash to its claimed id, and any pack whose total
/// decompressed size exceeds [`MAX_PACK_TOTAL`] (the amplification-bomb guard).
/// Returns the number of objects handed to `sink`.
pub fn for_each<F: FnMut(&str, Vec<u8>) -> Result<()>>(body: &[u8], mut sink: F) -> Result<usize> {
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

    let mut total: u64 = 0;
    let mut n = 0;
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
        total = total.saturating_add(content.len() as u64);
        if total > MAX_PACK_TOTAL {
            return Err(RepoError::Other("pack total size exceeds cap".into()));
        }
        if blake3::hash(&content).to_hex().as_str() != id {
            return Err(RepoError::Other(format!("pack object hash mismatch: {id}")));
        }
        sink(&id, content)?;
        n += 1;
    }
    Ok(n)
}

/// Parse a framed pack body into hash-verified `(id, content)` pairs. Bounded
/// by [`MAX_PACK_TOTAL`]. Prefer [`for_each`] for ingest (it never accumulates);
/// this collecting form is for the small callers that need a whole small pack
/// in hand (downloading one bucket pack, tests).
pub fn parse(body: &[u8]) -> Result<Vec<(String, Vec<u8>)>> {
    let mut out = Vec::new();
    for_each(body, |id, content| {
        out.push((id.to_string(), content));
        Ok(())
    })?;
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

    // ---- amplification-bomb regression tests (see the pack-ingest audit) ----

    /// A pack whose objects sum past `MAX_PACK_TOTAL` is rejected before the
    /// allocation can grow unbounded — the fix for the N-object decompression
    /// bomb. Two valid 300 MiB objects (each passes its hash check, compresses
    /// tiny as a zero-run) decode to 600 MiB > the 512 MiB cap, so the second
    /// trips the cap.
    #[test]
    fn total_size_cap_rejects_amplification_bomb() {
        let object_bytes = 300 * 1024 * 1024usize;
        let objects: Vec<(String, Vec<u8>)> = [1u8, 2u8]
            .iter()
            .map(|&i| {
                let mut c = vec![0u8; object_bytes];
                c[object_bytes - 1] = i; // distinct content → distinct, valid id
                obj(&c)
            })
            .collect();
        let body = build(&objects).unwrap();
        assert!(body.len() < object_bytes, "wire body stays tiny");

        let err = parse(&body).unwrap_err().to_string();
        assert!(err.contains("total size"), "got: {err}");
    }

    /// `for_each` streams: a well-formed pack hands each object to the sink once
    /// and never returns them all at once.
    #[test]
    fn for_each_streams_each_object_once() {
        let objects = vec![obj(b"one"), obj(b"two"), obj(b"three")];
        let body = build(&objects).unwrap();
        let mut seen = Vec::new();
        let n = for_each(&body, |id, content| {
            seen.push((id.to_string(), content));
            Ok(())
        })
        .unwrap();
        assert_eq!(n, 3);
        seen.sort();
        let mut want = objects;
        want.sort();
        assert_eq!(seen, want);
    }

    /// Duplicate index keys collapse (serde_json last-wins), so an attacker
    /// can't force re-decompression of the same body via repeated keys.
    #[test]
    fn duplicate_idx_keys_are_deduplicated() {
        let content = b"dedup test payload";
        let (id, _) = obj(content);
        let compressed = zstd::encode_all(&content[..], 0).unwrap();
        let idx_json = format!(
            r#"{{"{id}":[0,{good}],"{id}":[0,999]}}"#,
            good = compressed.len()
        );
        let mut body = (idx_json.len() as u32).to_be_bytes().to_vec();
        body.extend_from_slice(idx_json.as_bytes());
        body.extend_from_slice(&compressed);
        // last-wins [0,999] is out of range → rejected; no double-decompress.
        assert!(parse(&body).is_err());
    }
}

//! Regression tests for the pack-ingest amplification-bomb findings (pack audit
//! on commit 288ee6e) and the cloud/bucket transport security audit (commit
//! 4073347). The fixes added: a total-decoded-bytes cap to
//! `wirepack::for_each`/`parse`; `require_valid_pack_id` before any key is
//! built from a manifest line; `safe_ref_name` filtering on every key returned
//! by `bucket.list()` before it becomes a local ref; and hash-verification
//! inside `wirepack::parse` so an object injected under a wrong id is rejected.

use mca_repo::wirepack;
use mca_repo::{Bucket, BucketTransport, InMemoryBucket, Transport};

/// A pack whose objects sum past the total cap is rejected, no matter how small
/// the wire body. Two valid 300 MiB zero-run objects (each passes its hash
/// check, compresses tiny) decode to 600 MiB > the cap, so the second trips it.
#[test]
fn pack_total_size_is_capped() {
    let object_bytes = 300 * 1024 * 1024usize;
    let objects: Vec<(String, Vec<u8>)> = [1u8, 2u8]
        .iter()
        .map(|&i| {
            let mut c = vec![0u8; object_bytes];
            c[object_bytes - 1] = i; // distinct content → distinct, valid id
            (blake3::hash(&c).to_hex().to_string(), c)
        })
        .collect();
    let body = wirepack::build(&objects).unwrap();
    assert!(body.len() < object_bytes, "compressed body stays tiny");

    let err = wirepack::parse(&body).unwrap_err().to_string();
    assert!(
        err.contains("total size"),
        "expected total-size cap, got: {err}"
    );
}

/// A pack within the cap streams every object through `for_each` exactly once
/// (no whole-batch accumulation).
#[test]
fn pack_within_cap_streams_each_object() {
    let objects: Vec<(String, Vec<u8>)> = (0..5u8)
        .map(|i| {
            let c = vec![i; 1024];
            (blake3::hash(&c).to_hex().to_string(), c)
        })
        .collect();
    let body = wirepack::build(&objects).unwrap();
    let mut count = 0;
    let n = wirepack::for_each(&body, |_id, _content| {
        count += 1;
        Ok(())
    })
    .unwrap();
    assert_eq!(n, 5);
    assert_eq!(count, 5);
}

// ---- Cloud/bucket transport security PoCs (commit 4073347 audit) ----

/// Attack: hostile bucket advertises a `packs/manifest` whose lines contain path
/// traversal strings instead of 64-hex ids.  Before the fix the id was used
/// verbatim to build `packs/<id>` keys; now `require_valid_pack_id` rejects
/// anything that isn't exactly 64 lowercase hex chars.
///
/// Confirming: `missing()` (which triggers `ensure_index`) returns an error
/// rather than issuing a `get("packs/../../etc/passwd")` call.
#[test]
fn bucket_traversal_pack_id_in_manifest_is_rejected() {
    // Three hostile manifest lines: Unix traversal, absolute path, Windows drive.
    for hostile in &["../../etc/passwd", "/etc/shadow", "C:\\Windows\\win.ini"] {
        let b = InMemoryBucket::default();
        b.put("r/packs/manifest", hostile.as_bytes()).unwrap();
        let t = BucketTransport::new(Box::new(b), "r");
        let err = t
            .missing(&["a".repeat(64)])
            .expect_err("traversal pack id must be rejected");
        assert!(
            err.to_string().contains("malformed pack id"),
            "hostile manifest line '{hostile}' should produce malformed-pack-id error, got: {err}"
        );
    }
}

/// Attack: hostile bucket returns keys under `refs/heads/` that contain `..`
/// segments intended to escape the refs directory (e.g. a key named
/// `<prefix>/refs/heads/../../../evil`).
///
/// `BucketTransport::read_refs` slices off the prefix, then passes the
/// remainder through `safe_ref_name`.  Traversal names must be silently
/// dropped — they must not appear in the output of `list_refs`.
#[test]
fn bucket_traversal_ref_name_from_list_is_silently_dropped() {
    let bucket = InMemoryBucket::default();
    // A legitimate ref sitting next to hostile ones — it must survive.
    let good_hash = "a".repeat(64);
    bucket
        .put("r/refs/heads/main", format!("{good_hash}\n").as_bytes())
        .unwrap();
    // Hostile ref names: classic traversal, absolute path, NUL byte.
    for hostile_name in &[
        "../../../etc/evil",
        "/etc/shadow",
        "back\\slash",
        "null\x00byte",
    ] {
        bucket
            .put(
                &format!("r/refs/heads/{hostile_name}"),
                format!("{good_hash}\n").as_bytes(),
            )
            .unwrap();
    }
    let t = BucketTransport::new(Box::new(bucket), "r");
    let refs = t.list_refs().unwrap();
    // Only the legitimate ref must appear.
    assert_eq!(
        refs,
        vec![("refs/heads/main".to_string(), good_hash)],
        "hostile ref names must be dropped by safe_ref_name; got: {refs:?}"
    );
}

/// Attack: hostile bucket stores a pack whose object's actual content does NOT
/// hash to the id the index claims.  `wirepack::parse` computes blake3 of the
/// decompressed content and compares it to the id; a mismatch returns an error,
/// so the object is never inserted into the local store.
///
/// Without the hash check an attacker could substitute arbitrary bytes for any
/// object, poisoning the clone's object store.
#[test]
fn bucket_pack_with_wrong_object_id_is_rejected() {
    // Build a well-formed pack body but then patch the index to advertise the
    // object under a different (all-'b') id — simulating a hostile pack where
    // the content doesn't match the claimed hash.
    let real_content = b"honest chunk payload";
    let real_id = blake3::hash(real_content).to_hex().to_string();
    let objects = vec![(real_id, real_content.to_vec())];
    let good_body = wirepack::build(&objects).unwrap();

    // Read the 4-byte index length.
    let idx_len =
        u32::from_be_bytes([good_body[0], good_body[1], good_body[2], good_body[3]]) as usize;
    let idx_json = &good_body[4..4 + idx_len];
    let pack_bytes = &good_body[4 + idx_len..];

    // Replace the real id in the JSON with a fake 64-'b' id.
    let fake_id = "b".repeat(64);
    let real_id2 = blake3::hash(real_content).to_hex().to_string();
    let patched_idx = String::from_utf8_lossy(idx_json).replace(&real_id2, &fake_id);

    let mut hostile_body: Vec<u8> = Vec::new();
    hostile_body.extend_from_slice(&(patched_idx.len() as u32).to_be_bytes());
    hostile_body.extend_from_slice(patched_idx.as_bytes());
    hostile_body.extend_from_slice(pack_bytes);

    // The pack must be rejected: content's real hash != the fake id in the index.
    let err = wirepack::parse(&hostile_body).expect_err("mismatched id must be rejected");
    assert!(
        err.to_string().contains("hash mismatch"),
        "expected hash-mismatch error, got: {err}"
    );
}

/// Attack: hostile bucket tries to force a ref to point at a non-hex "hash"
/// (e.g. a traversal string) via the ref blob content.  The hash value from a
/// ref blob flows into `BucketTransport::fetch_object` → `is_object_id` check
/// before any disk operation, so a non-hex value returns an error immediately
/// without writing anything locally.
///
/// Concretely: if `refs/heads/main` blob contains `../../evil` instead of a
/// 64-hex commit hash, `list_refs` returns that string, `fetch_reachable` calls
/// `t.get_object("../../evil")`, and `fetch_object` rejects it before any
/// local write occurs.
#[test]
fn bucket_non_hex_ref_content_rejected_by_fetch_object() {
    let bucket = InMemoryBucket::default();
    // Blob content is a traversal string, not a 64-hex hash.
    bucket
        .put("r/refs/heads/main", b"../../etc/passwd\n")
        .unwrap();
    let t = BucketTransport::new(Box::new(bucket), "r");
    // list_refs returns the raw blob content as the hash (after safe_ref_name passes "main").
    let refs = t.list_refs().unwrap();
    assert!(
        refs.iter().any(|(r, _)| r == "refs/heads/main"),
        "main ref must be listed (name passes safe_ref_name)"
    );
    // But fetching the object at that non-hex hash must fail.
    let hash = refs
        .iter()
        .find(|(r, _)| r == "refs/heads/main")
        .map(|(_, h)| h.clone())
        .unwrap();
    let err = t
        .get_object(&hash)
        .expect_err("non-hex hash must be rejected by is_object_id");
    assert!(
        err.to_string().contains("invalid object id"),
        "expected invalid-object-id error, got: {err}"
    );
}

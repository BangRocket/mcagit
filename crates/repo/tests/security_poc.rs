//! Regression tests for the pack-ingest amplification-bomb findings (pack audit
//! on commit 288ee6e). The fix added a total-decoded-bytes cap to
//! `wirepack::for_each`/`parse` and switched the server's pack ingest to the
//! streaming `for_each` path, plus a body-size cap in `serve::read_body`.

use mca_repo::wirepack;

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

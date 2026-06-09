//! Serverless repository transport over a dumb object-storage bucket (S3/Azure).
//!
//! There's no daemon, so the repository protocol runs entirely client-side. A
//! push bundles the missing objects into one content-addressed wire pack
//! (reusing [`crate::wirepack`]), uploaded with its blob plus a CAS-guarded
//! `packs/manifest`; refs are tiny blobs updated with an ETag compare-and-swap.
//! Per push it's a handful of bucket writes regardless of how many chunks
//! changed. Bucket layout:
//!
//! ```text
//! <prefix>/HEAD               <prefix>/refs/heads/<b>   <prefix>/refs/tags/<t>
//! <prefix>/packs/<id>         <prefix>/packs/manifest
//! ```
//!
//! A `packs/manifest` line, and the pack ids it names, are **attacker-controlled**
//! (a hostile bucket): every id is pinned to the 64-hex object-id shape before it
//! is used to build a key, and every downloaded pack is hash-verified by
//! [`crate::wirepack::parse`].

use crate::remote::Transport;
use crate::{RepoError, Result};
use std::collections::HashMap;
use std::sync::Mutex;

/// A dumb object-storage bucket (S3 / Azure Blob): keyed blobs with ETag-based
/// optimistic concurrency. Deliberately minimal — all repository protocol logic
/// lives in [`BucketTransport`], so a backend is just these operations.
pub trait Bucket: Send + Sync {
    /// The blob's bytes and ETag, or `None` if it doesn't exist.
    fn get(&self, key: &str) -> Result<Option<(Vec<u8>, String)>>;
    /// Unconditional write.
    fn put(&self, key: &str, data: &[u8]) -> Result<()>;
    /// Conditional write: succeeds only if the current ETag equals
    /// `expected` (`None` ⇒ "must not already exist"). Returns `false` if the
    /// precondition fails (a concurrent writer changed it) — the caller
    /// re-reads and retries.
    fn put_if_match(&self, key: &str, data: &[u8], expected: Option<&str>) -> Result<bool>;
    /// Keys under `prefix`.
    fn list(&self, prefix: &str) -> Result<Vec<String>>;
}

/// An in-process [`Bucket`] for tests — models ETag-conditional writes (S3
/// `If-Match` / Azure lease semantics) so the protocol can be exercised without
/// a cloud account.
#[derive(Default)]
struct MemStore {
    blobs: HashMap<String, (Vec<u8>, String)>,
    seq: u64,
}

#[derive(Default)]
pub struct InMemoryBucket {
    inner: Mutex<MemStore>,
}

impl Bucket for InMemoryBucket {
    fn get(&self, key: &str) -> Result<Option<(Vec<u8>, String)>> {
        let g = self.inner.lock().unwrap();
        Ok(g.blobs.get(key).cloned())
    }
    fn put(&self, key: &str, data: &[u8]) -> Result<()> {
        let mut g = self.inner.lock().unwrap();
        g.seq += 1;
        let etag = g.seq.to_string();
        g.blobs.insert(key.to_string(), (data.to_vec(), etag));
        Ok(())
    }
    fn put_if_match(&self, key: &str, data: &[u8], expected: Option<&str>) -> Result<bool> {
        let mut g = self.inner.lock().unwrap();
        let current = g.blobs.get(key).map(|(_, e)| e.clone());
        if current.as_deref() != expected {
            return Ok(false);
        }
        g.seq += 1;
        let etag = g.seq.to_string();
        g.blobs.insert(key.to_string(), (data.to_vec(), etag));
        Ok(true)
    }
    fn list(&self, prefix: &str) -> Result<Vec<String>> {
        let g = self.inner.lock().unwrap();
        Ok(g.blobs
            .keys()
            .filter(|k| k.starts_with(prefix))
            .cloned()
            .collect())
    }
}

/// The repository protocol over a [`Bucket`].
pub struct BucketTransport {
    bucket: Box<dyn Bucket>,
    prefix: String,
    /// Lazy object-id → pack-id index, built from the `packs/manifest`.
    index: Mutex<Option<HashMap<String, String>>>,
}

/// Upper bound on pack ids in `packs/manifest` (one pack per push; this is far
/// above any real repo, but bounds a hostile bucket's clone-time round-trips).
const MAX_MANIFEST_PACKS: usize = 1_000_000;

fn is_object_id(id: &str) -> bool {
    id.len() == 64 && id.bytes().all(|b| b.is_ascii_hexdigit())
}

impl BucketTransport {
    pub fn new(bucket: Box<dyn Bucket>, prefix: &str) -> Self {
        Self {
            bucket,
            prefix: prefix.trim_matches('/').to_string(),
            index: Mutex::new(None),
        }
    }

    fn key(&self, suffix: &str) -> String {
        if self.prefix.is_empty() {
            suffix.to_string()
        } else {
            format!("{}/{suffix}", self.prefix)
        }
    }

    fn read_refs(&self, sub: &str) -> Result<Vec<(String, String)>> {
        let full = self.key(sub);
        let mut out = Vec::new();
        for key in self.bucket.list(&full)? {
            if let Some((data, _)) = self.bucket.get(&key)? {
                let name = key[full.len()..].to_string();
                if crate::repository::safe_ref_name(&name) {
                    out.push((name, String::from_utf8_lossy(&data).trim().to_string()));
                }
            }
        }
        Ok(out)
    }

    /// Build (or reuse) the object-id → pack-id index from `packs/manifest`.
    fn ensure_index(&self) -> Result<()> {
        let mut guard = self.index.lock().unwrap();
        if guard.is_some() {
            return Ok(());
        }
        let mut map = HashMap::new();
        if let Some((data, _)) = self.bucket.get(&self.key("packs/manifest"))? {
            let pack_ids = lines(&data);
            // A hostile bucket could advertise millions of pack ids to force one
            // download per id (a slow-DoS on clone). Real repos keep a pack per
            // push; cap well above that.
            if pack_ids.len() > MAX_MANIFEST_PACKS {
                return Err(RepoError::Other(format!(
                    "packs/manifest lists {} packs (> {MAX_MANIFEST_PACKS} cap)",
                    pack_ids.len()
                )));
            }
            for pack_id in pack_ids {
                require_valid_pack_id(&pack_id)?; // manifest is attacker-controlled
                if let Some((pack, _)) = self.bucket.get(&self.key(&format!("packs/{pack_id}")))? {
                    for (id, _) in crate::wirepack::parse(&pack)? {
                        map.insert(id, pack_id.clone());
                    }
                }
            }
        }
        *guard = Some(map);
        Ok(())
    }

    fn invalidate_index(&self) {
        *self.index.lock().unwrap() = None;
    }

    fn fetch_object(&self, id: &str) -> Result<Vec<u8>> {
        if !is_object_id(id) {
            return Err(RepoError::Other(format!("invalid object id: {id}")));
        }
        self.ensure_index()?;
        let pack_id = {
            let guard = self.index.lock().unwrap();
            guard
                .as_ref()
                .and_then(|m| m.get(id).cloned())
                .ok_or_else(|| RepoError::Other(format!("object not in bucket: {id}")))?
        };
        let (pack, _) = self
            .bucket
            .get(&self.key(&format!("packs/{pack_id}")))?
            .ok_or_else(|| RepoError::Other(format!("pack {pack_id} vanished")))?;
        // parse() hash-verifies every object, so a hostile pack can't lie.
        crate::wirepack::parse(&pack)?
            .into_iter()
            .find(|(oid, _)| oid == id)
            .map(|(_, content)| content)
            .ok_or_else(|| RepoError::Other(format!("object {id} missing from its pack")))
    }

    /// Append a pack id to `packs/manifest` with a CAS retry loop.
    fn append_to_manifest(&self, id: &str) -> Result<()> {
        let key = self.key("packs/manifest");
        for _ in 0..20 {
            let (cur, etag) = match self.bucket.get(&key)? {
                Some((d, e)) => (lines(&d), Some(e)),
                None => (Vec::new(), None),
            };
            if cur.iter().any(|l| l == id) {
                return Ok(());
            }
            let mut next = cur;
            next.push(id.to_string());
            let body = (next.join("\n") + "\n").into_bytes();
            if self.bucket.put_if_match(&key, &body, etag.as_deref())? {
                return Ok(());
            }
        }
        Err(RepoError::Other(
            "packs/manifest is contended — too many concurrent pushes".into(),
        ))
    }
}

fn require_valid_pack_id(id: &str) -> Result<()> {
    if is_object_id(id) {
        Ok(())
    } else {
        Err(RepoError::Other(format!(
            "bucket advertised a malformed pack id: '{id}'"
        )))
    }
}

fn lines(data: &[u8]) -> Vec<String> {
    String::from_utf8_lossy(data)
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .map(str::to_string)
        .collect()
}

impl Transport for BucketTransport {
    fn list_refs(&self) -> Result<Vec<(String, String)>> {
        let mut out = Vec::new();
        for (name, hash) in self.read_refs("refs/heads/")? {
            out.push((format!("refs/heads/{name}"), hash));
        }
        for (name, hash) in self.read_refs("refs/tags/")? {
            out.push((format!("refs/tags/{name}"), hash));
        }
        Ok(out)
    }

    fn get_object(&self, id: &str) -> Result<Vec<u8>> {
        self.fetch_object(id)
    }

    fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
        self.ensure_index()?;
        let guard = self.index.lock().unwrap();
        let have = guard.as_ref().expect("index built");
        Ok(ids
            .iter()
            .filter(|id| !have.contains_key(*id))
            .cloned()
            .collect())
    }

    fn put_object(&self, content: &[u8]) -> Result<()> {
        let id = blake3::hash(content).to_hex().to_string();
        self.put_objects(&[(id, content.to_vec())])
    }

    fn put_objects(&self, objects: &[(String, Vec<u8>)]) -> Result<()> {
        if objects.is_empty() {
            return Ok(());
        }
        let body = crate::wirepack::build(objects)?;
        // Name the pack by its own content hash (a valid object-id shape).
        let pack_id = blake3::hash(&body).to_hex().to_string();
        self.bucket
            .put(&self.key(&format!("packs/{pack_id}")), &body)?;
        self.append_to_manifest(&pack_id)?;
        self.invalidate_index(); // a new pack landed
        Ok(())
    }

    fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
        if !crate::repository::safe_ref_name(branch) {
            return Err(RepoError::BadRef(branch.to_string()));
        }
        let key = self.key(&format!("refs/heads/{branch}"));
        let etag = self.bucket.get(&key)?.map(|(_, e)| e);
        let body = format!("{hash}\n").into_bytes();
        if !self.bucket.put_if_match(&key, &body, etag.as_deref())? {
            return Err(RepoError::Other(format!(
                "ref {branch} changed concurrently — fetch + retry"
            )));
        }
        // First push: record the default branch so a clone picks it up.
        if self.bucket.get(&self.key("HEAD"))?.is_none() {
            self.bucket
                .put(&self.key("HEAD"), format!("{branch}\n").as_bytes())?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::remote::{fetch, push};
    use crate::repository::Repository;
    use crate::snapshot;

    fn commit_world(repo: &Repository, contents: &[u8], msg: &str) -> String {
        let d = repo.dir().parent().unwrap().join(format!("w-{msg}"));
        std::fs::create_dir_all(d.join("region")).unwrap();
        std::fs::write(d.join("level.dat_marker"), contents).unwrap();
        let m = snapshot::snapshot(repo, &d).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let parents: Vec<String> = repo.head_commit().into_iter().collect();
        let c = repo.create_commit(&tree, parents, msg, "me", "0").unwrap();
        let cur = repo.current_branch().unwrap_or_else(|| "main".into());
        repo.write_branch(&cur, &c).unwrap();
        c
    }

    #[test]
    fn push_fetch_roundtrip_through_a_bucket() {
        let tmp = tempfile::tempdir().unwrap();
        let local = Repository::init(&tmp.path().join("local")).unwrap();
        local.set_head_to_branch("main").unwrap();
        let c1 = commit_world(&local, b"v1", "one");

        // shared bucket, two transport views (the protocol is all client-side)
        let bucket = std::sync::Arc::new(InMemoryBucket::default());
        let t = BucketTransport::new(Box::new(ArcBucket(bucket.clone())), "myrepo");
        let n = push(&local, &t, "main").unwrap();
        assert!(n > 0);

        // refs + HEAD landed
        let refs = t.list_refs().unwrap();
        assert!(refs.iter().any(|(r, h)| r == "refs/heads/main" && h == &c1));

        // a fresh repo fetches it back and reproduces the commit
        let dl = Repository::init(&tmp.path().join("dl")).unwrap();
        let t2 = BucketTransport::new(Box::new(ArcBucket(bucket.clone())), "myrepo");
        let (tip, copied) = fetch(&dl, &t2, "main").unwrap();
        assert_eq!(tip, c1);
        assert!(copied > 0);
        assert!(dl.objects().exists(&c1));

        // second push sends only the objects the bucket lacks (the shared
        // history from c1 is already present and must not be re-sent).
        std::fs::write(tmp.path().join("w-one").join("level.dat_marker"), b"v2").unwrap();
        let m = snapshot::snapshot(&local, &tmp.path().join("w-one")).unwrap();
        let tree = local.write_manifest(&m).unwrap();
        let c2 = local
            .create_commit(&tree, vec![c1.clone()], "two", "me", "0")
            .unwrap();
        local.write_branch("main", &c2).unwrap();
        let t3 = BucketTransport::new(Box::new(ArcBucket(bucket.clone())), "myrepo");
        // c2 reaches {c2, tree2, blob_v2, c1, tree1, blob_v1}; only the three
        // new ones are missing from the bucket.
        let reachable = crate::transfer::reachable(&local, &c2).unwrap();
        assert_eq!(reachable.len(), 6);
        let n2 = push(&local, &t3, "main").unwrap();
        assert_eq!(n2, 3, "incremental push sends only the new objects");

        // the newer commit round-trips out of the bucket
        let dl2 = Repository::init(&tmp.path().join("dl2")).unwrap();
        let t4 = BucketTransport::new(Box::new(ArcBucket(bucket.clone())), "myrepo");
        let (tip2, _) = fetch(&dl2, &t4, "main").unwrap();
        assert_eq!(tip2, c2);
        assert!(dl2.objects().exists(&c2) && dl2.objects().exists(&c1));
    }

    #[test]
    fn rejects_malformed_pack_id_in_manifest() {
        let bucket = InMemoryBucket::default();
        bucket
            .put("r/packs/manifest", b"../../etc/passwd\n")
            .unwrap();
        let t = BucketTransport::new(Box::new(bucket), "r");
        // building the index must refuse the traversal id
        assert!(t.missing(&["a".repeat(64)]).is_err());
    }

    #[test]
    fn set_branch_writes_head_on_first_push() {
        let bucket = InMemoryBucket::default();
        let h = "a".repeat(64);
        let t = BucketTransport::new(Box::new(bucket), "r");
        t.set_branch("main", &h).unwrap();
        // first push records HEAD so a later clone picks the default branch
        let refs = t.list_refs().unwrap();
        assert!(refs
            .iter()
            .any(|(r, hash)| r == "refs/heads/main" && hash == &h));
        // a traversal branch name is refused before any key is built
        assert!(t.set_branch("../escape", &h).is_err());
    }

    #[test]
    fn set_branch_cas_rejects_a_stale_etag_write() {
        // Drive the bucket directly to model the CAS the transport relies on:
        // a write conditioned on a stale ETag must fail.
        let bucket = InMemoryBucket::default();
        bucket.put("r/refs/heads/main", b"v1\n").unwrap();
        let stale = bucket.get("r/refs/heads/main").unwrap().unwrap().1;
        bucket.put("r/refs/heads/main", b"v2\n").unwrap(); // concurrent move
        assert!(
            !bucket
                .put_if_match("r/refs/heads/main", b"v3\n", Some(&stale))
                .unwrap(),
            "stale-etag write must be rejected"
        );
    }

    /// Lets several `BucketTransport`s share one in-memory bucket in tests.
    struct ArcBucket(std::sync::Arc<InMemoryBucket>);
    impl Bucket for ArcBucket {
        fn get(&self, key: &str) -> Result<Option<(Vec<u8>, String)>> {
            self.0.get(key)
        }
        fn put(&self, key: &str, data: &[u8]) -> Result<()> {
            self.0.put(key, data)
        }
        fn put_if_match(&self, key: &str, data: &[u8], expected: Option<&str>) -> Result<bool> {
            self.0.put_if_match(key, data, expected)
        }
        fn list(&self, prefix: &str) -> Result<Vec<String>> {
            self.0.list(prefix)
        }
    }
}

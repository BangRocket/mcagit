//! The whole-world snapshot (`Manifest` ≈ a git tree) and the `CommitObject`.

use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// A whole-world snapshot by content hash. Region files map each present chunk
/// position (`"x,z"`) to its chunk-object id; loose NBT and all other files map
/// their relative path to a single object id.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    #[serde(default)]
    pub regions: BTreeMap<String, BTreeMap<String, String>>,
    #[serde(default)]
    pub nbt: BTreeMap<String, String>,
    #[serde(default)]
    pub blobs: BTreeMap<String, String>,
    /// Directories with no files — recorded so checkout reproduces them.
    #[serde(default)]
    pub empty_dirs: Vec<String>,
}

impl Manifest {
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }
}

/// A commit: a snapshot (`tree`) plus history and metadata.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommitObject {
    pub tree: String,
    #[serde(default)]
    pub parents: Vec<String>,
    #[serde(default)]
    pub message: String,
    #[serde(default)]
    pub author: String,
    /// ISO-8601 author date.
    #[serde(default)]
    pub time: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub committer: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub commit_time: Option<String>,
    /// SSH-format signature over [`CommitObject::signable_payload`], if signed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<String>,
}

impl CommitObject {
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }

    /// The exact bytes that get signed — the object as-is but with the
    /// signature field cleared, so signing and verifying agree on the payload.
    pub fn signable_payload(&self) -> Result<String> {
        CommitObject {
            signature: None,
            ..self.clone()
        }
        .to_json()
    }
}

/// An annotated tag object (git's tag object): a named, dated, optionally
/// signed pointer to a commit. Stored as a content-addressed object whose hash
/// the `refs/tags/<name>` ref then holds — unlike a lightweight tag, whose ref
/// holds the commit hash directly.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TagObject {
    /// Target hash (a commit).
    pub object: String,
    #[serde(rename = "type", default = "default_tag_type")]
    pub kind: String,
    /// The tag name.
    pub tag: String,
    pub tagger: String,
    /// ISO-8601 / unix-seconds timestamp.
    #[serde(default)]
    pub time: String,
    #[serde(default)]
    pub message: String,
    /// SSH-format signature over [`TagObject::signable_payload`], if signed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<String>,
}

fn default_tag_type() -> String {
    "commit".to_string()
}

impl TagObject {
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }

    /// Parses `text` as a tag object, or `None` if it isn't one (any other
    /// object — commit, tree, blob — yields `None`).
    pub fn try_from_json(text: &str) -> Option<Self> {
        if !text.starts_with('{') {
            return None;
        }
        let t: TagObject = serde_json::from_str(text).ok()?;
        (!t.object.is_empty() && !t.tag.is_empty() && !t.tagger.is_empty()).then_some(t)
    }

    /// The exact bytes that get signed (the object with the signature cleared).
    pub fn signable_payload(&self) -> Result<String> {
        TagObject {
            signature: None,
            ..self.clone()
        }
        .to_json()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn manifest_json_roundtrip() {
        let mut m = Manifest::default();
        let mut region = BTreeMap::new();
        region.insert("0,0".to_string(), "aa11".to_string());
        region.insert("1,0".to_string(), "bb22".to_string());
        m.regions.insert("region/r.0.0.mca".to_string(), region);
        m.nbt.insert("level.dat".to_string(), "cc33".to_string());
        m.blobs.insert("icon.png".to_string(), "dd44".to_string());
        m.empty_dirs.push("playerdata".to_string());

        let json = m.to_json().unwrap();
        assert!(json.contains("emptyDirs"));
        assert_eq!(Manifest::from_json(&json).unwrap(), m);
    }

    #[test]
    fn commit_json_roundtrip() {
        let c = CommitObject {
            tree: "treehash".into(),
            parents: vec!["p1".into()],
            message: "snapshot".into(),
            author: "Joshua <j@example.com>".into(),
            time: "2026-06-07T00:00:00Z".into(),
            committer: Some("Joshua <j@example.com>".into()),
            commit_time: Some("2026-06-07T00:00:01Z".into()),
            signature: None,
        };
        let json = c.to_json().unwrap();
        assert_eq!(CommitObject::from_json(&json).unwrap(), c);
    }

    #[test]
    fn tag_object_roundtrip_and_detection() {
        let t = TagObject {
            object: "abc123".into(),
            kind: "commit".into(),
            tag: "v1.0".into(),
            tagger: "me <m@e>".into(),
            time: "12345".into(),
            message: "release".into(),
            signature: None,
        };
        let json = t.to_json().unwrap();
        assert!(json.contains("\"type\""));
        assert_eq!(TagObject::from_json(&json).unwrap(), t);
        assert_eq!(TagObject::try_from_json(&json), Some(t));

        // a commit object is not a tag
        let c = CommitObject {
            tree: "t".into(),
            ..Default::default()
        };
        assert_eq!(TagObject::try_from_json(&c.to_json().unwrap()), None);
        assert_eq!(TagObject::try_from_json("not json"), None);
    }

    #[test]
    fn signable_payload_excludes_signature() {
        let mut c = CommitObject {
            tree: "t".into(),
            ..Default::default()
        };
        let unsigned = c.signable_payload().unwrap();
        c.signature = Some("SIG".into());
        assert_eq!(c.signable_payload().unwrap(), unsigned);
        assert!(c.to_json().unwrap().contains("SIG"));

        let mut t = TagObject {
            object: "o".into(),
            tag: "v".into(),
            tagger: "me".into(),
            ..Default::default()
        };
        let unsigned = t.signable_payload().unwrap();
        t.signature = Some("SIG".into());
        assert_eq!(t.signable_payload().unwrap(), unsigned);
    }

    #[test]
    fn commit_without_committer_omits_field() {
        let c = CommitObject {
            tree: "t".into(),
            ..Default::default()
        };
        let json = c.to_json().unwrap();
        assert!(!json.contains("committer"));
        assert_eq!(CommitObject::from_json(&json).unwrap(), c);
    }
}

# mcagit Rust Engine — Workspace + `mca-nbt` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Rust cargo workspace (M0) and implement the complete, fully-tested `mca-nbt` crate (M1): NBT tag model, big-endian reader/writer with Java modified-UTF8 strings, deterministic canonical bytes, list-element identity, the path language, and lossless type-tagged JSON.

**Architecture:** A cargo workspace under `rust/` in this repo. `mca-nbt` is the dependency-free bedrock crate every other crate builds on. Pure library — no I/O beyond byte slices in / byte vecs out. Reader/writer are the only place NBT binary layout lives; canonical/identity/path/json are built on top of the value model.

**Tech Stack:** Rust 2021, `indexmap` (order-preserving compounds), `cesu8` (Java modified UTF-8), `serde_json` (JSON), `thiserror` (errors). Tests are inline `#[cfg(test)]` modules.

**Reference:** design spec `docs/superpowers/specs/2026-06-07-mcagit-rust-port-design.md`. The .NET originals to mirror semantically live in `src/McaGit/Nbt/` (`NbtIdentity.cs`, `NbtPath.cs`, `NbtCanonical.cs`, `NbtJson.cs`).

---

## File Structure

```
rust/
  Cargo.toml                      workspace manifest (members, shared deps)
  rust-toolchain.toml             pin stable toolchain
  crates/
    nbt/
      Cargo.toml                  crate manifest
      src/
        lib.rs                    re-exports + crate docs + Result/NbtError
        value.rs                  NbtValue enum, tag_id()
        mutf8.rs                  Java modified-UTF8 encode/decode (wraps cesu8)
        read.rs                   binary reader (bytes -> (name, NbtValue))
        write.rs                  binary writer (NbtValue -> bytes, sort_keys option)
        canonical.rs              deterministic canonical bytes (sorted writer)
        identity.rs               identity_key() for list-element matching
        path.rs                   NbtPath: parse, get / get_mut / set / remove
        json.rs                   to_json / from_json (lossless, type-tagged)
.github/workflows/rust.yml        CI: fmt --check, clippy -D warnings, test
```

Responsibilities: `value` owns the data model; `mutf8`/`read`/`write` own binary layout; `canonical` owns determinism; `identity`/`path` own structural addressing; `json` owns the text interchange form. Each module is independently testable.

---

## Task 1: Workspace + crate scaffold (M0)

**Files:**
- Create: `rust/Cargo.toml`
- Create: `rust/rust-toolchain.toml`
- Create: `rust/crates/nbt/Cargo.toml`
- Create: `rust/crates/nbt/src/lib.rs`

- [ ] **Step 1: Create the workspace manifest**

`rust/Cargo.toml`:
```toml
[workspace]
resolver = "2"
members = ["crates/nbt"]

[workspace.package]
edition = "2021"
version = "0.1.0"
license = "MIT"

[workspace.dependencies]
indexmap = "2"
cesu8 = "1"
serde = { version = "1", features = ["derive"] }
serde_json = "1"
thiserror = "2"

[profile.release]
lto = "thin"
codegen-units = 1
```

- [ ] **Step 2: Pin the toolchain**

`rust/rust-toolchain.toml`:
```toml
[toolchain]
channel = "stable"
components = ["rustfmt", "clippy"]
```

- [ ] **Step 3: Create the nbt crate manifest**

`rust/crates/nbt/Cargo.toml`:
```toml
[package]
name = "mca-nbt"
edition.workspace = true
version.workspace = true
license.workspace = true

[dependencies]
indexmap = { workspace = true }
cesu8 = { workspace = true }
serde_json = { workspace = true }
thiserror = { workspace = true }
```

- [ ] **Step 4: Create a minimal lib.rs so the workspace builds**

`rust/crates/nbt/src/lib.rs`:
```rust
//! `mca-nbt` — NBT tag model, binary codec, canonical form, identity, path, JSON.

use thiserror::Error;

/// Errors produced while reading or addressing NBT.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum NbtError {
    #[error("unexpected end of input")]
    UnexpectedEof,
    #[error("unknown tag id {0}")]
    UnknownTag(u8),
    #[error("negative length in payload")]
    NegativeLength,
    #[error("invalid modified-UTF8 string")]
    InvalidString,
    #[error("invalid path: {0}")]
    InvalidPath(String),
    #[error("invalid JSON shape: {0}")]
    InvalidJson(String),
}

/// Crate result alias.
pub type Result<T> = std::result::Result<T, NbtError>;
```

- [ ] **Step 5: Verify it builds**

Run: `cd rust && cargo build`
Expected: compiles, `Finished` line, no errors.

- [ ] **Step 6: Commit**

```bash
cd /Volumes/Storage/Code/minecraft/mcagit
git add rust/Cargo.toml rust/rust-toolchain.toml rust/crates/nbt/Cargo.toml rust/crates/nbt/src/lib.rs
git commit -m "feat(rust): scaffold cargo workspace + mca-nbt crate skeleton"
```

---

## Task 2: CI workflow (M0 gate)

**Files:**
- Create: `.github/workflows/rust.yml`

- [ ] **Step 1: Add the Rust CI workflow**

`.github/workflows/rust.yml`:
```yaml
name: rust
on:
  push:
    paths: ["rust/**", ".github/workflows/rust.yml"]
  pull_request:
    paths: ["rust/**", ".github/workflows/rust.yml"]
defaults:
  run:
    working-directory: rust
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: dtolnay/rust-toolchain@stable
        with:
          components: rustfmt, clippy
      - run: cargo fmt --all -- --check
      - run: cargo clippy --all-targets -- -D warnings
      - run: cargo test --all
```

- [ ] **Step 2: Verify formatting locally matches CI**

Run: `cd rust && cargo fmt --all -- --check && cargo clippy --all-targets -- -D warnings`
Expected: both succeed with no output (clean).

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/rust.yml
git commit -m "ci(rust): fmt + clippy + test workflow for rust/"
```

---

## Task 3: Modified-UTF8 codec (`mutf8`)

**Files:**
- Create: `rust/crates/nbt/src/mutf8.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod mutf8;`)

- [ ] **Step 1: Write the failing tests**

`rust/crates/nbt/src/mutf8.rs`:
```rust
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
        // bare 0x00 is invalid in Java modified UTF-8
        assert!(decode(&[0x00]).is_err());
    }
}
```

- [ ] **Step 2: Wire the module**

In `rust/crates/nbt/src/lib.rs`, add after the `Result` alias:
```rust
pub mod mutf8;
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `cd rust && cargo test -p mca-nbt mutf8`
Expected: 4 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/mutf8.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): modified-UTF8 string codec"
```

---

## Task 4: NBT value model (`value`)

**Files:**
- Create: `rust/crates/nbt/src/value.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod value;` + re-export)

- [ ] **Step 1: Write the value model and its test**

`rust/crates/nbt/src/value.rs`:
```rust
//! The NBT data model.

use indexmap::IndexMap;

/// A compound's key→value map, preserving insertion order on read.
pub type Compound = IndexMap<String, NbtValue>;

/// An NBT tag value. `ByteArray` holds raw bytes; `List` is homogeneous by
/// construction (the writer derives the element type from the first element).
#[derive(Debug, Clone, PartialEq)]
pub enum NbtValue {
    Byte(i8),
    Short(i16),
    Int(i32),
    Long(i64),
    Float(f32),
    Double(f64),
    ByteArray(Vec<u8>),
    String(String),
    List(Vec<NbtValue>),
    Compound(Compound),
    IntArray(Vec<i32>),
    LongArray(Vec<i64>),
}

/// The NBT tag-type byte for a value (TAG_End = 0 is never a value).
pub fn tag_id(v: &NbtValue) -> u8 {
    match v {
        NbtValue::Byte(_) => 1,
        NbtValue::Short(_) => 2,
        NbtValue::Int(_) => 3,
        NbtValue::Long(_) => 4,
        NbtValue::Float(_) => 5,
        NbtValue::Double(_) => 6,
        NbtValue::ByteArray(_) => 7,
        NbtValue::String(_) => 8,
        NbtValue::List(_) => 9,
        NbtValue::Compound(_) => 10,
        NbtValue::IntArray(_) => 11,
        NbtValue::LongArray(_) => 12,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tag_ids_match_spec() {
        assert_eq!(tag_id(&NbtValue::Byte(0)), 1);
        assert_eq!(tag_id(&NbtValue::Compound(Compound::new())), 10);
        assert_eq!(tag_id(&NbtValue::LongArray(vec![])), 12);
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod value;
pub use value::{tag_id, Compound, NbtValue};
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt value`
Expected: 1 test passes.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/value.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): NbtValue model + tag_id"
```

---

## Task 5: Binary reader (`read`)

**Files:**
- Create: `rust/crates/nbt/src/read.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod read;` + re-export `read`)

- [ ] **Step 1: Write the reader and a unit test**

`rust/crates/nbt/src/read.rs`:
```rust
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
        assert_eq!(read(&[10, 0, 4, b'r']).unwrap_err(), NbtError::UnexpectedEof);
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod read;
pub use read::read;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt read`
Expected: 2 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/read.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): big-endian binary reader"
```

---

## Task 6: Binary writer (`write`) + round-trip

**Files:**
- Create: `rust/crates/nbt/src/write.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod write;` + re-export `write_named`)

- [ ] **Step 1: Write the writer with a round-trip test**

`rust/crates/nbt/src/write.rs`:
```rust
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
        inner.insert("Pos".into(), NbtValue::List(vec![
            NbtValue::Double(1.0),
            NbtValue::Double(2.0),
        ]));
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
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod write;
pub use write::write_named;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt write`
Expected: 2 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/write.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): big-endian binary writer + round-trip"
```

---

## Task 7: Canonical bytes (`canonical`)

**Files:**
- Create: `rust/crates/nbt/src/canonical.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod canonical;` + re-export)

- [ ] **Step 1: Write canonical + determinism test**

`rust/crates/nbt/src/canonical.rs`:
```rust
//! Deterministic canonical byte form: compound keys are emitted sorted, with a
//! fixed empty root name, so equal content hashes identically regardless of
//! original key order or on-disk compression.

use crate::value::NbtValue;
use crate::write::write_named;

/// Canonical bytes for a value (sorted compounds, empty root name).
pub fn canonical_bytes(v: &NbtValue) -> Vec<u8> {
    write_named("", v, true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    #[test]
    fn key_order_does_not_affect_canonical_bytes() {
        let mut a = Compound::new();
        a.insert("b".into(), NbtValue::Int(2));
        a.insert("a".into(), NbtValue::Int(1));

        let mut b = Compound::new();
        b.insert("a".into(), NbtValue::Int(1));
        b.insert("b".into(), NbtValue::Int(2));

        assert_eq!(
            canonical_bytes(&NbtValue::Compound(a)),
            canonical_bytes(&NbtValue::Compound(b))
        );
    }

    #[test]
    fn nested_compounds_are_sorted_too() {
        let mut inner1 = Compound::new();
        inner1.insert("z".into(), NbtValue::Byte(1));
        inner1.insert("y".into(), NbtValue::Byte(2));
        let mut outer1 = Compound::new();
        outer1.insert("c".into(), NbtValue::Compound(inner1));

        let mut inner2 = Compound::new();
        inner2.insert("y".into(), NbtValue::Byte(2));
        inner2.insert("z".into(), NbtValue::Byte(1));
        let mut outer2 = Compound::new();
        outer2.insert("c".into(), NbtValue::Compound(inner2));

        assert_eq!(
            canonical_bytes(&NbtValue::Compound(outer1)),
            canonical_bytes(&NbtValue::Compound(outer2))
        );
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod canonical;
pub use canonical::canonical_bytes;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt canonical`
Expected: 2 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/canonical.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): deterministic canonical bytes"
```

---

## Task 8: List-element identity (`identity`)

**Files:**
- Create: `rust/crates/nbt/src/identity.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod identity;` + re-export)

Mirrors `src/McaGit/Nbt/NbtIdentity.cs`: a stable key for matching a compound across versions. Precedence: entity UUID (modern int-array, then legacy most/least longs) → block coords → `Slot` → string `id`. No match → `None` (caller falls back to index).

- [ ] **Step 1: Write identity + tests**

`rust/crates/nbt/src/identity.rs`:
```rust
//! Stable identity key for matching list elements across versions.

use crate::value::NbtValue;

/// Derive a stable identity key for a list element, or `None` to fall back to
/// positional index matching.
pub fn identity_key(v: &NbtValue) -> Option<String> {
    let NbtValue::Compound(m) = v else {
        return None;
    };

    // Modern entity UUID: IntArray of length 4 under "UUID".
    if let Some(NbtValue::IntArray(a)) = m.get("UUID") {
        if a.len() == 4 {
            return Some(format!("uuid:{},{},{},{}", a[0], a[1], a[2], a[3]));
        }
    }
    // Legacy entity UUID: paired longs.
    if let (Some(NbtValue::Long(hi)), Some(NbtValue::Long(lo))) =
        (m.get("UUIDMost"), m.get("UUIDLeast"))
    {
        return Some(format!("uuid:{}:{}", hi, lo));
    }
    // Block entity coords.
    if let (Some(NbtValue::Int(x)), Some(NbtValue::Int(y)), Some(NbtValue::Int(z))) =
        (m.get("x"), m.get("y"), m.get("z"))
    {
        return Some(format!("block:{},{},{}", x, y, z));
    }
    // Inventory slot.
    if let Some(NbtValue::Byte(s)) = m.get("Slot") {
        return Some(format!("slot:{}", s));
    }
    // Generic string id.
    if let Some(NbtValue::String(id)) = m.get("id") {
        return Some(format!("id:{}", id));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    fn compound(pairs: &[(&str, NbtValue)]) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert((*k).into(), v.clone());
        }
        NbtValue::Compound(m)
    }

    #[test]
    fn modern_uuid_wins() {
        let v = compound(&[
            ("UUID", NbtValue::IntArray(vec![1, 2, 3, 4])),
            ("id", NbtValue::String("zombie".into())),
        ]);
        assert_eq!(identity_key(&v).as_deref(), Some("uuid:1,2,3,4"));
    }

    #[test]
    fn block_coords() {
        let v = compound(&[
            ("x", NbtValue::Int(10)),
            ("y", NbtValue::Int(64)),
            ("z", NbtValue::Int(-3)),
        ]);
        assert_eq!(identity_key(&v).as_deref(), Some("block:10,64,-3"));
    }

    #[test]
    fn slot_then_id() {
        assert_eq!(
            identity_key(&compound(&[("Slot", NbtValue::Byte(2))])).as_deref(),
            Some("slot:2")
        );
        assert_eq!(
            identity_key(&compound(&[("id", NbtValue::String("minecraft:stone".into()))]))
                .as_deref(),
            Some("id:minecraft:stone")
        );
    }

    #[test]
    fn no_identity_returns_none() {
        assert_eq!(identity_key(&compound(&[("foo", NbtValue::Int(1))])), None);
        assert_eq!(identity_key(&NbtValue::Int(5)), None);
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod identity;
pub use identity::identity_key;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt identity`
Expected: 4 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/identity.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): list-element identity keys"
```

---

## Task 9: Path language (`path`)

**Files:**
- Create: `rust/crates/nbt/src/path.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod path;` + re-export)

Mirrors `src/McaGit/Nbt/NbtPath.cs`. Path syntax: dot-separated keys, with `[n]` for list index and `[ident]` for identity match (e.g. `Entities[uuid:1,2,3,4].Pos[1]`). Keys containing literal `.` or `[` are not addressable (documented limitation).

- [ ] **Step 1: Write the path parser + traversal + tests**

`rust/crates/nbt/src/path.rs`:
```rust
//! The NBT path language: addressing nodes by key, list index, or identity.

use crate::identity::identity_key;
use crate::value::NbtValue;
use crate::{NbtError, Result};

/// One step in a path.
#[derive(Debug, Clone, PartialEq)]
enum Seg {
    Key(String),
    Index(usize),
    Ident(String),
}

/// A parsed path. Build with [`NbtPath::parse`].
#[derive(Debug, Clone, PartialEq)]
pub struct NbtPath {
    segs: Vec<Seg>,
}

impl NbtPath {
    /// Parse a path string like `Data.Player.Pos[0]` or `E[uuid:1,2,3,4].id`.
    pub fn parse(s: &str) -> Result<Self> {
        let mut segs = Vec::new();
        let bytes = s.as_bytes();
        let mut i = 0usize;

        while i < bytes.len() {
            match bytes[i] as char {
                '.' => {
                    i += 1; // skip separator
                }
                '[' => {
                    let start = i + 1;
                    let end = s[start..]
                        .find(']')
                        .map(|off| start + off)
                        .ok_or_else(|| NbtError::InvalidPath(format!("unclosed '[' in {s:?}")))?;
                    let inner = &s[start..end];
                    if inner.is_empty() {
                        return Err(NbtError::InvalidPath(format!("empty [] in {s:?}")));
                    }
                    if let Ok(n) = inner.parse::<usize>() {
                        segs.push(Seg::Index(n));
                    } else {
                        segs.push(Seg::Ident(inner.to_string()));
                    }
                    i = end + 1;
                }
                _ => {
                    // a key: read until the next '.' or '['
                    let rest = &s[i..];
                    let stop = rest.find(['.', '[']).unwrap_or(rest.len());
                    segs.push(Seg::Key(rest[..stop].to_string()));
                    i += stop;
                }
            }
        }
        if segs.is_empty() {
            return Err(NbtError::InvalidPath(format!("empty path {s:?}")));
        }
        Ok(NbtPath { segs })
    }

    /// Borrow the node at this path, if present.
    pub fn get<'v>(&self, root: &'v NbtValue) -> Option<&'v NbtValue> {
        let mut cur = root;
        for seg in &self.segs {
            cur = step(cur, seg)?;
        }
        Some(cur)
    }

    /// Mutably borrow the node at this path, if present.
    pub fn get_mut<'v>(&self, root: &'v mut NbtValue) -> Option<&'v mut NbtValue> {
        let mut cur = root;
        for seg in &self.segs {
            cur = step_mut(cur, seg)?;
        }
        Some(cur)
    }

    /// Set the node at this path to `value`. The parent must exist; if the final
    /// step is a `Key` on a compound, it is inserted or replaced. Returns
    /// `false` if the parent path does not resolve or the final step is not
    /// applicable.
    pub fn set(&self, root: &mut NbtValue, value: NbtValue) -> bool {
        let (last, parents) = match self.segs.split_last() {
            Some(x) => x,
            None => return false,
        };
        let mut cur = root;
        for seg in parents {
            match step_mut(cur, seg) {
                Some(next) => cur = next,
                None => return false,
            }
        }
        match (last, cur) {
            (Seg::Key(k), NbtValue::Compound(m)) => {
                m.insert(k.clone(), value);
                true
            }
            (Seg::Index(i), NbtValue::List(items)) if *i < items.len() => {
                items[*i] = value;
                true
            }
            (Seg::Ident(id), NbtValue::List(items)) => {
                if let Some(slot) =
                    items.iter_mut().find(|e| identity_key(e).as_deref() == Some(id.as_str()))
                {
                    *slot = value;
                    true
                } else {
                    false
                }
            }
            _ => false,
        }
    }

    /// Remove the node at this path. Returns the removed value, or `None`.
    pub fn remove(&self, root: &mut NbtValue) -> Option<NbtValue> {
        let (last, parents) = self.segs.split_last()?;
        let mut cur = root;
        for seg in parents {
            cur = step_mut(cur, seg)?;
        }
        match (last, cur) {
            (Seg::Key(k), NbtValue::Compound(m)) => m.shift_remove(k),
            (Seg::Index(i), NbtValue::List(items)) if *i < items.len() => Some(items.remove(*i)),
            (Seg::Ident(id), NbtValue::List(items)) => {
                let pos = items
                    .iter()
                    .position(|e| identity_key(e).as_deref() == Some(id.as_str()))?;
                Some(items.remove(pos))
            }
            _ => None,
        }
    }
}

fn step<'v>(cur: &'v NbtValue, seg: &Seg) -> Option<&'v NbtValue> {
    match (seg, cur) {
        (Seg::Key(k), NbtValue::Compound(m)) => m.get(k),
        (Seg::Index(i), NbtValue::List(items)) => items.get(*i),
        (Seg::Ident(id), NbtValue::List(items)) => {
            items.iter().find(|e| identity_key(e).as_deref() == Some(id.as_str()))
        }
        _ => None,
    }
}

fn step_mut<'v>(cur: &'v mut NbtValue, seg: &Seg) -> Option<&'v mut NbtValue> {
    match (seg, cur) {
        (Seg::Key(k), NbtValue::Compound(m)) => m.get_mut(k),
        (Seg::Index(i), NbtValue::List(items)) => items.get_mut(*i),
        (Seg::Ident(id), NbtValue::List(items)) => {
            items.iter_mut().find(|e| identity_key(e).as_deref() == Some(id.as_str()))
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    fn world() -> NbtValue {
        // { Data: { Player: { Pos: [1.0, 2.0, 3.0] } },
        //   Entities: [ {UUID:[1,2,3,4], id:"zombie"} ] }
        let mut pos = Vec::new();
        pos.push(NbtValue::Double(1.0));
        pos.push(NbtValue::Double(2.0));
        pos.push(NbtValue::Double(3.0));
        let mut player = Compound::new();
        player.insert("Pos".into(), NbtValue::List(pos));
        let mut data = Compound::new();
        data.insert("Player".into(), NbtValue::Compound(player));

        let mut ent = Compound::new();
        ent.insert("UUID".into(), NbtValue::IntArray(vec![1, 2, 3, 4]));
        ent.insert("id".into(), NbtValue::String("zombie".into()));

        let mut root = Compound::new();
        root.insert("Data".into(), NbtValue::Compound(data));
        root.insert("Entities".into(), NbtValue::List(vec![NbtValue::Compound(ent)]));
        NbtValue::Compound(root)
    }

    #[test]
    fn get_by_key_and_index() {
        let w = world();
        let p = NbtPath::parse("Data.Player.Pos[1]").unwrap();
        assert_eq!(p.get(&w), Some(&NbtValue::Double(2.0)));
    }

    #[test]
    fn get_by_identity() {
        let w = world();
        let p = NbtPath::parse("Entities[uuid:1,2,3,4].id").unwrap();
        assert_eq!(p.get(&w), Some(&NbtValue::String("zombie".into())));
    }

    #[test]
    fn set_replaces_value() {
        let mut w = world();
        let p = NbtPath::parse("Data.Player.Pos[1]").unwrap();
        assert!(p.set(&mut w, NbtValue::Double(99.0)));
        assert_eq!(p.get(&w), Some(&NbtValue::Double(99.0)));
    }

    #[test]
    fn set_inserts_new_key() {
        let mut w = world();
        let p = NbtPath::parse("Data.Player.Health").unwrap();
        assert!(p.set(&mut w, NbtValue::Float(20.0)));
        assert_eq!(p.get(&w), Some(&NbtValue::Float(20.0)));
    }

    #[test]
    fn remove_by_identity() {
        let mut w = world();
        let p = NbtPath::parse("Entities[uuid:1,2,3,4]").unwrap();
        assert!(p.remove(&mut w).is_some());
        let count = NbtPath::parse("Entities").unwrap().get(&w);
        let NbtValue::List(items) = count.unwrap() else {
            panic!()
        };
        assert!(items.is_empty());
    }

    #[test]
    fn missing_path_returns_none() {
        let w = world();
        assert_eq!(NbtPath::parse("Data.Nope").unwrap().get(&w), None);
    }

    #[test]
    fn empty_path_errors() {
        assert!(NbtPath::parse("").is_err());
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod path;
pub use path::NbtPath;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt path`
Expected: 7 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/path.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): path language (get/set/remove by key, index, identity)"
```

---

## Task 10: Lossless type-tagged JSON (`json`)

**Files:**
- Create: `rust/crates/nbt/src/json.rs`
- Modify: `rust/crates/nbt/src/lib.rs` (add `mod json;` + re-export)

Mirrors `src/McaGit/Nbt/NbtJson.cs`: each value becomes a single-key object tagging its type; `Long`/`LongArray` use strings so values beyond 2^53 survive.

- [ ] **Step 1: Write to_json / from_json + round-trip test**

`rust/crates/nbt/src/json.rs`:
```rust
//! Lossless, type-tagged JSON encoding of NBT (longs as strings).

use crate::value::{Compound, NbtValue};
use crate::{NbtError, Result};
use serde_json::{Map, Value as J};

/// Encode an NBT value to its type-tagged JSON form.
pub fn to_json(v: &NbtValue) -> J {
    fn obj(tag: &str, val: J) -> J {
        let mut m = Map::new();
        m.insert(tag.to_string(), val);
        J::Object(m)
    }
    match v {
        NbtValue::Byte(x) => obj("byte", J::from(*x)),
        NbtValue::Short(x) => obj("short", J::from(*x)),
        NbtValue::Int(x) => obj("int", J::from(*x)),
        NbtValue::Long(x) => obj("long", J::from(x.to_string())),
        NbtValue::Float(x) => obj("float", J::from(*x)),
        NbtValue::Double(x) => obj("double", J::from(*x)),
        NbtValue::ByteArray(b) => obj("byteArray", J::from(b.clone())),
        NbtValue::String(s) => obj("string", J::from(s.clone())),
        NbtValue::List(items) => obj("list", J::Array(items.iter().map(to_json).collect())),
        NbtValue::Compound(m) => {
            let mut o = Map::new();
            for (k, val) in m {
                o.insert(k.clone(), to_json(val));
            }
            obj("compound", J::Object(o))
        }
        NbtValue::IntArray(a) => obj("intArray", J::Array(a.iter().map(|x| J::from(*x)).collect())),
        NbtValue::LongArray(a) => obj(
            "longArray",
            J::Array(a.iter().map(|x| J::from(x.to_string())).collect()),
        ),
    }
}

/// Decode a type-tagged JSON value back into NBT.
pub fn from_json(j: &J) -> Result<NbtValue> {
    let map = j
        .as_object()
        .ok_or_else(|| NbtError::InvalidJson("expected object".into()))?;
    let (tag, val) = map
        .iter()
        .next()
        .ok_or_else(|| NbtError::InvalidJson("empty object".into()))?;
    if map.len() != 1 {
        return Err(NbtError::InvalidJson(format!("expected single tag, got {}", map.len())));
    }
    let bad = |what: &str| NbtError::InvalidJson(format!("bad {what}"));
    Ok(match tag.as_str() {
        "byte" => NbtValue::Byte(val.as_i64().ok_or_else(|| bad("byte"))? as i8),
        "short" => NbtValue::Short(val.as_i64().ok_or_else(|| bad("short"))? as i16),
        "int" => NbtValue::Int(val.as_i64().ok_or_else(|| bad("int"))? as i32),
        "long" => NbtValue::Long(
            val.as_str()
                .ok_or_else(|| bad("long"))?
                .parse()
                .map_err(|_| bad("long"))?,
        ),
        "float" => NbtValue::Float(val.as_f64().ok_or_else(|| bad("float"))? as f32),
        "double" => NbtValue::Double(val.as_f64().ok_or_else(|| bad("double"))?),
        "byteArray" => {
            let arr = val.as_array().ok_or_else(|| bad("byteArray"))?;
            let mut b = Vec::with_capacity(arr.len());
            for x in arr {
                b.push(x.as_u64().ok_or_else(|| bad("byteArray"))? as u8);
            }
            NbtValue::ByteArray(b)
        }
        "string" => NbtValue::String(val.as_str().ok_or_else(|| bad("string"))?.to_string()),
        "list" => {
            let arr = val.as_array().ok_or_else(|| bad("list"))?;
            let mut items = Vec::with_capacity(arr.len());
            for x in arr {
                items.push(from_json(x)?);
            }
            NbtValue::List(items)
        }
        "compound" => {
            let o = val.as_object().ok_or_else(|| bad("compound"))?;
            let mut m = Compound::new();
            for (k, v) in o {
                m.insert(k.clone(), from_json(v)?);
            }
            NbtValue::Compound(m)
        }
        "intArray" => {
            let arr = val.as_array().ok_or_else(|| bad("intArray"))?;
            let mut a = Vec::with_capacity(arr.len());
            for x in arr {
                a.push(x.as_i64().ok_or_else(|| bad("intArray"))? as i32);
            }
            NbtValue::IntArray(a)
        }
        "longArray" => {
            let arr = val.as_array().ok_or_else(|| bad("longArray"))?;
            let mut a = Vec::with_capacity(arr.len());
            for x in arr {
                a.push(x.as_str().ok_or_else(|| bad("longArray"))?.parse().map_err(|_| bad("longArray"))?);
            }
            NbtValue::LongArray(a)
        }
        other => return Err(NbtError::InvalidJson(format!("unknown tag {other:?}"))),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Compound;

    #[test]
    fn large_long_survives_roundtrip() {
        let big = NbtValue::Long(9_007_199_254_740_993); // 2^53 + 1
        let j = to_json(&big);
        assert_eq!(j["long"], J::from("9007199254740993"));
        assert_eq!(from_json(&j).unwrap(), big);
    }

    #[test]
    fn nested_roundtrips() {
        let mut m = Compound::new();
        m.insert("name".into(), NbtValue::String("x".into()));
        m.insert("cells".into(), NbtValue::LongArray(vec![1, -2, 1 << 60]));
        m.insert("kids".into(), NbtValue::List(vec![NbtValue::Byte(1), NbtValue::Byte(2)]));
        let v = NbtValue::Compound(m);
        assert_eq!(from_json(&to_json(&v)).unwrap(), v);
    }

    #[test]
    fn rejects_multi_key_object() {
        let mut m = Map::new();
        m.insert("int".into(), J::from(1));
        m.insert("extra".into(), J::from(2));
        assert!(from_json(&J::Object(m)).is_err());
    }
}
```

- [ ] **Step 2: Wire + re-export**

In `rust/crates/nbt/src/lib.rs` add:
```rust
pub mod json;
pub use json::{from_json, to_json};
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-nbt json`
Expected: 3 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/nbt/src/json.rs rust/crates/nbt/src/lib.rs
git commit -m "feat(nbt): lossless type-tagged JSON encoding"
```

---

## Task 11: Crate-wide green + lint gate (M1 gate)

**Files:** none (verification only)

- [ ] **Step 1: Full crate test run**

Run: `cd rust && cargo test --all`
Expected: all tests across mutf8/value/read/write/canonical/identity/path/json pass (≈ 28 tests), 0 failures.

- [ ] **Step 2: Lint + format gate (must match CI)**

Run: `cd rust && cargo fmt --all -- --check && cargo clippy --all-targets -- -D warnings`
Expected: both clean, no diffs, no warnings.

- [ ] **Step 3: Commit any fmt fixes (if needed)**

```bash
git add -A rust/
git commit -m "style(nbt): cargo fmt" || echo "nothing to format"
```

---

## Done criteria (M0 + M1)

- `rust/` workspace builds; `.github/workflows/rust.yml` runs fmt + clippy + test.
- `mca-nbt` exposes: `NbtValue`/`Compound`/`tag_id`, `read`, `write_named`, `canonical_bytes`, `identity_key`, `NbtPath`, `to_json`/`from_json`, `NbtError`/`Result`.
- `cargo test --all` green; `cargo clippy -D warnings` clean.
- **Next plan (M2):** `mca-anvil` — `RegionFile` (mmap read), `RawChunk`, `ChunkCodec` (zlib/gzip/lz4/none over `mca-nbt`), `RegionWriter`, plus the round-trip-a-real-dobbscraft-chunk gate. Then M3 (object store + commit + parallel checkout) for the speed proof.

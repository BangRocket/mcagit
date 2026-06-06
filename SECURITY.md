# Security policy

## Reporting a vulnerability

Please report security issues privately via [GitHub Security Advisories](https://github.com/BangRocket/mcagit/security/advisories/new) (or open a minimal issue asking for a private channel — do not include exploit details in a public issue). We aim to acknowledge within a few days.

## Threat model

mcagit is a local-first CLI. Its strongest guarantees hold when you run it on worlds and repositories you control. The notes below consolidate the trust assumptions so you can decide what is safe to expose.

### What is hardened

- **Path confinement.** Untrusted relative paths — manifest keys, patch paths, and ref names that can arrive over the network — are confined with `PathGuard.Confine(root, rel)` before any filesystem access, so a crafted name like `../../HEAD` cannot escape the intended directory (checkout, patch apply, and `refs/heads` / `refs/tags` / remote-tracking writes).
- **Object-id validation.** Object hashes must pass `ObjectStore.IsValidHash` (64 lowercase hex) before they are turned into a path; a malicious id cannot address an arbitrary file.
- **Hash verification on receive.** `ObjectStore.ImportRaw` re-hashes incoming objects, so a hostile remote cannot poison the store with mismatched content.
- **Resource caps.** Inbound sizes are bounded (object inflate, server request body, frame length, packfile index count) and NBT recursion is capped (`NbtCanonical.MaxDepth`) so a deeply nested or oversized payload cannot exhaust memory or stack.
- **Constant-time token compare.** Server auth tokens are compared with `CryptographicOperations.FixedTimeEquals`.
- **Non-destructive by construction.** `diff` / `extract` / `status` never modify a world; `apply` only writes a fresh output directory; only `checkout` / `reset --hard` / `merge` / `rebase` / `bisect` / `clean` / `stash` touch the bound worktree.
- **Inter-process lock.** `commit` and `push` take a repository lock so concurrent runs cannot race branch advancement.

### What you must account for

- **The built-in HTTP server has no TLS and a single shared push token.** Reads are anonymous by default; pushes require `--allow-push` plus the token. Put it behind a reverse proxy for `https`, and treat the push token as a shared secret. There is no per-user auth or read token yet.
- **`serve-stdio` trusts the SSH layer.** It runs over an authenticated SSH session and currently grants write to any caller that reaches it — confidentiality, integrity, and authorization are SSH's job (keys / `authorized_keys` restrictions). Do not expose it to principals you would not give push access.
- **Cloud bucket objects are not encrypted at rest by mcagit.** A world's NBT is recoverable by anyone who can read the bucket. Rely on the provider's server-side encryption and tight bucket ACLs, and scope credentials per backup. Client-side (record-level) encryption is future work — see [`docs/cloud-backend.md`](docs/cloud-backend.md).
- **`apply` and `checkout` write files derived from patch / snapshot content.** Path handling is confined (above), but only apply patches and check out repositories from sources you trust, as with `git`.
- **Signatures are SSH-key based, not GPG.** `commit -S` / `tag -s` sign with an SSH key; `tag -v` verifies against an allowed-signers file. There is no web-of-trust.

## Supported versions

mcagit is pre-1.0; security fixes land on `main` and ship in the next tagged release. Build self-contained binaries from a recent `main` for the latest fixes.

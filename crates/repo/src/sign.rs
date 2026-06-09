//! Signs and verifies commit/tag payloads with an SSH key, the way git does
//! with `gpg.format=ssh` — by shelling to `ssh-keygen -Y sign|verify`. No GPG
//! dependency; auth and key handling are ssh's. Signing is always optional: if
//! `ssh-keygen` or the key is missing, callers commit/tag unsigned.

use crate::{RepoError, Result};
use std::path::{Path, PathBuf};
use std::process::Command;

/// The ssh signature namespace mcagit signs under.
pub const NAMESPACE: &str = "mcagit";

/// Whether `ssh-keygen` is on PATH (signing/verifying needs it).
pub fn available() -> bool {
    which("ssh-keygen").is_some()
}

/// Sign `payload` with an SSH private key, returning the armored
/// `-----BEGIN SSH SIGNATURE-----` blob.
pub fn sign(payload: &str, key_file: &str) -> Result<String> {
    let key = expand_home(key_file)?;
    if !key.is_file() {
        return Err(RepoError::Other(format!(
            "signing key not found: {key_file} (set user.signingkey)"
        )));
    }
    if !available() {
        return Err(RepoError::Other(
            "ssh-keygen not found on PATH — cannot sign".into(),
        ));
    }
    let dir = tempfile::Builder::new().prefix("mcagit-sign").tempdir()?;
    let data = dir.path().join("payload");
    std::fs::write(&data, payload)?;
    let out = run(
        "ssh-keygen",
        &[
            "-Y".as_ref(),
            "sign".as_ref(),
            "-f".as_ref(),
            key.as_os_str(),
            "-n".as_ref(),
            NAMESPACE.as_ref(),
            data.as_os_str(),
        ],
        None,
    )?;
    let sig_file = dir.path().join("payload.sig");
    if out.code != 0 || !sig_file.is_file() {
        return Err(RepoError::Other(format!(
            "ssh-keygen sign failed: {}",
            out.stderr.trim()
        )));
    }
    Ok(std::fs::read_to_string(sig_file)?)
}

/// The outcome of verifying a signature.
#[derive(Debug)]
pub struct VerifyResult {
    /// The signature is cryptographically valid for its embedded key.
    pub valid: bool,
    /// The signer was matched against an allowed-signers file. Only this
    /// establishes trust — `valid` alone means any throwaway key.
    pub signer_verified: bool,
    /// The matched principal, when signer-verified.
    pub identity: Option<String>,
    pub detail: String,
}

/// Verify `signature` over `payload`. With an allowed-signers file (and a
/// matching principal) it confirms the signer; without one it falls back to
/// `check-novalidate` (valid signature, signer not established as trusted).
pub fn verify(payload: &str, signature: &str, allowed_signers: Option<&str>) -> VerifyResult {
    if signature.is_empty() {
        return failed("object is not signed");
    }
    if !available() {
        return failed("ssh-keygen not found on PATH — cannot verify");
    }
    let Ok(dir) = tempfile::Builder::new().prefix("mcagit-verify").tempdir() else {
        return failed("could not create temp dir");
    };
    let sig_file = dir.path().join("payload.sig");
    if std::fs::write(&sig_file, signature).is_err() {
        return failed("could not write signature file");
    }

    if let Some(signers) = allowed_signers {
        if let Ok(signers) = expand_home(signers) {
            if signers.is_file() {
                if let Some(principal) = find_principal(&sig_file, &signers) {
                    let out = run(
                        "ssh-keygen",
                        &[
                            "-Y".as_ref(),
                            "verify".as_ref(),
                            "-f".as_ref(),
                            signers.as_os_str(),
                            "-I".as_ref(),
                            principal.as_ref(),
                            "-n".as_ref(),
                            NAMESPACE.as_ref(),
                            "-s".as_ref(),
                            sig_file.as_os_str(),
                        ],
                        Some(payload),
                    );
                    return match out {
                        Ok(o) if o.code == 0 => VerifyResult {
                            valid: true,
                            signer_verified: true,
                            identity: Some(principal.clone()),
                            detail: format!("Good signature from {principal}"),
                        },
                        Ok(o) => VerifyResult {
                            valid: false,
                            signer_verified: false,
                            identity: Some(principal),
                            detail: o.stderr.trim().to_string(),
                        },
                        Err(e) => failed(&e.to_string()),
                    };
                }
            }
        }
    }

    match run(
        "ssh-keygen",
        &[
            "-Y".as_ref(),
            "check-novalidate".as_ref(),
            "-n".as_ref(),
            NAMESPACE.as_ref(),
            "-s".as_ref(),
            sig_file.as_os_str(),
        ],
        Some(payload),
    ) {
        Ok(o) if o.code == 0 => VerifyResult {
            valid: true,
            signer_verified: false,
            identity: None,
            detail: "Valid signature (signer not verified — set gpg.ssh.allowedSignersFile)".into(),
        },
        Ok(o) => failed(o.stderr.trim()),
        Err(e) => failed(&e.to_string()),
    }
}

fn failed(detail: &str) -> VerifyResult {
    VerifyResult {
        valid: false,
        signer_verified: false,
        identity: None,
        detail: detail.to_string(),
    }
}

fn find_principal(sig_file: &Path, allowed_signers: &Path) -> Option<String> {
    let out = run(
        "ssh-keygen",
        &[
            "-Y".as_ref(),
            "find-principals".as_ref(),
            "-s".as_ref(),
            sig_file.as_os_str(),
            "-f".as_ref(),
            allowed_signers.as_os_str(),
        ],
        None,
    )
    .ok()?;
    out.stdout
        .lines()
        .map(str::trim)
        .find(|l| !l.is_empty())
        .map(str::to_string)
}

struct Output {
    code: i32,
    stdout: String,
    stderr: String,
}

fn run(exe: &str, args: &[&std::ffi::OsStr], stdin: Option<&str>) -> Result<Output> {
    use std::io::Write;
    use std::process::Stdio;
    let mut cmd = Command::new(exe);
    cmd.args(args)
        .stdin(if stdin.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = cmd
        .spawn()
        .map_err(|e| RepoError::Other(format!("could not start {exe}: {e}")))?;
    if let Some(input) = stdin {
        let mut pipe = child.stdin.take().expect("piped stdin");
        pipe.write_all(input.as_bytes())?;
        drop(pipe);
    }
    let out = child.wait_with_output()?;
    Ok(Output {
        code: out.status.code().unwrap_or(1),
        stdout: String::from_utf8_lossy(&out.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&out.stderr).into_owned(),
    })
}

fn which(exe: &str) -> Option<PathBuf> {
    // On Windows the binary is ssh-keygen.exe; probing only "ssh-keygen" would
    // silently disable signing/verify even with OpenSSH installed.
    let names: &[String] = &if cfg!(windows) {
        vec![format!("{exe}.exe"), exe.to_string()]
    } else {
        vec![exe.to_string()]
    };
    for dir in std::env::split_paths(&std::env::var_os("PATH")?) {
        for name in names {
            let cand = dir.join(name);
            if cand.is_file() {
                return Some(cand);
            }
        }
    }
    None
}

/// Expands a leading `~` to the home directory, confined to it (a key path
/// like `~/../../etc/x` must not escape). Absolute paths pass through.
fn expand_home(path: &str) -> Result<PathBuf> {
    if !path.starts_with('~') {
        return Ok(PathBuf::from(path));
    }
    let home = std::env::var_os("HOME")
        .or_else(|| std::env::var_os("USERPROFILE"))
        .map(PathBuf::from)
        .ok_or_else(|| RepoError::Other("cannot expand ~: no home directory".into()))?;
    let rel = path.trim_start_matches(['~', '/', '\\']);
    let full = home.join(rel);
    // Normalize without touching the filesystem (the key may not exist yet).
    let mut norm = PathBuf::new();
    for c in full.components() {
        match c {
            std::path::Component::ParentDir => {
                if !norm.pop() {
                    return Err(RepoError::Other(format!(
                        "key path escapes home directory: {path}"
                    )));
                }
            }
            std::path::Component::CurDir => {}
            other => norm.push(other),
        }
    }
    if !norm.starts_with(&home) {
        return Err(RepoError::Other(format!(
            "key path escapes home directory: {path}"
        )));
    }
    Ok(norm)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn home_expansion_is_confined() {
        if std::env::var_os("HOME").is_none() && std::env::var_os("USERPROFILE").is_none() {
            return; // no home in this environment
        }
        assert!(expand_home("~/key").is_ok());
        assert!(expand_home("/abs/key").is_ok());
        assert!(expand_home("~/../../etc/passwd").is_err());
    }

    #[test]
    fn unsigned_is_invalid() {
        let r = verify("payload", "", None);
        assert!(!r.valid && !r.signer_verified);
    }

    /// End-to-end sign + verify with a throwaway ed25519 key (skipped when
    /// ssh-keygen isn't available).
    #[test]
    fn sign_and_verify_roundtrip() {
        if !available() {
            return;
        }
        let d = tempfile::tempdir().unwrap();
        let key = d.path().join("id_test");
        let gen = Command::new("ssh-keygen")
            .args(["-q", "-t", "ed25519", "-N", "", "-C", "t@test", "-f"])
            .arg(&key)
            .status()
            .unwrap();
        assert!(gen.success());

        let payload = "the signable payload";
        let sig = sign(payload, key.to_str().unwrap()).unwrap();
        assert!(sig.contains("BEGIN SSH SIGNATURE"));

        // check-novalidate: valid but not signer-verified
        let r = verify(payload, &sig, None);
        assert!(r.valid && !r.signer_verified, "{}", r.detail);

        // with an allowed-signers file the signer is verified
        let pubkey = std::fs::read_to_string(key.with_extension("pub")).unwrap();
        let signers = d.path().join("allowed");
        std::fs::write(&signers, format!("t@test {pubkey}")).unwrap();
        let r = verify(payload, &sig, signers.to_str());
        assert!(r.valid && r.signer_verified, "{}", r.detail);
        assert_eq!(r.identity.as_deref(), Some("t@test"));

        // a tampered payload fails
        let r = verify("tampered", &sig, signers.to_str());
        assert!(!r.valid);
    }
}

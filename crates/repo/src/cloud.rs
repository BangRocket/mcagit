//! S3 and Azure Blob [`Bucket`] adapters over `ureq` — REST + request signing,
//! no async cloud SDKs. S3 uses AWS Signature V4; Azure uses SharedKey. These
//! are the only part of the cloud transport not exercised end-to-end by tests
//! (the protocol logic lives in [`crate::bucket::BucketTransport`] and runs
//! against `InMemoryBucket`); the signing math is unit-tested against published
//! vectors, the network paths are smoke-only.

use crate::bucket::Bucket;
use crate::{RepoError, Result};
use base64::Engine;
use hmac::{Hmac, Mac};
use sha2::{Digest, Sha256};

type HmacSha256 = Hmac<Sha256>;

fn hmac(key: &[u8], data: &[u8]) -> Vec<u8> {
    let mut m = HmacSha256::new_from_slice(key).expect("hmac accepts any key length");
    m.update(data);
    m.finalize().into_bytes().to_vec()
}

fn sha256_hex(data: &[u8]) -> String {
    let mut h = Sha256::new();
    h.update(data);
    h.finalize().iter().map(|b| format!("{b:02x}")).collect()
}

fn net_err<E: std::fmt::Display>(e: E) -> RepoError {
    RepoError::Other(format!("cloud transport: {e}"))
}

/// Body-read cap for bucket downloads. ureq defaults to 10 MiB, which a normal
/// pack blob (a push batches up to ~128 MiB raw) blows past — so a real cloud
/// push would be unfetchable. Match the wire-pack framing cap; the pack itself
/// is still hash-verified and total-size-bounded by `wirepack` on ingest.
const MAX_DOWNLOAD: u64 = 512 * 1024 * 1024;

// ---- S3 (AWS Signature V4) ----

/// An [`Bucket`] over any S3-compatible store (AWS, R2, B2, MinIO). Credentials
/// come from `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (+ optional
/// `AWS_SESSION_TOKEN`); set `S3_ENDPOINT_URL` (+ `AWS_REGION`) for non-AWS
/// providers.
pub struct S3Bucket {
    bucket: String,
    region: String,
    endpoint: String, // scheme://host (no trailing slash), path-style
    access_key: String,
    secret_key: String,
    session_token: Option<String>,
}

impl S3Bucket {
    /// Connect from the environment for `s3://bucket[/prefix]` (the prefix is
    /// handled by [`crate::bucket::BucketTransport`], not here).
    pub fn connect(bucket: &str) -> Result<Self> {
        let region = std::env::var("AWS_REGION")
            .or_else(|_| std::env::var("AWS_DEFAULT_REGION"))
            .unwrap_or_else(|_| "us-east-1".to_string());
        let endpoint = std::env::var("S3_ENDPOINT_URL")
            .unwrap_or_else(|_| format!("https://s3.{region}.amazonaws.com"));
        let access_key = std::env::var("AWS_ACCESS_KEY_ID")
            .map_err(|_| RepoError::Other("S3: AWS_ACCESS_KEY_ID not set".into()))?;
        let secret_key = std::env::var("AWS_SECRET_ACCESS_KEY")
            .map_err(|_| RepoError::Other("S3: AWS_SECRET_ACCESS_KEY not set".into()))?;
        Ok(Self {
            bucket: bucket.to_string(),
            region,
            endpoint: endpoint.trim_end_matches('/').to_string(),
            access_key,
            secret_key,
            session_token: std::env::var("AWS_SESSION_TOKEN")
                .ok()
                .filter(|s| !s.is_empty()),
        })
    }

    fn url(&self, key: &str) -> String {
        format!("{}/{}/{}", self.endpoint, self.bucket, encode_key(key))
    }

    fn host(&self) -> String {
        self.endpoint
            .split_once("://")
            .map(|(_, h)| h.to_string())
            .unwrap_or_else(|| self.endpoint.clone())
    }

    /// Sign a request, returning the headers to attach (including Authorization).
    fn sign(
        &self,
        method: &str,
        key: &str,
        query: &str,
        payload: &[u8],
        amz_date: &str, // basic ISO-8601, e.g. 20150830T123600Z
        extra: &[(String, String)],
    ) -> Vec<(String, String)> {
        let date = &amz_date[..8];
        let payload_hash = sha256_hex(payload);
        let canonical_uri = format!("/{}/{}", self.bucket, encode_key(key));

        // Canonical + signed headers (lowercased name, sorted).
        let mut headers: Vec<(String, String)> = vec![
            ("host".into(), self.host()),
            ("x-amz-content-sha256".into(), payload_hash.clone()),
            ("x-amz-date".into(), amz_date.to_string()),
        ];
        if let Some(tok) = &self.session_token {
            headers.push(("x-amz-security-token".into(), tok.clone()));
        }
        for (k, v) in extra {
            headers.push((k.to_lowercase(), v.clone()));
        }
        headers.sort_by(|a, b| a.0.cmp(&b.0));
        let canonical_headers: String = headers
            .iter()
            .map(|(k, v)| format!("{k}:{}\n", v.trim()))
            .collect();
        let signed_headers = headers
            .iter()
            .map(|(k, _)| k.as_str())
            .collect::<Vec<_>>()
            .join(";");

        let canonical_request = format!(
            "{method}\n{canonical_uri}\n{query}\n{canonical_headers}\n{signed_headers}\n{payload_hash}"
        );
        let scope = format!("{date}/{}/s3/aws4_request", self.region);
        let string_to_sign = format!(
            "AWS4-HMAC-SHA256\n{amz_date}\n{scope}\n{}",
            sha256_hex(canonical_request.as_bytes())
        );
        let signature =
            sigv4_signature(&self.secret_key, date, &self.region, "s3", &string_to_sign);
        let auth = format!(
            "AWS4-HMAC-SHA256 Credential={}/{scope}, SignedHeaders={signed_headers}, Signature={signature}",
            self.access_key
        );

        let mut out: Vec<(String, String)> = headers
            .into_iter()
            .filter(|(k, _)| k != "host") // ureq sets Host itself
            .collect();
        out.push(("Authorization".into(), auth));
        out
    }
}

/// Derive the SigV4 signing key and sign `string_to_sign` (hex).
fn sigv4_signature(
    secret: &str,
    date: &str,
    region: &str,
    service: &str,
    string_to_sign: &str,
) -> String {
    let k_date = hmac(format!("AWS4{secret}").as_bytes(), date.as_bytes());
    let k_region = hmac(&k_date, region.as_bytes());
    let k_service = hmac(&k_region, service.as_bytes());
    let k_signing = hmac(&k_service, b"aws4_request");
    hmac(&k_signing, string_to_sign.as_bytes())
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// Percent-encode an object key for a path-style S3 URL (keeps `/`).
fn encode_key(key: &str) -> String {
    let mut out = String::new();
    for seg in key.split('/') {
        if !out.is_empty() {
            out.push('/');
        }
        for b in seg.bytes() {
            match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                    out.push(b as char)
                }
                _ => out.push_str(&format!("%{b:02X}")),
            }
        }
    }
    out
}

impl Bucket for S3Bucket {
    fn get(&self, key: &str) -> Result<Option<(Vec<u8>, String)>> {
        let amz = amz_date();
        let headers = self.sign("GET", key, "", b"", &amz, &[]);
        let mut req = ureq::get(self.url(key));
        for (k, v) in &headers {
            req = req.header(k, v);
        }
        match req.call() {
            Ok(mut resp) => {
                let etag = resp
                    .headers()
                    .get("etag")
                    .and_then(|v| v.to_str().ok())
                    .unwrap_or("")
                    .trim_matches('"')
                    .to_string();
                let data = resp
                    .body_mut()
                    .with_config()
                    .limit(MAX_DOWNLOAD)
                    .read_to_vec()
                    .map_err(net_err)?;
                Ok(Some((data, etag)))
            }
            Err(ureq::Error::StatusCode(404)) => Ok(None),
            Err(e) => Err(net_err(e)),
        }
    }

    fn put(&self, key: &str, data: &[u8]) -> Result<()> {
        let amz = amz_date();
        let headers = self.sign("PUT", key, "", data, &amz, &[]);
        let mut req = ureq::put(self.url(key));
        for (k, v) in &headers {
            req = req.header(k, v);
        }
        req.send(data).map_err(net_err)?;
        Ok(())
    }

    fn put_if_match(&self, key: &str, data: &[u8], expected: Option<&str>) -> Result<bool> {
        let amz = amz_date();
        let cond = match expected {
            Some(etag) => ("If-Match".to_string(), format!("\"{etag}\"")),
            None => ("If-None-Match".to_string(), "*".to_string()),
        };
        let headers = self.sign("PUT", key, "", data, &amz, std::slice::from_ref(&cond));
        let mut req = ureq::put(self.url(key));
        for (k, v) in &headers {
            req = req.header(k, v);
        }
        match req.send(data) {
            Ok(_) => Ok(true),
            Err(ureq::Error::StatusCode(412)) | Err(ureq::Error::StatusCode(409)) => Ok(false),
            Err(e) => Err(net_err(e)),
        }
    }

    fn list(&self, prefix: &str) -> Result<Vec<String>> {
        // ListObjectsV2 with the prefix; parse <Key> elements from the XML.
        let query = format!("list-type=2&prefix={}", encode_key(prefix));
        let amz = amz_date();
        let headers = self.sign("GET", "", &query, b"", &amz, &[]);
        let url = format!("{}/{}?{}", self.endpoint, self.bucket, query);
        let mut req = ureq::get(&url);
        for (k, v) in &headers {
            req = req.header(k, v);
        }
        let body = req
            .call()
            .map_err(net_err)?
            .body_mut()
            .read_to_string()
            .map_err(net_err)?;
        Ok(parse_s3_keys(&body))
    }
}

/// Extract `<Key>…</Key>` values from an S3 ListObjectsV2 XML body.
fn parse_s3_keys(xml: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut rest = xml;
    while let Some(start) = rest.find("<Key>") {
        rest = &rest[start + 5..];
        if let Some(end) = rest.find("</Key>") {
            out.push(rest[..end].to_string());
            rest = &rest[end + 6..];
        } else {
            break;
        }
    }
    out
}

// ---- Azure Blob (SharedKey) ----

/// A [`Bucket`] over Azure Blob Storage. Auth via `AZURE_STORAGE_ACCOUNT` +
/// `AZURE_STORAGE_KEY`.
pub struct AzureBucket {
    account: String,
    container: String,
    key: Vec<u8>, // decoded shared key
}

impl AzureBucket {
    pub fn connect(account: &str, container: &str) -> Result<Self> {
        let account = if account.is_empty() {
            std::env::var("AZURE_STORAGE_ACCOUNT")
                .map_err(|_| RepoError::Other("Azure: AZURE_STORAGE_ACCOUNT not set".into()))?
        } else {
            account.to_string()
        };
        let key_b64 = std::env::var("AZURE_STORAGE_KEY")
            .map_err(|_| RepoError::Other("Azure: AZURE_STORAGE_KEY not set".into()))?;
        let key = base64::engine::general_purpose::STANDARD
            .decode(key_b64.trim())
            .map_err(|e| RepoError::Other(format!("Azure: bad AZURE_STORAGE_KEY: {e}")))?;
        Ok(Self {
            account,
            container: container.to_string(),
            key,
        })
    }

    fn url(&self, key: &str) -> String {
        format!(
            "https://{}.blob.core.windows.net/{}/{}",
            self.account,
            self.container,
            encode_key(key)
        )
    }

    /// Build the SharedKey `Authorization` header for a request.
    fn authorization(
        &self,
        method: &str,
        key: &str,
        date: &str,
        content_len: usize,
        canonical_headers: &[(String, String)],
        canonical_query: &[(String, String)],
    ) -> String {
        let mut ch: Vec<(String, String)> = canonical_headers.to_vec();
        ch.sort_by(|a, b| a.0.cmp(&b.0));
        let canon_headers: String = ch.iter().map(|(k, v)| format!("{k}:{v}\n")).collect();

        let mut cq = canonical_query.to_vec();
        cq.sort_by(|a, b| a.0.cmp(&b.0));
        let mut canon_resource = format!("/{}/{}/{}", self.account, self.container, key);
        for (k, v) in cq {
            canon_resource.push_str(&format!("\n{k}:{v}"));
        }

        // The fixed 13-field SharedKey string-to-sign (blob service).
        let len = if content_len == 0 {
            String::new()
        } else {
            content_len.to_string()
        };
        let string_to_sign = format!(
            "{method}\n\n\n{len}\n\napplication/octet-stream\n\n\n\n\n\n\n{canon_headers}{canon_resource}"
        );
        let _ = date; // date travels via the x-ms-date canonical header
        let sig = base64::engine::general_purpose::STANDARD
            .encode(hmac(&self.key, string_to_sign.as_bytes()));
        format!("SharedKey {}:{sig}", self.account)
    }
}

impl Bucket for AzureBucket {
    fn get(&self, key: &str) -> Result<Option<(Vec<u8>, String)>> {
        let date = http_date();
        let ch = vec![
            ("x-ms-date".to_string(), date.clone()),
            ("x-ms-version".to_string(), "2021-08-06".to_string()),
        ];
        let auth = self.authorization("GET", key, &date, 0, &ch, &[]);
        let mut req = ureq::get(self.url(key))
            .header("x-ms-date", &date)
            .header("x-ms-version", "2021-08-06")
            .header("Authorization", &auth);
        let _ = &mut req;
        match req.call() {
            Ok(mut resp) => {
                let etag = resp
                    .headers()
                    .get("etag")
                    .and_then(|v| v.to_str().ok())
                    .unwrap_or("")
                    .trim_matches('"')
                    .to_string();
                let data = resp
                    .body_mut()
                    .with_config()
                    .limit(MAX_DOWNLOAD)
                    .read_to_vec()
                    .map_err(net_err)?;
                Ok(Some((data, etag)))
            }
            Err(ureq::Error::StatusCode(404)) => Ok(None),
            Err(e) => Err(net_err(e)),
        }
    }

    fn put(&self, key: &str, data: &[u8]) -> Result<()> {
        self.put_conditional(key, data, None, false)
    }

    fn put_if_match(&self, key: &str, data: &[u8], expected: Option<&str>) -> Result<bool> {
        match self.put_conditional(key, data, expected, true) {
            Ok(()) => Ok(true),
            Err(RepoError::Other(e)) if e.contains("412") || e.contains("409") => Ok(false),
            Err(e) => Err(e),
        }
    }

    fn list(&self, prefix: &str) -> Result<Vec<String>> {
        let date = http_date();
        let query = vec![
            ("comp".to_string(), "list".to_string()),
            ("prefix".to_string(), prefix.to_string()),
            ("restype".to_string(), "container".to_string()),
        ];
        let ch = vec![
            ("x-ms-date".to_string(), date.clone()),
            ("x-ms-version".to_string(), "2021-08-06".to_string()),
        ];
        // For list the canonical resource has no blob component (container-level).
        let mut cq = query.clone();
        cq.sort_by(|a, b| a.0.cmp(&b.0));
        let mut canon_resource = format!("/{}/{}", self.account, self.container);
        for (k, v) in &cq {
            canon_resource.push_str(&format!("\n{k}:{v}"));
        }
        let mut chs = ch.clone();
        chs.sort_by(|a, b| a.0.cmp(&b.0));
        let canon_headers: String = chs.iter().map(|(k, v)| format!("{k}:{v}\n")).collect();
        let string_to_sign = format!(
            "GET\n\n\n\n\napplication/octet-stream\n\n\n\n\n\n\n{canon_headers}{canon_resource}"
        );
        let auth = format!(
            "SharedKey {}:{}",
            self.account,
            base64::engine::general_purpose::STANDARD
                .encode(hmac(&self.key, string_to_sign.as_bytes()))
        );
        let qs: String = query
            .iter()
            .map(|(k, v)| format!("{k}={}", encode_key(v)))
            .collect::<Vec<_>>()
            .join("&");
        let url = format!(
            "https://{}.blob.core.windows.net/{}?{qs}",
            self.account, self.container
        );
        let body = ureq::get(&url)
            .header("x-ms-date", &date)
            .header("x-ms-version", "2021-08-06")
            .header("Authorization", &auth)
            .call()
            .map_err(net_err)?
            .body_mut()
            .read_to_string()
            .map_err(net_err)?;
        Ok(parse_azure_names(&body))
    }
}

impl AzureBucket {
    fn put_conditional(
        &self,
        key: &str,
        data: &[u8],
        expected: Option<&str>,
        conditional: bool,
    ) -> Result<()> {
        let date = http_date();
        let mut ch = vec![
            ("x-ms-blob-type".to_string(), "BlockBlob".to_string()),
            ("x-ms-date".to_string(), date.clone()),
            ("x-ms-version".to_string(), "2021-08-06".to_string()),
        ];
        let auth = self.authorization("PUT", key, &date, data.len(), &ch, &[]);
        ch.sort_by(|a, b| a.0.cmp(&b.0));
        let mut req = ureq::put(self.url(key))
            .header("x-ms-blob-type", "BlockBlob")
            .header("x-ms-date", &date)
            .header("x-ms-version", "2021-08-06")
            .header("Content-Type", "application/octet-stream")
            .header("Authorization", &auth);
        if conditional {
            req = match expected {
                Some(etag) => req.header("If-Match", &format!("\"{etag}\"")),
                None => req.header("If-None-Match", "*"),
            };
        }
        match req.send(data) {
            Ok(_) => Ok(()),
            Err(ureq::Error::StatusCode(code)) => {
                Err(RepoError::Other(format!("cloud transport: status {code}")))
            }
            Err(e) => Err(net_err(e)),
        }
    }
}

fn parse_azure_names(xml: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut rest = xml;
    while let Some(start) = rest.find("<Name>") {
        rest = &rest[start + 6..];
        if let Some(end) = rest.find("</Name>") {
            out.push(rest[..end].to_string());
            rest = &rest[end + 7..];
        } else {
            break;
        }
    }
    out
}

// ---- timestamps (no chrono; format from SystemTime) ----

/// AWS amz-date in basic ISO-8601 (`YYYYMMDDxHHMMSSZ`, x = literal `T`).
fn amz_date() -> String {
    let (y, mo, d, h, mi, s) = utc_now();
    format!("{y:04}{mo:02}{d:02}T{h:02}{mi:02}{s:02}Z")
}

/// RFC 1123 date for Azure `x-ms-date`, e.g. `Tue, 30 Aug 2015 12:36:00 GMT`.
fn http_date() -> String {
    let (y, mo, d, h, mi, s) = utc_now();
    let secs = days_from_civil(y, mo, d) * 86400 + (h as i64) * 3600 + (mi as i64) * 60 + s as i64;
    let dow = ((secs.div_euclid(86400) % 7) + 4).rem_euclid(7); // 1970-01-01 = Thu(4)
    const DOW: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const MON: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    format!(
        "{}, {d:02} {} {y:04} {h:02}:{mi:02}:{s:02} GMT",
        DOW[dow as usize],
        MON[(mo - 1) as usize]
    )
}

fn utc_now() -> (i64, u32, u32, u32, u32, u32) {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    let days = secs.div_euclid(86400);
    let rem = secs.rem_euclid(86400);
    let (y, mo, d) = civil_from_days(days);
    (
        y,
        mo,
        d,
        (rem / 3600) as u32,
        ((rem % 3600) / 60) as u32,
        (rem % 60) as u32,
    )
}

/// Days since 1970-01-01 → (year, month, day). Howard Hinnant's algorithm.
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// (year, month, day) → days since 1970-01-01.
fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = if m > 2 { m - 3 } else { m + 9 } as i64;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sigv4_matches_aws_test_vector() {
        // AWS SigV4 "get-vanilla" published test vector.
        let secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        let amz_date = "20150830T123600Z";
        let date = "20150830";
        let region = "us-east-1";
        let service = "service";
        let canonical_request = "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        let string_to_sign = format!(
            "AWS4-HMAC-SHA256\n{amz_date}\n{date}/{region}/{service}/aws4_request\n{}",
            sha256_hex(canonical_request.as_bytes())
        );
        let sig = sigv4_signature(secret, date, region, service, &string_to_sign);
        assert_eq!(
            sig,
            "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31"
        );
    }

    #[test]
    fn sha256_of_empty_is_known() {
        assert_eq!(
            sha256_hex(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }

    #[test]
    fn civil_date_roundtrips() {
        // 2015-08-30 is day 16677 since the epoch.
        let days = days_from_civil(2015, 8, 30);
        assert_eq!(days, 16677);
        assert_eq!(civil_from_days(16677), (2015, 8, 30));
        // epoch itself
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(days_from_civil(1970, 1, 1), 0);
    }

    #[test]
    fn s3_key_listing_parses_xml() {
        let xml = "<ListBucketResult><Contents><Key>r/packs/abc</Key></Contents><Contents><Key>r/HEAD</Key></Contents></ListBucketResult>";
        assert_eq!(parse_s3_keys(xml), vec!["r/packs/abc", "r/HEAD"]);
    }

    #[test]
    fn azure_name_listing_parses_xml() {
        let xml =
            "<Blobs><Blob><Name>r/packs/abc</Name></Blob><Blob><Name>r/HEAD</Name></Blob></Blobs>";
        assert_eq!(parse_azure_names(xml), vec!["r/packs/abc", "r/HEAD"]);
    }

    #[test]
    fn key_encoding_keeps_slashes_escapes_specials() {
        assert_eq!(encode_key("r/packs/abc"), "r/packs/abc");
        assert_eq!(encode_key("a b"), "a%20b");
    }
}

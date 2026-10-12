//! Native core of the OpenShape Client Downloader (Ubuntu): SHA-256 of the downloaded .deb (checked against
//! the digest GitHub publishes for every release asset) and Debian version comparison (dpkg rules), so the
//! installer decides "update or not" exactly like apt does. No dependencies.

use std::ffi::CStr;
use std::fs::File;
use std::io::Read;
use std::os::raw::{c_char, c_int};

// ------------------------------------------------------------------ SHA-256

const K: [u32; 64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

pub struct Sha256 {
    h: [u32; 8],
    buf: [u8; 64],
    len: usize,
    total: u64,
}

impl Default for Sha256 {
    fn default() -> Self {
        Sha256 {
            h: [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19],
            buf: [0; 64],
            len: 0,
            total: 0,
        }
    }
}

impl Sha256 {
    fn block(&mut self, b: &[u8]) {
        let mut w = [0u32; 64];
        for i in 0..16 {
            w[i] = u32::from_be_bytes([b[4 * i], b[4 * i + 1], b[4 * i + 2], b[4 * i + 3]]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16].wrapping_add(s0).wrapping_add(w[i - 7]).wrapping_add(s1);
        }
        let [mut a, mut b2, mut c, mut d, mut e, mut f, mut g, mut h] = self.h;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ (!e & g);
            let t1 = h.wrapping_add(s1).wrapping_add(ch).wrapping_add(K[i]).wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b2) ^ (a & c) ^ (b2 & c);
            let t2 = s0.wrapping_add(maj);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b2;
            b2 = a;
            a = t1.wrapping_add(t2);
        }
        for (x, v) in self.h.iter_mut().zip([a, b2, c, d, e, f, g, h]) {
            *x = x.wrapping_add(v);
        }
    }

    pub fn update(&mut self, mut data: &[u8]) {
        self.total += data.len() as u64;
        if self.len > 0 {
            let n = (64 - self.len).min(data.len());
            self.buf[self.len..self.len + n].copy_from_slice(&data[..n]);
            self.len += n;
            data = &data[n..];
            if self.len < 64 {
                return;
            }
            let b = self.buf;
            self.block(&b);
            self.len = 0;
        }
        while data.len() >= 64 {
            self.block(&data[..64]);
            data = &data[64..];
        }
        self.buf[..data.len()].copy_from_slice(data);
        self.len = data.len();
    }

    pub fn finish(mut self) -> [u8; 32] {
        let bits = self.total * 8;
        self.update(&[0x80]);
        while self.len != 56 {
            self.update(&[0]);
        }
        let mut b = self.buf;
        b[56..].copy_from_slice(&bits.to_be_bytes());
        self.block(&b);
        let mut out = [0u8; 32];
        for (i, v) in self.h.iter().enumerate() {
            out[4 * i..4 * i + 4].copy_from_slice(&v.to_be_bytes());
        }
        out
    }
}

pub fn sha256_hex(data: &[u8]) -> String {
    let mut s = Sha256::default();
    s.update(data);
    s.finish().iter().map(|b| format!("{b:02x}")).collect()
}

pub fn sha256_file(path: &str) -> std::io::Result<String> {
    let mut f = File::open(path)?;
    let mut s = Sha256::default();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        s.update(&buf[..n]);
    }
    Ok(s.finish().iter().map(|b| format!("{b:02x}")).collect())
}

// ------------------------------------------------------------------ Debian versions

fn order(c: Option<char>) -> i32 {
    match c {
        None => 0,
        Some('~') => -1,
        Some(c) if c.is_ascii_digit() => 0,
        Some(c) if c.is_ascii_alphabetic() => c as i32,
        Some(c) => c as i32 + 256,
    }
}

/// dpkg's verrevcmp for one upstream or revision part.
fn verrevcmp(a: &str, b: &str) -> i32 {
    let (a, b): (Vec<char>, Vec<char>) = (a.chars().collect(), b.chars().collect());
    let (mut i, mut j) = (0, 0);
    while i < a.len() || j < b.len() {
        let mut first_diff = 0;
        while (i < a.len() && !a[i].is_ascii_digit()) || (j < b.len() && !b[j].is_ascii_digit()) {
            let ac = order(a.get(i).copied());
            let bc = order(b.get(j).copied());
            if ac != bc {
                return ac - bc;
            }
            i += 1;
            j += 1;
        }
        while i < a.len() && a[i] == '0' {
            i += 1;
        }
        while j < b.len() && b[j] == '0' {
            j += 1;
        }
        while i < a.len() && a[i].is_ascii_digit() && j < b.len() && b[j].is_ascii_digit() {
            if first_diff == 0 {
                first_diff = a[i] as i32 - b[j] as i32;
            }
            i += 1;
            j += 1;
        }
        if i < a.len() && a[i].is_ascii_digit() {
            return 1;
        }
        if j < b.len() && b[j].is_ascii_digit() {
            return -1;
        }
        if first_diff != 0 {
            return first_diff;
        }
    }
    0
}

fn split(v: &str) -> (u64, &str, &str) {
    let v = v.trim();
    let (epoch, rest) = match v.split_once(':') {
        Some((e, r)) if e.chars().all(|c| c.is_ascii_digit()) => (e.parse().unwrap_or(0), r),
        _ => (0, v),
    };
    match rest.rsplit_once('-') {
        Some((u, r)) => (epoch, u, r),
        None => (epoch, rest, ""),
    }
}

/// Compare two Debian package versions: negative, zero or positive like dpkg --compare-versions.
pub fn compare_versions(a: &str, b: &str) -> i32 {
    let (ea, ua, ra) = split(a);
    let (eb, ub, rb) = split(b);
    if ea != eb {
        return if ea > eb { 1 } else { -1 };
    }
    let r = verrevcmp(ua, ub);
    if r != 0 {
        return r.signum();
    }
    verrevcmp(ra, rb).signum()
}

// ------------------------------------------------------------------ C ABI

unsafe fn cstr<'a>(p: *const c_char) -> Option<&'a str> {
    if p.is_null() {
        return None;
    }
    CStr::from_ptr(p).to_str().ok()
}

/// Writes the 64 hex digits (plus NUL) of the file's SHA-256 into `out` (65 bytes). Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn osi_sha256_file(path: *const c_char, out: *mut u8) -> c_int {
    let Some(p) = cstr(path) else { return -1 };
    if out.is_null() {
        return -1;
    }
    match sha256_file(p) {
        Ok(hex) => {
            std::ptr::copy_nonoverlapping(hex.as_ptr(), out, 64);
            *out.add(64) = 0;
            0
        }
        Err(_) => -2,
    }
}

/// dpkg --compare-versions: -1, 0 or 1.
#[no_mangle]
pub unsafe extern "C" fn osi_compare_versions(a: *const c_char, b: *const c_char) -> c_int {
    match (cstr(a), cstr(b)) {
        (Some(a), Some(b)) => compare_versions(a, b),
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_known_vectors() {
        assert_eq!(sha256_hex(b""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        assert_eq!(sha256_hex(b"abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        let long = vec![b'a'; 1_000_000];
        assert_eq!(sha256_hex(&long), "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
    }

    #[test]
    fn debian_versions() {
        assert!(compare_versions("1.1.0", "1.0.1") > 0);
        assert!(compare_versions("1.0.10", "1.0.9") > 0);
        assert_eq!(compare_versions("1.1.0", "1.1.0"), 0);
        assert!(compare_versions("1.1.0~rc1", "1.1.0") < 0);
        assert!(compare_versions("1:0.1", "2.0") > 0);
        assert!(compare_versions("1.0-2", "1.0-10") < 0);
        assert!(compare_versions("1.0a", "1.0") > 0);
    }
}

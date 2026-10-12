//! Mesh file formats: STL (binary write, binary/ASCII read), OBJ and USDA.

use crate::geom::{v3, Mesh, V3};
use std::collections::HashMap;
use std::fmt::Write as _;
use std::io::{self, Write};

/// Shared vertex list + index triples (welded on a 1e-6 mm grid).
fn indexed(m: &Mesh) -> (Vec<V3>, Vec<[usize; 3]>) {
    let mut map: HashMap<(i64, i64, i64), usize> = HashMap::new();
    let mut pts = Vec::new();
    let mut tris = Vec::with_capacity(m.tris.len());
    for t in &m.tris {
        let mut idx = [0usize; 3];
        for (k, p) in t.iter().enumerate() {
            let key = ((p.x * 1e6).round() as i64, (p.y * 1e6).round() as i64, (p.z * 1e6).round() as i64);
            idx[k] = *map.entry(key).or_insert_with(|| {
                pts.push(*p);
                pts.len() - 1
            });
        }
        if idx[0] != idx[1] && idx[1] != idx[2] && idx[0] != idx[2] {
            tris.push(idx);
        }
    }
    (pts, tris)
}

pub fn write_stl(bodies: &[(&str, &Mesh)], out: &mut impl Write) -> io::Result<()> {
    let mut header = [0u8; 80];
    let title = b"OpenShape CAD binary STL (mm)";
    header[..title.len()].copy_from_slice(title);
    out.write_all(&header)?;
    let count: usize = bodies.iter().map(|(_, m)| m.tris.len()).sum();
    out.write_all(&(count as u32).to_le_bytes())?;
    for (_, m) in bodies {
        for t in &m.tris {
            let n = (t[1] - t[0]).cross(t[2] - t[0]).norm();
            for v in [n, t[0], t[1], t[2]] {
                for c in [v.x, v.y, v.z] {
                    out.write_all(&(c as f32).to_le_bytes())?;
                }
            }
            out.write_all(&[0, 0])?;
        }
    }
    Ok(())
}

pub fn write_obj(bodies: &[(&str, &Mesh)], out: &mut impl Write) -> io::Result<()> {
    let mut s = String::from("# OpenShape CAD (units: mm, Z up)\n");
    let mut base = 1;
    for (name, m) in bodies {
        let (pts, tris) = indexed(m);
        let _ = writeln!(s, "o {}", sanitize(name));
        for p in &pts {
            let _ = writeln!(s, "v {} {} {}", p.x, p.y, p.z);
        }
        for t in &tris {
            let _ = writeln!(s, "f {} {} {}", t[0] + base, t[1] + base, t[2] + base);
        }
        base += pts.len();
    }
    out.write_all(s.as_bytes())
}

fn sanitize(name: &str) -> String {
    // USD/OBJ identifiers are ASCII: keep letters, digits and '_', fold the rest
    let mut s = String::new();
    for c in name.chars() {
        let c = if c.is_ascii_alphanumeric() { c } else { '_' };
        if !(c == '_' && s.ends_with('_')) {
            s.push(c);
        }
    }
    let s = s.trim_matches('_').to_string();
    if !s.chars().any(|c| c.is_ascii_alphabetic()) {
        return format!("Body_{}", if s.is_empty() { "0".to_string() } else { s });
    }
    if s.chars().next().unwrap().is_ascii_digit() {
        return format!("_{s}");
    }
    s
}

/// USD ASCII stage in millimetres with Z up (Isaac Sim reads metersPerUnit).
pub fn write_usda(bodies: &[(&str, &Mesh)], out: &mut impl Write) -> io::Result<()> {
    let mut s = String::from("#usda 1.0\n(\n    defaultPrim = \"World\"\n    metersPerUnit = 0.001\n    upAxis = \"Z\"\n    doc = \"Exported by OpenShape CAD\"\n)\n\ndef Xform \"World\"\n{\n");
    let mut used: HashMap<String, usize> = HashMap::new();
    for (name, m) in bodies {
        let mut prim = sanitize(name);
        let k = used.entry(prim.clone()).or_insert(0);
        *k += 1;
        if *k > 1 {
            prim = format!("{prim}_{k}");
        }
        let (pts, tris) = indexed(m);
        let (lo, hi) = m.bounds();
        let _ = writeln!(s, "    def Mesh \"{prim}\"\n    {{");
        let _ = writeln!(s, "        float3[] extent = [({}, {}, {}), ({}, {}, {})]", lo.x, lo.y, lo.z, hi.x, hi.y, hi.z);
        let counts = vec!["3"; tris.len()].join(", ");
        let _ = writeln!(s, "        int[] faceVertexCounts = [{counts}]");
        let idx: Vec<String> = tris.iter().map(|t| format!("{}, {}, {}", t[0], t[1], t[2])).collect();
        let _ = writeln!(s, "        int[] faceVertexIndices = [{}]", idx.join(", "));
        let p: Vec<String> = pts.iter().map(|p| format!("({}, {}, {})", p.x, p.y, p.z)).collect();
        let _ = writeln!(s, "        point3f[] points = [{}]", p.join(", "));
        let _ = writeln!(s, "        uniform token subdivisionScheme = \"none\"");
        let _ = writeln!(s, "    }}");
    }
    s.push_str("}\n");
    out.write_all(s.as_bytes())
}

pub fn read_stl(data: &[u8]) -> Option<Mesh> {
    if data.len() >= 84 {
        let n = u32::from_le_bytes([data[80], data[81], data[82], data[83]]) as usize;
        if 84 + n * 50 == data.len() {
            let f = |o: usize| f32::from_le_bytes([data[o], data[o + 1], data[o + 2], data[o + 3]]) as f64;
            let mut m = Mesh::default();
            for i in 0..n {
                let o = 84 + i * 50 + 12;
                let p = |k: usize| v3(f(o + k * 12), f(o + k * 12 + 4), f(o + k * 12 + 8));
                m.tris.push([p(0), p(1), p(2)]);
            }
            return Some(m);
        }
    }
    let text = std::str::from_utf8(data).ok()?;
    let mut m = Mesh::default();
    let mut cur = Vec::new();
    for line in text.lines() {
        let mut it = line.split_whitespace();
        if it.next() == Some("vertex") {
            let c: Vec<f64> = it.filter_map(|x| x.parse().ok()).collect();
            if c.len() == 3 {
                cur.push(v3(c[0], c[1], c[2]));
            }
            if cur.len() == 3 {
                m.tris.push([cur[0], cur[1], cur[2]]);
                cur.clear();
            }
        }
    }
    (!m.tris.is_empty()).then_some(m)
}

pub fn read_obj(text: &str) -> Option<Mesh> {
    let mut pts = Vec::new();
    let mut m = Mesh::default();
    for line in text.lines() {
        let mut it = line.split_whitespace();
        match it.next() {
            Some("v") => {
                let c: Vec<f64> = it.take(3).filter_map(|x| x.parse().ok()).collect();
                if c.len() == 3 {
                    pts.push(v3(c[0], c[1], c[2]));
                }
            }
            Some("f") => {
                let idx: Vec<usize> = it
                    .filter_map(|x| x.split('/').next()?.parse::<i64>().ok())
                    .filter_map(|i| if i < 0 { pts.len().checked_sub((-i) as usize) } else { (i as usize).checked_sub(1) })
                    .collect();
                for k in 1..idx.len().saturating_sub(1) {
                    if let (Some(a), Some(b), Some(c)) = (pts.get(idx[0]), pts.get(idx[k]), pts.get(idx[k + 1])) {
                        m.tris.push([*a, *b, *c]);
                    }
                }
            }
            _ => {}
        }
    }
    (!m.tris.is_empty()).then_some(m)
}

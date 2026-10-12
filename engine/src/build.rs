//! Solid builders: primitives, extrusion and revolution of sketch profiles.

use crate::csg::{boolean, Op};
use crate::geom::{v3, Affine, Mesh, V3};
use std::f64::consts::PI;

pub const SEGMENTS: usize = 64;

/// Sketch plane: origin plus orthonormal u, v (in-plane) and n = u x v.
#[derive(Clone, Copy, Debug)]
pub struct Frame {
    pub o: V3,
    pub u: V3,
    pub v: V3,
    pub n: V3,
}

impl Frame {
    pub fn at(&self, p: (f64, f64), h: f64) -> V3 {
        self.o + self.u * p.0 + self.v * p.1 + self.n * h
    }
}

pub fn signed_area(p: &[(f64, f64)]) -> f64 {
    let n = p.len();
    (0..n).map(|i| {
        let (a, b) = (p[i], p[(i + 1) % n]);
        a.0 * b.1 - b.0 * a.1
    })
    .sum::<f64>()
        / 2.0
}

/// Drop repeated and collinear points; returns a counter-clockwise loop.
pub fn clean_loop(pts: &[(f64, f64)]) -> Vec<(f64, f64)> {
    let mut p: Vec<(f64, f64)> = Vec::with_capacity(pts.len());
    for &q in pts {
        if p.last().map_or(true, |l: &(f64, f64)| (l.0 - q.0).hypot(l.1 - q.1) > 1e-9) {
            p.push(q);
        }
    }
    while p.len() > 1 && (p[0].0 - p[p.len() - 1].0).hypot(p[0].1 - p[p.len() - 1].1) <= 1e-9 {
        p.pop();
    }
    let mut changed = true;
    while changed && p.len() > 3 {
        changed = false;
        let n = p.len();
        for i in 0..n {
            let (a, b, c) = (p[(i + n - 1) % n], p[i], p[(i + 1) % n]);
            let cr = (b.0 - a.0) * (c.1 - b.1) - (b.1 - a.1) * (c.0 - b.0);
            let scale = (b.0 - a.0).hypot(b.1 - a.1) * (c.0 - b.0).hypot(c.1 - b.1);
            if cr.abs() <= 1e-12 * scale.max(1e-12) {
                p.remove(i);
                changed = true;
                break;
            }
        }
    }
    if signed_area(&p) < 0.0 {
        p.reverse();
    }
    p
}

fn in_tri(p: (f64, f64), a: (f64, f64), b: (f64, f64), c: (f64, f64)) -> bool {
    let d = |p: (f64, f64), q: (f64, f64), r: (f64, f64)| (q.0 - p.0) * (r.1 - p.1) - (q.1 - p.1) * (r.0 - p.0);
    let (d1, d2, d3) = (d(a, b, p), d(b, c, p), d(c, a, p));
    d1 >= -1e-12 && d2 >= -1e-12 && d3 >= -1e-12
}

/// Ear-clipping triangulation of a simple counter-clockwise polygon (indices into `p`).
pub fn triangulate(p: &[(f64, f64)]) -> Vec<[usize; 3]> {
    let mut idx: Vec<usize> = (0..p.len()).collect();
    let mut out = Vec::new();
    let mut guard = 0;
    while idx.len() > 3 && guard < 100_000 {
        guard += 1;
        let n = idx.len();
        let mut clipped = false;
        for i in 0..n {
            let (ia, ib, ic) = (idx[(i + n - 1) % n], idx[i], idx[(i + 1) % n]);
            let (a, b, c) = (p[ia], p[ib], p[ic]);
            let convex = (b.0 - a.0) * (c.1 - a.1) - (b.1 - a.1) * (c.0 - a.0) > 1e-14;
            if !convex {
                continue;
            }
            let blocked = idx.iter().any(|&j| j != ia && j != ib && j != ic && in_tri(p[j], a, b, c) && p[j] != a && p[j] != b && p[j] != c);
            if blocked {
                continue;
            }
            out.push([ia, ib, ic]);
            idx.remove(i);
            clipped = true;
            break;
        }
        if !clipped {
            // degenerate input: fall back to a fan so we still produce a closed solid
            for i in 1..idx.len() - 1 {
                out.push([idx[0], idx[i], idx[i + 1]]);
            }
            return out;
        }
    }
    if idx.len() == 3 {
        out.push([idx[0], idx[1], idx[2]]);
    }
    out
}

/// Prism of one simple loop between heights h0 < h1 along the frame normal.
pub fn prism(f: &Frame, loop2d: &[(f64, f64)], h0: f64, h1: f64) -> Mesh {
    let p = clean_loop(loop2d);
    let mut m = Mesh::default();
    if p.len() < 3 || (h1 - h0).abs() < 1e-9 {
        return m;
    }
    let (h0, h1) = (h0.min(h1), h0.max(h1));
    for t in triangulate(&p) {
        m.tris.push([f.at(p[t[0]], h1), f.at(p[t[1]], h1), f.at(p[t[2]], h1)]);
        m.tris.push([f.at(p[t[0]], h0), f.at(p[t[2]], h0), f.at(p[t[1]], h0)]);
    }
    let n = p.len();
    for i in 0..n {
        let (a, b) = (p[i], p[(i + 1) % n]);
        let (a0, b0, b1, a1) = (f.at(a, h0), f.at(b, h0), f.at(b, h1), f.at(a, h1));
        m.tris.push([a0, b0, b1]);
        m.tris.push([a0, b1, a1]);
    }
    m
}

/// Extrude a region (first loop = outer boundary, the rest = holes).
pub fn extrude(f: &Frame, loops: &[Vec<(f64, f64)>], h0: f64, h1: f64) -> Mesh {
    if loops.is_empty() {
        return Mesh::default();
    }
    let mut m = prism(f, &loops[0], h0, h1);
    let pad = 1e-3 * (1.0 + (h1 - h0).abs());
    for hole in &loops[1..] {
        let cut = prism(f, hole, h0.min(h1) - pad, h0.max(h1) + pad);
        m = boolean(&m, &cut, Op::Subtract);
    }
    m
}

/// Revolve a region around the in-plane axis through `a` with direction `d` (sketch coordinates).
pub fn revolve(f: &Frame, loops: &[Vec<(f64, f64)>], a: (f64, f64), d: (f64, f64), angle: f64) -> Mesh {
    if loops.is_empty() {
        return Mesh::default();
    }
    let mut m = revolve_loop(f, &loops[0], a, d, angle);
    for hole in &loops[1..] {
        let cut = revolve_loop(f, hole, a, d, angle);
        m = boolean(&m, &cut, Op::Subtract);
    }
    m
}

fn revolve_loop(f: &Frame, loop2d: &[(f64, f64)], a: (f64, f64), d: (f64, f64), angle: f64) -> Mesh {
    let p = clean_loop(loop2d);
    let mut m = Mesh::default();
    if p.len() < 3 || angle.abs() < 1e-6 {
        return m;
    }
    let angle = angle.clamp(-360.0, 360.0);
    let full = (angle.abs() - 360.0).abs() < 1e-9;
    let origin = f.at(a, 0.0);
    let mut axis = (f.u * d.0 + f.v * d.1).norm();
    // revolve towards +n so cap/side windings below are outward
    let c = p.iter().fold((0.0, 0.0), |s, q| (s.0 + q.0, s.1 + q.1));
    let centroid = f.at((c.0 / p.len() as f64, c.1 / p.len() as f64), 0.0);
    if axis.cross(centroid - origin).dot(f.n) < 0.0 {
        axis = -axis;
    }
    let sign = angle.signum();
    let steps = ((SEGMENTS as f64 * angle.abs() / 360.0).ceil() as usize).max(3);
    let rings: Vec<Vec<V3>> = (0..=steps)
        .map(|k| {
            let r = Affine::rotation(origin, axis, sign * angle.abs() * k as f64 / steps as f64);
            p.iter().map(|q| r.apply(f.at(*q, 0.0))).collect()
        })
        .collect();
    let n = p.len();
    for k in 0..steps {
        for i in 0..n {
            let j = (i + 1) % n;
            let (a0, b0, b1, a1) = (rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]);
            m.tris.push([a0, b0, b1]);
            m.tris.push([a0, b1, a1]);
        }
    }
    if !full {
        for t in triangulate(&p) {
            let s = &rings[0];
            m.tris.push([s[t[0]], s[t[2]], s[t[1]]]);
            let e = &rings[steps];
            m.tris.push([e[t[0]], e[t[1]], e[t[2]]]);
        }
    }
    m.tris.retain(|t| (t[1] - t[0]).cross(t[2] - t[0]).len() > 1e-12);
    // a negative angle sweeps the other way, which mirrors the winding
    m.oriented()
}

pub fn cuboid(lo: V3, hi: V3) -> Mesh {
    let f = Frame { o: v3(0.0, 0.0, 0.0), u: v3(1.0, 0.0, 0.0), v: v3(0.0, 1.0, 0.0), n: v3(0.0, 0.0, 1.0) };
    prism(&f, &[(lo.x, lo.y), (hi.x, lo.y), (hi.x, hi.y), (lo.x, hi.y)], lo.z, hi.z)
}

pub fn circle(c: (f64, f64), r: f64, n: usize) -> Vec<(f64, f64)> {
    (0..n).map(|i| {
        let a = 2.0 * PI * i as f64 / n as f64;
        (c.0 + r * a.cos(), c.1 + r * a.sin())
    })
    .collect()
}

const XY: Frame = Frame { o: v3(0.0, 0.0, 0.0), u: v3(1.0, 0.0, 0.0), v: v3(0.0, 1.0, 0.0), n: v3(0.0, 0.0, 1.0) };
const XZ: Frame = Frame { o: v3(0.0, 0.0, 0.0), u: v3(1.0, 0.0, 0.0), v: v3(0.0, 0.0, 1.0), n: v3(0.0, -1.0, 0.0) };

/// Cylinder standing on z = base.z, centred on base.
pub fn cylinder(base: V3, r: f64, h: f64) -> Mesh {
    let f = Frame { o: base, ..XY };
    prism(&f, &circle((0.0, 0.0), r, SEGMENTS), 0.0, h)
}

/// Cone / frustum standing on base (r1 bottom, r2 top).
pub fn cone(base: V3, r1: f64, r2: f64, h: f64) -> Mesh {
    let f = Frame { o: base, ..XZ };
    let mut prof = vec![(0.0, 0.0), (r1.max(0.0), 0.0)];
    prof.push((r2.max(0.0), h));
    prof.push((0.0, h));
    revolve(&f, &[prof], (0.0, 0.0), (0.0, 1.0), 360.0)
}

pub fn sphere(c: V3, r: f64) -> Mesh {
    let f = Frame { o: c, ..XZ };
    let half = SEGMENTS / 2;
    let prof: Vec<(f64, f64)> = (0..=half)
        .map(|i| {
            let a = -PI / 2.0 + PI * i as f64 / half as f64;
            (r * a.cos(), r * a.sin())
        })
        .collect();
    revolve(&f, &[prof], (0.0, 0.0), (0.0, 1.0), 360.0)
}

pub fn torus(c: V3, major: f64, minor: f64) -> Mesh {
    let f = Frame { o: c, ..XZ };
    let prof = circle((major, 0.0), minor.min(major * 0.999), SEGMENTS / 2);
    revolve(&f, &[prof], (0.0, 0.0), (0.0, 1.0), 360.0)
}

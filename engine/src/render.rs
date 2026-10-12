//! Software viewport renderer: perspective camera, z-buffer, smooth shading,
//! screen-space feature edges, ground grid and a per-pixel id buffer for picking.

use crate::geom::{v3, Mesh, V3};
use std::collections::HashMap;

#[derive(Clone, Copy, Debug)]
pub struct Camera {
    pub eye: V3,
    pub right: V3,
    pub up: V3,
    pub fwd: V3,
    pub focal: f64,
    pub cx: f64,
    pub cy: f64,
}

impl Camera {
    fn view(&self, p: V3) -> V3 {
        let d = p - self.eye;
        v3(d.dot(self.right), d.dot(self.up), d.dot(self.fwd))
    }
    fn screen(&self, v: V3) -> (f64, f64) {
        (self.cx + self.focal * v.x / v.z, self.cy - self.focal * v.y / v.z)
    }
}

pub const FLAG_SELECTED: u32 = 1;
pub const FLAG_GHOST: u32 = 2; // translucent (previews, cut tools)
pub const FLAG_NO_EDGES: u32 = 4;

pub struct Item<'a> {
    pub mesh: &'a Mesh,
    pub smooth: &'a [V3],
    pub color: [f32; 3],
    pub flags: u32,
}

/// Per-corner smooth normals: faces meeting at a vertex within ~35 degrees are averaged.
pub fn smooth_normals(m: &Mesh) -> Vec<V3> {
    let key = |p: V3| ((p.x * 1e4).round() as i64, (p.y * 1e4).round() as i64, (p.z * 1e4).round() as i64);
    let face: Vec<V3> = m.tris.iter().map(|t| (t[1] - t[0]).cross(t[2] - t[0])).collect();
    let mut at: HashMap<(i64, i64, i64), Vec<usize>> = HashMap::new();
    for (i, t) in m.tris.iter().enumerate() {
        for p in t {
            at.entry(key(*p)).or_default().push(i);
        }
    }
    let cos = 35f64.to_radians().cos();
    let mut out = Vec::with_capacity(m.tris.len() * 3);
    for (i, t) in m.tris.iter().enumerate() {
        let fi = face[i].norm();
        for p in t {
            let mut s = v3(0.0, 0.0, 0.0);
            if let Some(list) = at.get(&key(*p)) {
                for &j in list {
                    if face[j].norm().dot(fi) >= cos {
                        s = s + face[j];
                    }
                }
            }
            out.push(if s.len() > 0.0 { s.norm() } else { fi });
        }
    }
    out
}

pub struct Frame {
    pub w: usize,
    pub h: usize,
    pub color: Vec<[f32; 3]>,
    pub depth: Vec<f32>,
    pub id: Vec<u32>,
    pub normal: Vec<[f32; 3]>,
}

impl Frame {
    pub fn new(w: usize, h: usize) -> Frame {
        let n = w * h;
        Frame { w, h, color: vec![[0.0; 3]; n], depth: vec![f32::INFINITY; n], id: vec![0; n], normal: vec![[0.0; 3]; n] }
    }
}

const NEAR: f64 = 0.05;

struct Shaded {
    v: V3,     // view-space position
    n: V3,     // view-space smooth normal
}

/// Clip a view-space triangle against the near plane (returns 0, 3 or 4 vertices).
fn clip_near(t: [Shaded; 3]) -> Vec<Shaded> {
    let inside: Vec<bool> = t.iter().map(|s| s.v.z > NEAR).collect();
    if inside.iter().all(|&b| b) {
        return t.into_iter().collect();
    }
    if !inside.iter().any(|&b| b) {
        return Vec::new();
    }
    let mut out = Vec::new();
    for i in 0..3 {
        let j = (i + 1) % 3;
        let (a, b) = (&t[i], &t[j]);
        if inside[i] {
            out.push(Shaded { v: a.v, n: a.n });
        }
        if inside[i] != inside[j] {
            let k = (NEAR - a.v.z) / (b.v.z - a.v.z);
            out.push(Shaded { v: a.v.lerp(b.v, k), n: a.n.lerp(b.n, k) });
        }
    }
    out
}

fn shade(base: [f32; 3], n: V3, flags: u32) -> [f32; 3] {
    // lights live in view space: a key light from the upper left, a fill from the right
    let key = v3(-0.45, 0.65, -0.62).norm();
    let fill = v3(0.7, -0.1, -0.7).norm();
    let view = v3(0.0, 0.0, -1.0);
    let d1 = n.dot(key).max(0.0);
    let d2 = n.dot(fill).max(0.0);
    let h = (key + view).norm();
    let spec = n.dot(h).max(0.0).powi(40) * 0.35;
    let amb = 0.32;
    let mut c = [0f32; 3];
    for k in 0..3 {
        let b = base[k] as f64;
        c[k] = (b * (amb + 0.62 * d1 + 0.22 * d2) + spec) as f32;
    }
    if flags & FLAG_SELECTED != 0 {
        let sel = [0.2f32, 0.48, 0.92];
        for k in 0..3 {
            c[k] = c[k] * 0.55 + sel[k] * 0.45 * (0.6 + 0.4 * d1 as f32);
        }
    }
    c
}

/// Rasterise one triangle into rows [y0, y1).
fn raster(fr: &mut Band, cam: &Camera, tri: &[Shaded], face_n: V3, id: u32, item: &Item, ghost_pass: bool) {
    let s: Vec<(f64, f64)> = tri.iter().map(|t| cam.screen(t.v)).collect();
    for k in 1..tri.len() - 1 {
        let idx = [0, k, k + 1];
        let (a, b, c) = (s[idx[0]], s[idx[1]], s[idx[2]]);
        let area = (b.0 - a.0) * (c.1 - a.1) - (b.1 - a.1) * (c.0 - a.0);
        if area.abs() < 1e-12 {
            continue;
        }
        let minx = a.0.min(b.0).min(c.0).floor().max(0.0) as i64;
        let maxx = a.0.max(b.0).max(c.0).ceil().min(fr.w as f64 - 1.0) as i64;
        let miny = a.1.min(b.1).min(c.1).floor().max(fr.y0 as f64) as i64;
        let maxy = a.1.max(b.1).max(c.1).ceil().min(fr.y1 as f64 - 1.0) as i64;
        if minx > maxx || miny > maxy {
            continue;
        }
        let iz = [1.0 / tri[idx[0]].v.z, 1.0 / tri[idx[1]].v.z, 1.0 / tri[idx[2]].v.z];
        for y in miny..=maxy {
            let py = y as f64 + 0.5;
            for x in minx..=maxx {
                let px = x as f64 + 0.5;
                let w0 = ((b.0 - px) * (c.1 - py) - (b.1 - py) * (c.0 - px)) / area;
                let w1 = ((c.0 - px) * (a.1 - py) - (c.1 - py) * (a.0 - px)) / area;
                let w2 = 1.0 - w0 - w1;
                if w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9 {
                    continue;
                }
                let inv = w0 * iz[0] + w1 * iz[1] + w2 * iz[2];
                let z = (1.0 / inv) as f32;
                let o = (y as usize - fr.y0) * fr.w + x as usize;
                if z >= fr.depth[o] {
                    continue;
                }
                // perspective-correct normal
                let (p0, p1, p2) = (w0 * iz[0] / inv, w1 * iz[1] / inv, w2 * iz[2] / inv);
                let mut n = (tri[idx[0]].n * p0 + tri[idx[1]].n * p1 + tri[idx[2]].n * p2).norm();
                let pos = tri[idx[0]].v * p0 + tri[idx[1]].v * p1 + tri[idx[2]].v * p2;
                let mut fnv = face_n;
                if n.dot(pos) > 0.0 {
                    n = -n;
                }
                if fnv.dot(pos) > 0.0 {
                    fnv = -fnv;
                }
                let c = shade(item.color, n, item.flags);
                if ghost_pass {
                    let a = 0.42f32;
                    let dst = &mut fr.color[o];
                    for k in 0..3 {
                        dst[k] = dst[k] * (1.0 - a) + c[k] * a;
                    }
                } else {
                    fr.depth[o] = z;
                    fr.color[o] = c;
                    fr.id[o] = id;
                    fr.normal[o] = [fnv.x as f32, fnv.y as f32, fnv.z as f32];
                }
            }
        }
    }
}

struct Band<'a> {
    w: usize,
    y0: usize,
    y1: usize,
    color: &'a mut [[f32; 3]],
    depth: &'a mut [f32],
    id: &'a mut [u32],
    normal: &'a mut [[f32; 3]],
}

fn background(fr: &mut Frame) {
    let top = [1.0f32, 1.0, 1.0];
    let bottom = [0.925f32, 0.93, 0.935];
    for y in 0..fr.h {
        let t = y as f32 / fr.h.max(1) as f32;
        let c = [top[0] + (bottom[0] - top[0]) * t, top[1] + (bottom[1] - top[1]) * t, top[2] + (bottom[2] - top[2]) * t];
        for x in 0..fr.w {
            fr.color[y * fr.w + x] = c;
        }
    }
}

/// Draw a depth-tested line segment (world coordinates).
fn line(fr: &mut Frame, cam: &Camera, a: V3, b: V3, col: [f32; 3], alpha: f32) {
    let (mut va, mut vb) = (cam.view(a), cam.view(b));
    if va.z <= NEAR && vb.z <= NEAR {
        return;
    }
    if va.z <= NEAR {
        va = va.lerp(vb, (NEAR - va.z) / (vb.z - va.z));
    } else if vb.z <= NEAR {
        vb = vb.lerp(va, (NEAR - vb.z) / (va.z - vb.z));
    }
    let (sa, sb) = (cam.screen(va), cam.screen(vb));
    // clip the screen segment to the frame (Liang-Barsky) so huge projections stay dense
    let (dx, dy) = (sb.0 - sa.0, sb.1 - sa.1);
    let (mut t0, mut t1) = (0.0f64, 1.0f64);
    for (p, q) in [(-dx, sa.0 + 1.0), (dx, fr.w as f64 - sa.0), (-dy, sa.1 + 1.0), (dy, fr.h as f64 - sa.1)] {
        if p.abs() < 1e-12 {
            if q < 0.0 {
                return;
            }
        } else {
            let r = q / p;
            if p < 0.0 {
                t0 = t0.max(r);
            } else {
                t1 = t1.min(r);
            }
        }
    }
    if t0 > t1 {
        return;
    }
    let len = (dx.hypot(dy) * (t1 - t0)).ceil();
    let steps = len.min(20000.0) as usize + 1;
    for i in 0..=steps {
        let t = t0 + (t1 - t0) * i as f64 / steps as f64;
        let x = sa.0 + dx * t;
        let y = sa.1 + dy * t;
        if x < 0.0 || y < 0.0 || x >= fr.w as f64 || y >= fr.h as f64 {
            continue;
        }
        let iz = (1.0 / va.z) * (1.0 - t) + (1.0 / vb.z) * t;
        let z = (1.0 / iz) as f32;
        let o = y as usize * fr.w + x as usize;
        if z > fr.depth[o] * 1.0005 {
            continue;
        }
        let d = &mut fr.color[o];
        for k in 0..3 {
            d[k] = d[k] * (1.0 - alpha) + col[k] * alpha;
        }
    }
}

fn grid(fr: &mut Frame, cam: &Camera, step: f64) {
    let n = 60i64;
    // centre the grid under the camera's look point so it never runs out
    let t = if cam.fwd.z.abs() > 1e-6 { -cam.eye.z / cam.fwd.z } else { 0.0 };
    let look = if t > 0.0 { cam.eye + cam.fwd * t } else { v3(0.0, 0.0, 0.0) };
    let big = step * 10.0;
    let ox = (look.x / big).round() * big;
    let oy = (look.y / big).round() * big;
    let ext = step * n as f64;
    for i in -n..=n {
        let x = ox + i as f64 * step;
        let y = oy + i as f64 * step;
        let major = ((x / big) - (x / big).round()).abs() < 1e-9;
        let majory = ((y / big) - (y / big).round()).abs() < 1e-9;
        if x.abs() > 1e-9 {
            line(fr, cam, v3(x, oy - ext, 0.0), v3(x, oy + ext, 0.0), [0.55, 0.57, 0.6], if major { 0.42 } else { 0.2 });
        }
        if y.abs() > 1e-9 {
            line(fr, cam, v3(ox - ext, y, 0.0), v3(ox + ext, y, 0.0), [0.55, 0.57, 0.6], if majory { 0.42 } else { 0.2 });
        }
    }
    let r = ext + ox.abs().max(oy.abs());
    line(fr, cam, v3(-r, 0.0, 0.0), v3(r, 0.0, 0.0), [0.85, 0.2, 0.2], 0.85);
    line(fr, cam, v3(0.0, -r, 0.0), v3(0.0, r, 0.0), [0.2, 0.65, 0.25], 0.85);
}

fn edges(fr: &mut Frame, flags: &[u32]) {
    let (w, h) = (fr.w, fr.h);
    let cos = 30f32.to_radians().cos();
    let mut mark = vec![0u8; w * h];
    for y in 0..h {
        for x in 0..w {
            let o = y * w + x;
            let id = fr.id[o];
            for (dx, dy) in [(1usize, 0usize), (0, 1)] {
                let (nx, ny) = (x + dx, y + dy);
                if nx >= w || ny >= h {
                    continue;
                }
                let q = ny * w + nx;
                let jd = fr.id[q];
                if id == 0 && jd == 0 {
                    continue;
                }
                let edge = if id != jd {
                    true
                } else {
                    let (a, b) = (fr.normal[o], fr.normal[q]);
                    let dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
                    let dz = (fr.depth[o] - fr.depth[q]).abs() / fr.depth[o].min(fr.depth[q]).max(1e-6);
                    dot < cos || dz > 0.02
                };
                if edge {
                    // put the line on the nearer surface
                    let pick = if fr.depth[o] <= fr.depth[q] { o } else { q };
                    let pid = fr.id[pick];
                    if pid == 0 || flags.get(pid as usize - 1).map_or(false, |f| f & FLAG_NO_EDGES != 0) {
                        continue;
                    }
                    mark[pick] = if flags.get(pid as usize - 1).map_or(false, |f| f & FLAG_SELECTED != 0) { 2 } else { 1 };
                }
            }
        }
    }
    for (o, m) in mark.iter().enumerate() {
        let col = match m {
            1 => [0.12f32, 0.12, 0.14],
            2 => [0.05, 0.3, 0.85],
            _ => continue,
        };
        let a = 0.9;
        let d = &mut fr.color[o];
        for k in 0..3 {
            d[k] = d[k] * (1.0 - a) + col[k] * a;
        }
    }
}

pub struct Options {
    pub grid: bool,
    pub grid_step: f64,
    pub edges: bool,
}

pub fn render(items: &[Item], cam: &Camera, w: usize, h: usize, opt: &Options) -> Frame {
    let mut fr = Frame::new(w, h);
    background(&mut fr);
    // view-space geometry, shared by all bands
    let prepared: Vec<Vec<(Vec<Shaded>, V3)>> = items
        .iter()
        .map(|it| {
            it.mesh
                .tris
                .iter()
                .enumerate()
                .filter_map(|(i, t)| {
                    let fnorm = (t[1] - t[0]).cross(t[2] - t[0]).norm();
                    let tri = [0, 1, 2].map(|k| Shaded {
                        v: cam.view(t[k]),
                        n: {
                            let n = it.smooth.get(i * 3 + k).copied().unwrap_or(fnorm);
                            v3(n.dot(cam.right), n.dot(cam.up), n.dot(cam.fwd))
                        },
                    });
                    let poly = clip_near(tri);
                    if poly.len() < 3 {
                        return None;
                    }
                    Some((poly, v3(fnorm.dot(cam.right), fnorm.dot(cam.up), fnorm.dot(cam.fwd))))
                })
                .collect()
        })
        .collect();

    let threads = std::thread::available_parallelism().map_or(4, |n| n.get()).clamp(1, 32);
    let rows = h.div_ceil(threads).max(1);
    for pass in 0..2 {
        let ghost = pass == 1;
        if ghost && !items.iter().any(|i| i.flags & FLAG_GHOST != 0) {
            break;
        }
        std::thread::scope(|s| {
            let mut color = fr.color.as_mut_slice();
            let mut depth = fr.depth.as_mut_slice();
            let mut id = fr.id.as_mut_slice();
            let mut normal = fr.normal.as_mut_slice();
            let mut y0 = 0;
            while y0 < h {
                let y1 = (y0 + rows).min(h);
                let n = (y1 - y0) * w;
                let (c, rc) = color.split_at_mut(n);
                let (d, rd) = depth.split_at_mut(n);
                let (i, ri) = id.split_at_mut(n);
                let (nn, rn) = normal.split_at_mut(n);
                color = rc;
                depth = rd;
                id = ri;
                normal = rn;
                let prepared = &prepared;
                s.spawn(move || {
                    let mut band = Band { w, y0, y1, color: c, depth: d, id: i, normal: nn };
                    for (k, it) in items.iter().enumerate() {
                        if (it.flags & FLAG_GHOST != 0) != ghost {
                            continue;
                        }
                        for (poly, fnv) in &prepared[k] {
                            raster(&mut band, cam, poly, *fnv, k as u32 + 1, it, ghost);
                        }
                    }
                });
                y0 = y1;
            }
        });
        if !ghost {
            if opt.grid {
                grid(&mut fr, cam, opt.grid_step);
            }
            if opt.edges {
                let flags: Vec<u32> = items.iter().map(|i| i.flags).collect();
                edges(&mut fr, &flags);
            }
        }
    }
    fr
}

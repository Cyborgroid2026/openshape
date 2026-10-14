//! Progressive path tracer for the render workspace: BVH over the display meshes, a sky with a sun
//! (soft shadows through next-event estimation), metal/plastic materials and a shadow-catching ground.
//!
//! Each call adds samples per pixel into a float accumulation buffer, so the caller can show the
//! image getting cleaner pass after pass and stop whenever the view changes.

use crate::geom::{v3, V3};
use crate::Solid;
use std::sync::{Arc, Mutex, OnceLock};

#[derive(Clone, Copy)]
pub struct Material {
    pub color: [f64; 3],
    pub metallic: f64,
    pub roughness: f64,
}

struct Tri {
    p0: V3,
    e1: V3,
    e2: V3,
    n: [V3; 3],
    mat: u32,
}

#[derive(Clone, Copy)]
struct Node {
    lo: V3,
    hi: V3,
    /// leaf: first triangle index and count; inner: index of the left child (right = left + 1), count 0
    start: u32,
    count: u32,
}

pub struct Scene {
    tris: Vec<Tri>,
    nodes: Vec<Node>,
    mats: Vec<Material>,
    pub lo: V3,
    pub hi: V3,
}

impl Scene {
    pub fn build(solids: &[(&Solid, Material)]) -> Scene {
        let mut tris = Vec::new();
        let mut mats = Vec::new();
        for (i, (s, m)) in solids.iter().enumerate() {
            mats.push(*m);
            for (k, t) in s.mesh.tris.iter().enumerate() {
                let n = if s.smooth.len() >= k * 3 + 3 { [s.smooth[k * 3], s.smooth[k * 3 + 1], s.smooth[k * 3 + 2]] } else {
                    let f = (t[1] - t[0]).cross(t[2] - t[0]).norm();
                    [f, f, f]
                };
                tris.push(Tri { p0: t[0], e1: t[1] - t[0], e2: t[2] - t[0], n, mat: i as u32 });
            }
        }
        let mut scene = Scene { tris, nodes: Vec::new(), mats, lo: v3(0.0, 0.0, 0.0), hi: v3(0.0, 0.0, 0.0) };
        if !scene.tris.is_empty() {
            let n = scene.tris.len();
            scene.nodes.reserve(n * 2 / 3 + 1);
            scene.nodes.push(Node { lo: v3(0.0, 0.0, 0.0), hi: v3(0.0, 0.0, 0.0), start: 0, count: 0 });
            scene.split(0, 0, n);
            scene.lo = scene.nodes[0].lo;
            scene.hi = scene.nodes[0].hi;
        }
        scene
    }

    fn tri_bounds(t: &Tri) -> (V3, V3) {
        let a = t.p0;
        let b = t.p0 + t.e1;
        let c = t.p0 + t.e2;
        (a.min(b).min(c), a.max(b).max(c))
    }

    fn split(&mut self, node: usize, start: usize, end: usize) {
        let mut lo = v3(f64::MAX, f64::MAX, f64::MAX);
        let mut hi = v3(f64::MIN, f64::MIN, f64::MIN);
        let mut clo = lo;
        let mut chi = hi;
        for t in &self.tris[start..end] {
            let (a, b) = Self::tri_bounds(t);
            lo = lo.min(a);
            hi = hi.max(b);
            let c = (a + b) * 0.5;
            clo = clo.min(c);
            chi = chi.max(c);
        }
        self.nodes[node].lo = lo;
        self.nodes[node].hi = hi;
        let count = end - start;
        if count <= 4 {
            self.nodes[node].start = start as u32;
            self.nodes[node].count = count as u32;
            return;
        }
        // median split along the longest axis of the centroids
        let ext = chi - clo;
        let axis = if ext.x >= ext.y && ext.x >= ext.z { 0 } else if ext.y >= ext.z { 1 } else { 2 };
        let key = |t: &Tri| {
            let (a, b) = Self::tri_bounds(t);
            let c = (a + b) * 0.5;
            match axis {
                0 => c.x,
                1 => c.y,
                _ => c.z,
            }
        };
        let mid = start + count / 2;
        self.tris[start..end].select_nth_unstable_by(count / 2, |a, b| key(a).partial_cmp(&key(b)).unwrap_or(std::cmp::Ordering::Equal));
        let left = self.nodes.len();
        self.nodes.push(Node { lo, hi, start: 0, count: 0 });
        self.nodes.push(Node { lo, hi, start: 0, count: 0 });
        self.nodes[node].start = left as u32;
        self.nodes[node].count = 0;
        self.split(left, start, mid);
        self.split(left + 1, mid, end);
    }

    /// Nearest hit: (distance, triangle, barycentric u, v).
    fn hit(&self, o: V3, d: V3, tmax: f64, any: bool) -> Option<(f64, usize, f64, f64)> {
        if self.nodes.is_empty() {
            return None;
        }
        let inv = v3(1.0 / d.x, 1.0 / d.y, 1.0 / d.z);
        let mut best: Option<(f64, usize, f64, f64)> = None;
        let mut tbest = tmax;
        let mut stack = [0u32; 64];
        let mut sp = 1;
        while sp > 0 {
            sp -= 1;
            let n = &self.nodes[stack[sp] as usize];
            if !slab(n.lo, n.hi, o, inv, tbest) {
                continue;
            }
            if n.count > 0 {
                for i in n.start as usize..(n.start + n.count) as usize {
                    let t = &self.tris[i];
                    // Möller–Trumbore
                    let p = d.cross(t.e2);
                    let det = t.e1.dot(p);
                    if det.abs() < 1e-14 {
                        continue;
                    }
                    let id = 1.0 / det;
                    let s = o - t.p0;
                    let u = s.dot(p) * id;
                    if !(0.0..=1.0).contains(&u) {
                        continue;
                    }
                    let q = s.cross(t.e1);
                    let v = d.dot(q) * id;
                    if v < 0.0 || u + v > 1.0 {
                        continue;
                    }
                    let dist = t.e2.dot(q) * id;
                    if dist > 1e-7 && dist < tbest {
                        tbest = dist;
                        best = Some((dist, i, u, v));
                        if any {
                            return best;
                        }
                    }
                }
            } else if sp + 2 <= stack.len() {
                stack[sp] = n.start;
                stack[sp + 1] = n.start + 1;
                sp += 2;
            }
        }
        best
    }
}

fn slab(lo: V3, hi: V3, o: V3, inv: V3, tmax: f64) -> bool {
    let (mut t0, mut t1) = (0.0f64, tmax);
    for (l, h, oo, i) in [(lo.x, hi.x, o.x, inv.x), (lo.y, hi.y, o.y, inv.y), (lo.z, hi.z, o.z, inv.z)] {
        let (mut a, mut b) = ((l - oo) * i, (h - oo) * i);
        if a > b {
            std::mem::swap(&mut a, &mut b);
        }
        t0 = t0.max(a);
        t1 = t1.min(b);
        if t0 > t1 {
            return false;
        }
    }
    true
}

/// Lighting and backdrop.
#[derive(Clone, Copy)]
pub struct Env {
    pub sky_top: [f64; 3],
    pub sky_horizon: [f64; 3],
    pub ground: [f64; 3],
    /// unit vector towards the sun
    pub sun: V3,
    pub sun_color: [f64; 3],
    /// angular radius of the sun disc (radians): bigger = softer shadows
    pub sun_radius: f64,
    pub ground_z: f64,
    pub ground_on: bool,
    pub ground_reflect: f64,
    /// solid colour behind the model (seen by the camera only), or None for the sky
    pub backdrop: Option<[f64; 3]>,
    pub exposure: f64,
    /// brightness of the two studio soft boxes in the sky (0 = none)
    pub softboxes: f64,
}

impl Env {
    /// Read from the C array documented on `osc_raytrace`.
    pub fn from(a: &[f64]) -> Env {
        Env {
            sky_top: [a[0], a[1], a[2]],
            sky_horizon: [a[3], a[4], a[5]],
            ground: [a[6], a[7], a[8]],
            sun: v3(a[9], a[10], a[11]).norm(),
            sun_color: [a[12], a[13], a[14]],
            sun_radius: a[15].to_radians().max(1e-4),
            ground_z: a[16],
            ground_on: a[17] != 0.0,
            ground_reflect: a[18].clamp(0.0, 1.0),
            backdrop: if a[19] != 0.0 { Some([a[20], a[21], a[22]]) } else { None },
            exposure: a[23],
            softboxes: if a.len() > 24 { a[24] } else { 0.0 },
        }
    }

    fn sky(&self, d: V3) -> [f64; 3] {
        let t = d.z.clamp(-1.0, 1.0);
        let base = if t >= 0.0 {
            let k = t.sqrt();
            lerp3(self.sky_horizon, self.sky_top, k)
        } else {
            lerp3(self.sky_horizon, mul3(self.ground, 0.6), (-t).sqrt().min(1.0))
        };
        if self.softboxes <= 0.0 || t <= 0.05 {
            return base;
        }
        // studio presets: two large soft boxes left and right above the model, like a photo booth,
        // so polished metal has something to reflect
        let az = d.y.atan2(d.x);
        let el = t.asin();
        let mut boost = 0.0;
        for centre in [0.9f64, 0.9 + std::f64::consts::PI] {
            let da = ((az - centre + std::f64::consts::PI).rem_euclid(std::f64::consts::TAU) - std::f64::consts::PI).abs();
            let de = (el - 0.6).abs();
            if da < 0.45 && de < 0.3 {
                let edge = ((0.45 - da) / 0.08).min(1.0) * ((0.3 - de) / 0.08).min(1.0);
                boost += self.softboxes * edge;
            }
        }
        add3(base, [boost; 3])
    }
}

fn lerp3(a: [f64; 3], b: [f64; 3], t: f64) -> [f64; 3] {
    [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]
}
fn mul3(a: [f64; 3], k: f64) -> [f64; 3] {
    [a[0] * k, a[1] * k, a[2] * k]
}
fn mulv(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] * b[0], a[1] * b[1], a[2] * b[2]]
}
fn add3(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

/// Small fast PRNG (PCG32).
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> f64 {
        let old = self.0;
        self.0 = old.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let x = (((old >> 18) ^ old) >> 27) as u32;
        let r = (old >> 59) as u32;
        (x.rotate_right(r) as f64) / 4294967296.0
    }
}

/// Orthonormal basis around n.
fn basis(n: V3) -> (V3, V3) {
    let a = if n.x.abs() > 0.9 { v3(0.0, 1.0, 0.0) } else { v3(1.0, 0.0, 0.0) };
    let t = n.cross(a).norm();
    (t, n.cross(t))
}

fn cosine_dir(n: V3, rng: &mut Rng) -> V3 {
    let (t, b) = basis(n);
    let r1 = rng.next() * std::f64::consts::TAU;
    let r2 = rng.next();
    let s = r2.sqrt();
    (t * (r1.cos() * s) + b * (r1.sin() * s) + n * (1.0 - r2).sqrt()).norm()
}

/// Direction within a cone of half-angle `a` around `d`.
fn cone_dir(d: V3, a: f64, rng: &mut Rng) -> V3 {
    let (t, b) = basis(d);
    let cos_a = a.cos();
    let z = 1.0 - rng.next() * (1.0 - cos_a);
    let r = (1.0 - z * z).max(0.0).sqrt();
    let phi = rng.next() * std::f64::consts::TAU;
    (t * (r * phi.cos()) + b * (r * phi.sin()) + d * z).norm()
}

fn reflect(d: V3, n: V3) -> V3 {
    d - n * (2.0 * d.dot(n))
}

pub struct Camera {
    pub eye: V3,
    pub right: V3,
    pub up: V3,
    pub fwd: V3,
    pub focal: f64,
    pub cx: f64,
    pub cy: f64,
}

fn radiance(scene: &Scene, env: &Env, mut o: V3, mut d: V3, rng: &mut Rng) -> [f64; 3] {
    let mut col = [0.0; 3];
    let mut thr = [1.0; 3];
    let mut specular_bounce = true; // the camera ray sees the sun disc directly
    for bounce in 0..6 {
        let tri_hit = scene.hit(o, d, f64::MAX, false);
        let ground_t = if env.ground_on && d.z < -1e-9 { (env.ground_z - o.z) / d.z } else { -1.0 };
        let ground_hit = ground_t > 1e-6 && tri_hit.map_or(true, |h| ground_t < h.0);
        if tri_hit.is_none() && !ground_hit {
            let mut sky = if bounce == 0 { env.backdrop.unwrap_or_else(|| env.sky(d)) } else { env.sky(d) };
            // the sun disc shows in reflections, and to the camera when the sky is the backdrop
            let sees_sun = specular_bounce && (bounce > 0 || env.backdrop.is_none());
            if sees_sun {
                if d.dot(env.sun) > env.sun_radius.cos() {
                    let k = 1.0 / (std::f64::consts::PI * env.sun_radius * env.sun_radius);
                    sky = add3(sky, mul3(env.sun_color, k));
                }
            }
            col = add3(col, mulv(thr, sky));
            break;
        }
        let (p, n, mat, ground) = if ground_hit {
            (o + d * ground_t, v3(0.0, 0.0, 1.0), Material { color: env.ground, metallic: 0.0, roughness: 1.0 }, true)
        } else {
            let (t, i, u, v) = tri_hit.unwrap();
            let tr = &scene.tris[i];
            let mut n = (tr.n[0] * (1.0 - u - v) + tr.n[1] * u + tr.n[2] * v).norm();
            let gn = tr.e1.cross(tr.e2).norm();
            let gn = if gn.dot(d) > 0.0 { gn * -1.0 } else { gn };
            if n.dot(gn) < 0.0 {
                n = n * -1.0;
            }
            (o + d * t, n, scene.mats[tr.mat as usize], false)
        };
        let eps = 1e-4 * (1.0 + p.len() * 1e-3);
        let po = p + n * eps;
        let metallic = mat.metallic.clamp(0.0, 1.0);
        let rough = mat.roughness.clamp(0.02, 1.0);
        // Schlick Fresnel with F0 = 0.04 for plastics and the base colour for metals
        let cos_v = (-d.dot(n)).clamp(0.0, 1.0);
        let f0 = lerp3([0.04; 3], mat.color, metallic);
        let fw = (1.0 - cos_v).powi(5);
        let fres = [f0[0] + (1.0 - f0[0]) * fw, f0[1] + (1.0 - f0[1]) * fw, f0[2] + (1.0 - f0[2]) * fw];
        let fres_avg = (fres[0] + fres[1] + fres[2]) / 3.0;
        let diffuse = mul3(mat.color, 1.0 - metallic);
        let p_spec = if ground {
            env.ground_reflect * 0.5
        } else {
            (fres_avg * (1.0 - rough * 0.5)).clamp(0.05, 0.95).max(metallic * 0.9)
        };

        // direct sun light (soft shadow towards a random point on the disc)
        let ld = cone_dir(env.sun, env.sun_radius, rng);
        let ndl = n.dot(ld);
        if ndl > 0.0 && scene.hit(po, ld, f64::MAX, true).is_none() {
            // sun_color is the irradiance that makes a white matte surface facing the sun read sun_color
            let dif = mul3(diffuse, ndl);
            // a broad highlight for rough surfaces (the sun disc itself is caught by specular rays)
            let h = (ld - d).norm();
            let shin = 2.0 / (rough * rough * rough * rough) - 2.0;
            let spec_k = if rough > 0.15 { n.dot(h).max(0.0).powf(shin.max(1.0)) * (shin + 8.0) / (8.0 * std::f64::consts::PI) * ndl } else { 0.0 };
            let lit = add3(dif, mul3(fres, spec_k));
            col = add3(col, mulv(thr, mulv(lit, env.sun_color)));
        }

        // continue the path. Plastics: estimate the glossy reflection of the environment with one ray
        // every time (no speckles from rarely chosen bright reflections) and carry on diffusely.
        // Metals and the ground: choose between the specular lobe and diffuse.
        if !ground && metallic < 0.5 {
            let r = reflect(d, n);
            let sd = if rough > 0.02 { cone_dir(r, rough * rough * 1.2, rng) } else { r };
            let to_ground = env.ground_on && sd.z < -1e-9 && (env.ground_z - po.z) / sd.z > 0.0;
            if sd.dot(n) > 0.0 && !to_ground && scene.hit(po, sd, f64::MAX, true).is_none() {
                let mut l = env.sky(sd);
                if rough < 0.35 && sd.dot(env.sun) > env.sun_radius.cos() {
                    l = add3(l, mul3(env.sun_color, 1.0 / (std::f64::consts::PI * env.sun_radius * env.sun_radius)));
                }
                col = add3(col, mulv(thr, mulv(fres, l)));
            }
            thr = mulv(thr, mul3(diffuse, 1.0 - fres_avg));
            specular_bounce = false;
            o = po;
            d = cosine_dir(n, rng);
        } else if rng.next() < p_spec {
            let r = reflect(d, n);
            let nd = if rough > 0.02 { cone_dir(r, rough * rough * 1.2, rng) } else { r };
            if nd.dot(n) <= 0.0 {
                break;
            }
            let w = if ground { [env.ground_reflect; 3] } else { fres };
            thr = mulv(thr, mul3(w, 1.0 / p_spec));
            specular_bounce = rough < 0.35;
            o = po;
            d = nd;
        } else {
            thr = mulv(thr, mul3(diffuse, 1.0 / (1.0 - p_spec)));
            specular_bounce = false;
            o = po;
            d = cosine_dir(n, rng);
        }
        // Russian roulette
        if bounce >= 2 {
            let m = thr[0].max(thr[1]).max(thr[2]).min(0.95);
            if rng.next() > m {
                break;
            }
            thr = mul3(thr, 1.0 / m);
        }
    }
    col
}

/// ACES filmic approximation, then sRGB.
fn tone(c: f64) -> u8 {
    let a = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);
    let s = a.clamp(0.0, 1.0);
    let g = if s <= 0.0031308 { 12.92 * s } else { 1.055 * s.powf(1.0 / 2.4) - 0.055 };
    (g * 255.0 + 0.5) as u8
}

/// Add `spp` samples per pixel into `accum` (RGB floats) and write the tone-mapped average of
/// `total` samples into `out` (RGBA).
pub fn render(scene: &Scene, env: &Env, cam: &Camera, w: usize, h: usize, spp: usize, seed: u64, accum: &mut [f32], total: usize, out: &mut [u8]) {
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).min(64);
    let rows: Vec<(usize, &mut [f32])> = accum.chunks_mut(w * 3).enumerate().collect();
    let rows = Mutex::new(rows.into_iter());
    std::thread::scope(|s| {
        for _ in 0..threads {
            s.spawn(|| loop {
                let next = rows.lock().unwrap().next();
                let Some((y, row)) = next else { break };
                let mut rng = Rng(seed.wrapping_mul(0x9E3779B97F4A7C15) ^ ((y as u64) << 20) ^ 0x2545F4914F6CDD1D);
                rng.next();
                for x in 0..w {
                    let mut c = [0.0; 3];
                    for _ in 0..spp {
                        let px = x as f64 + rng.next();
                        let py = y as f64 + rng.next();
                        let d = (cam.fwd * cam.focal + cam.right * (px - cam.cx) - cam.up * (py - cam.cy)).norm();
                        let r = radiance(scene, env, cam.eye, d, &mut rng);
                        for k in 0..3 {
                            // clamp fireflies
                            c[k] += r[k].min(6.0);
                        }
                    }
                    for k in 0..3 {
                        row[x * 3 + k] += c[k] as f32;
                    }
                }
            });
        }
    });
    let inv = env.exposure / total.max(1) as f64;
    for (i, px) in out.chunks_mut(4).enumerate().take(w * h) {
        px[0] = tone(accum[i * 3] as f64 * inv);
        px[1] = tone(accum[i * 3 + 1] as f64 * inv);
        px[2] = tone(accum[i * 3 + 2] as f64 * inv);
        px[3] = 255;
    }
}

/// The last scene built, reused while the set of solids and materials stays the same.
pub fn cached_scene(key: Vec<u64>, build: impl FnOnce() -> Scene) -> Arc<Scene> {
    static CACHE: OnceLock<Mutex<Option<(Vec<u64>, Arc<Scene>)>>> = OnceLock::new();
    let c = CACHE.get_or_init(|| Mutex::new(None));
    let mut g = c.lock().unwrap();
    if let Some((k, s)) = g.as_ref() {
        if *k == key {
            return s.clone();
        }
    }
    let s = Arc::new(build());
    *g = Some((key, s.clone()));
    s
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geom::Mesh;

    fn cube(lo: V3, hi: V3) -> Mesh {
        let p = |i: usize| v3(if i & 1 != 0 { hi.x } else { lo.x }, if i & 2 != 0 { hi.y } else { lo.y }, if i & 4 != 0 { hi.z } else { lo.z });
        let quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        let mut m = Mesh::default();
        for q in quads {
            m.tris.push([p(q[0]), p(q[1]), p(q[2])]);
            m.tris.push([p(q[0]), p(q[2]), p(q[3])]);
        }
        m
    }

    fn env() -> Env {
        Env {
            sky_top: [0.35, 0.5, 0.8],
            sky_horizon: [0.8, 0.85, 0.9],
            ground: [0.8, 0.8, 0.8],
            sun: v3(0.3, -0.4, 0.85).norm(),
            sun_color: [1.2, 1.15, 1.1],
            sun_radius: 2f64.to_radians(),
            ground_z: 0.0,
            ground_on: true,
            ground_reflect: 0.0,
            backdrop: None,
            exposure: 1.0,
            softboxes: 0.0,
        }
    }

    /// Camera looking straight down at the origin from height z.
    fn top_cam(z: f64, w: usize, h: usize) -> Camera {
        Camera { eye: v3(0.0, 0.0, z), right: v3(1.0, 0.0, 0.0), up: v3(0.0, 1.0, 0.0), fwd: v3(0.0, 0.0, -1.0), focal: h as f64, cx: w as f64 / 2.0, cy: h as f64 / 2.0 }
    }

    fn luminance(px: &[u8], w: usize, x: usize, y: usize) -> f64 {
        let i = (y * w + x) * 4;
        0.3 * px[i] as f64 + 0.6 * px[i + 1] as f64 + 0.1 * px[i + 2] as f64
    }

    #[test]
    fn box_casts_a_shadow_on_the_ground() {
        let mesh = cube(v3(-5.0, -5.0, 0.0), v3(5.0, 5.0, 10.0));
        let solid = Solid { smooth: crate::render::smooth_normals(&mesh), mesh, faces: Vec::new() };
        let scene = Scene::build(&[(&solid, Material { color: [0.7, 0.7, 0.7], metallic: 0.0, roughness: 0.5 })]);
        let (w, h) = (80, 80);
        let mut acc = vec![0f32; w * h * 3];
        let mut px = vec![0u8; w * h * 4];
        render(&scene, &env(), &top_cam(60.0, w, h), w, h, 16, 1, &mut acc, 16, &mut px);
        // the sun is towards +x -y, so the shadow falls towards -x +y of the box: ground point
        // (-6.75, 6.75) looks at the sun through the box, (6.75, -6.75) sees it
        let shadow = luminance(&px, w, 31, 31);
        let lit = luminance(&px, w, 49, 49);
        let top = luminance(&px, w, 40, 40);
        assert!(lit > shadow + 40.0, "lit ground {lit} vs shadow {shadow}");
        assert!(top > 60.0, "box top is lit ({top})");
    }

    #[test]
    fn mirror_metal_reflects_the_sky() {
        let mesh = cube(v3(-50.0, -50.0, -1.0), v3(50.0, 50.0, 0.0));
        let solid = Solid { smooth: crate::render::smooth_normals(&mesh), mesh, faces: Vec::new() };
        let mut e = env();
        e.ground_on = false;
        e.sun = v3(1.0, 0.0, 0.2).norm();   // keep the sun out of the mirror
        let (w, h) = (20, 20);
        let mut acc = vec![0f32; w * h * 3];
        let mut px = vec![0u8; w * h * 4];
        let scene = Scene::build(&[(&solid, Material { color: [0.95, 0.95, 0.95], metallic: 1.0, roughness: 0.02 })]);
        render(&scene, &e, &top_cam(30.0, w, h), w, h, 4, 2, &mut acc, 4, &mut px);
        let i = (10 * w + 10) * 4;
        // looking straight down at a mirror shows the zenith: blue dominates
        assert!(px[i + 2] > px[i] + 20, "reflection is sky blue: {:?}", &px[i..i + 3]);
    }
}

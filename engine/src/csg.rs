//! Constructive solid geometry on triangle meshes with BSP trees
//! (the classic csg.js algorithm, in f64).

use crate::geom::{Mesh, V3};

const EPS: f64 = 1e-6;

#[derive(Clone, Copy, Debug)]
struct Plane {
    n: V3,
    w: f64,
}

impl Plane {
    fn from_points(a: V3, b: V3, c: V3) -> Option<Plane> {
        let n = (b - a).cross(c - a);
        let l = n.len();
        if l < 1e-12 {
            return None;
        }
        let n = n * (1.0 / l);
        Some(Plane { n, w: n.dot(a) })
    }

    fn flip(&mut self) {
        self.n = -self.n;
        self.w = -self.w;
    }
}

#[derive(Clone, Debug)]
struct Poly {
    v: Vec<V3>,
    plane: Plane,
}

impl Poly {
    fn flip(&mut self) {
        self.v.reverse();
        self.plane.flip();
    }
}

const COPLANAR: u8 = 0;
const FRONT: u8 = 1;
const BACK: u8 = 2;
const SPANNING: u8 = 3;

/// Split `p` by `plane` into the four output lists.
fn split(plane: &Plane, p: Poly, cf: &mut Vec<Poly>, cb: &mut Vec<Poly>, f: &mut Vec<Poly>, b: &mut Vec<Poly>) {
    let mut kind = 0u8;
    let types: Vec<u8> = p
        .v
        .iter()
        .map(|v| {
            let t = plane.n.dot(*v) - plane.w;
            let k = if t < -EPS {
                BACK
            } else if t > EPS {
                FRONT
            } else {
                COPLANAR
            };
            kind |= k;
            k
        })
        .collect();
    match kind {
        COPLANAR => {
            if plane.n.dot(p.plane.n) > 0.0 {
                cf.push(p)
            } else {
                cb.push(p)
            }
        }
        FRONT => f.push(p),
        BACK => b.push(p),
        _ => {
            let n = p.v.len();
            let mut fv = Vec::with_capacity(n + 1);
            let mut bv = Vec::with_capacity(n + 1);
            for i in 0..n {
                let j = (i + 1) % n;
                let (ti, tj) = (types[i], types[j]);
                let (vi, vj) = (p.v[i], p.v[j]);
                if ti != BACK {
                    fv.push(vi);
                }
                if ti != FRONT {
                    bv.push(vi);
                }
                if (ti | tj) == SPANNING {
                    let t = (plane.w - plane.n.dot(vi)) / plane.n.dot(vj - vi);
                    let v = vi.lerp(vj, t);
                    fv.push(v);
                    bv.push(v);
                }
            }
            if fv.len() >= 3 {
                f.push(Poly { v: fv, plane: p.plane });
            }
            if bv.len() >= 3 {
                b.push(Poly { v: bv, plane: p.plane });
            }
        }
    }
}

#[derive(Default)]
struct Node {
    plane: Option<Plane>,
    front: Option<Box<Node>>,
    back: Option<Box<Node>>,
    polys: Vec<Poly>,
}

impl Node {
    fn new(polys: Vec<Poly>) -> Node {
        let mut n = Node::default();
        n.build(polys);
        n
    }

    fn invert(&mut self) {
        for p in &mut self.polys {
            p.flip();
        }
        if let Some(pl) = &mut self.plane {
            pl.flip();
        }
        if let Some(f) = &mut self.front {
            f.invert();
        }
        if let Some(b) = &mut self.back {
            b.invert();
        }
        std::mem::swap(&mut self.front, &mut self.back);
    }

    fn clip_polygons(&self, polys: Vec<Poly>) -> Vec<Poly> {
        let Some(plane) = self.plane else { return polys };
        let (mut f, mut b) = (Vec::new(), Vec::new());
        let (mut cf, mut cb) = (Vec::new(), Vec::new());
        for p in polys {
            split(&plane, p, &mut cf, &mut cb, &mut f, &mut b);
        }
        f.append(&mut cf);
        b.append(&mut cb);
        let mut f = match &self.front {
            Some(n) => n.clip_polygons(f),
            None => f,
        };
        let b = match &self.back {
            Some(n) => n.clip_polygons(b),
            None => Vec::new(),
        };
        f.extend(b);
        f
    }

    fn clip_to(&mut self, bsp: &Node) {
        self.polys = bsp.clip_polygons(std::mem::take(&mut self.polys));
        if let Some(f) = &mut self.front {
            f.clip_to(bsp);
        }
        if let Some(b) = &mut self.back {
            b.clip_to(bsp);
        }
    }

    fn all_polygons(&self, out: &mut Vec<Poly>) {
        out.extend(self.polys.iter().cloned());
        if let Some(f) = &self.front {
            f.all_polygons(out);
        }
        if let Some(b) = &self.back {
            b.all_polygons(out);
        }
    }

    fn build(&mut self, polys: Vec<Poly>) {
        if polys.is_empty() {
            return;
        }
        let plane = *self.plane.get_or_insert(polys[0].plane);
        let (mut f, mut b) = (Vec::new(), Vec::new());
        let (mut cf, mut cb) = (Vec::new(), Vec::new());
        for p in polys {
            split(&plane, p, &mut cf, &mut cb, &mut f, &mut b);
        }
        self.polys.append(&mut cf);
        self.polys.append(&mut cb);
        if !f.is_empty() {
            self.front.get_or_insert_with(Default::default).build(f);
        }
        if !b.is_empty() {
            self.back.get_or_insert_with(Default::default).build(b);
        }
    }

    fn polygons(&self) -> Vec<Poly> {
        let mut v = Vec::new();
        self.all_polygons(&mut v);
        v
    }
}

fn to_polys(m: &Mesh) -> Vec<Poly> {
    m.tris
        .iter()
        .filter_map(|t| Plane::from_points(t[0], t[1], t[2]).map(|plane| Poly { v: t.to_vec(), plane }))
        .collect()
}

fn to_mesh(polys: &[Poly]) -> Mesh {
    let mut tris = Vec::new();
    for p in polys {
        for i in 1..p.v.len() - 1 {
            let t = [p.v[0], p.v[i], p.v[i + 1]];
            if (t[1] - t[0]).cross(t[2] - t[0]).len() > 1e-12 {
                tris.push(t);
            }
        }
    }
    Mesh { tris }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Op {
    Union,
    Subtract,
    Intersect,
}

fn overlap(a: &Mesh, b: &Mesh) -> bool {
    let (alo, ahi) = a.bounds();
    let (blo, bhi) = b.bounds();
    alo.x <= bhi.x + EPS && blo.x <= ahi.x + EPS && alo.y <= bhi.y + EPS && blo.y <= ahi.y + EPS && alo.z <= bhi.z + EPS && blo.z <= ahi.z + EPS
}

fn run(a: &Mesh, b: &Mesh, op: Op) -> Mesh {
    let mut na = Node::new(to_polys(a));
    let mut nb = Node::new(to_polys(b));
    match op {
        Op::Union => {
            na.clip_to(&nb);
            nb.clip_to(&na);
            nb.invert();
            nb.clip_to(&na);
            nb.invert();
            na.build(nb.polygons());
        }
        Op::Subtract => {
            na.invert();
            na.clip_to(&nb);
            nb.clip_to(&na);
            nb.invert();
            nb.clip_to(&na);
            nb.invert();
            na.build(nb.polygons());
            na.invert();
        }
        Op::Intersect => {
            na.invert();
            nb.clip_to(&na);
            nb.invert();
            na.clip_to(&nb);
            nb.clip_to(&na);
            na.build(nb.polygons());
            na.invert();
        }
    }
    to_mesh(&na.polygons())
}

/// Boolean of two closed meshes. BSP recursion can get deep, so it runs on a big stack.
pub fn boolean(a: &Mesh, b: &Mesh, op: Op) -> Mesh {
    if a.tris.is_empty() || b.tris.is_empty() || !overlap(a, b) {
        return match op {
            Op::Union => {
                let mut m = a.clone();
                m.append(b);
                m
            }
            Op::Subtract => a.clone(),
            Op::Intersect => Mesh::default(),
        };
    }
    std::thread::scope(|s| {
        std::thread::Builder::new()
            .stack_size(1 << 30)
            .spawn_scoped(s, || run(a, b, op))
            .expect("csg thread")
            .join()
            .unwrap_or_default()
    })
}

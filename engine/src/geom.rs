//! Vectors, affine transforms and the triangle mesh type.
//!
//! World space is right-handed with Z up; lengths are millimetres.

use std::ops::{Add, Mul, Neg, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct V3 {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

pub const fn v3(x: f64, y: f64, z: f64) -> V3 {
    V3 { x, y, z }
}

impl V3 {
    pub fn dot(self, o: V3) -> f64 {
        self.x * o.x + self.y * o.y + self.z * o.z
    }
    pub fn cross(self, o: V3) -> V3 {
        v3(self.y * o.z - self.z * o.y, self.z * o.x - self.x * o.z, self.x * o.y - self.y * o.x)
    }
    pub fn len(self) -> f64 {
        self.dot(self).sqrt()
    }
    pub fn norm(self) -> V3 {
        let l = self.len();
        if l > 1e-15 {
            self * (1.0 / l)
        } else {
            v3(0.0, 0.0, 1.0)
        }
    }
    pub fn lerp(self, o: V3, t: f64) -> V3 {
        self + (o - self) * t
    }
    pub fn min(self, o: V3) -> V3 {
        v3(self.x.min(o.x), self.y.min(o.y), self.z.min(o.z))
    }
    pub fn max(self, o: V3) -> V3 {
        v3(self.x.max(o.x), self.y.max(o.y), self.z.max(o.z))
    }
}

impl Add for V3 {
    type Output = V3;
    fn add(self, o: V3) -> V3 {
        v3(self.x + o.x, self.y + o.y, self.z + o.z)
    }
}
impl Sub for V3 {
    type Output = V3;
    fn sub(self, o: V3) -> V3 {
        v3(self.x - o.x, self.y - o.y, self.z - o.z)
    }
}
impl Mul<f64> for V3 {
    type Output = V3;
    fn mul(self, k: f64) -> V3 {
        v3(self.x * k, self.y * k, self.z * k)
    }
}
impl Neg for V3 {
    type Output = V3;
    fn neg(self) -> V3 {
        v3(-self.x, -self.y, -self.z)
    }
}

/// Affine transform: rows of a 3x4 matrix, p' = M[..3] * p + M[3].
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Affine(pub [[f64; 4]; 3]);

impl Affine {
    pub const IDENTITY: Affine = Affine([[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0]]);

    pub fn apply(&self, p: V3) -> V3 {
        let m = &self.0;
        v3(
            m[0][0] * p.x + m[0][1] * p.y + m[0][2] * p.z + m[0][3],
            m[1][0] * p.x + m[1][1] * p.y + m[1][2] * p.z + m[1][3],
            m[2][0] * p.x + m[2][1] * p.y + m[2][2] * p.z + m[2][3],
        )
    }

    pub fn det(&self) -> f64 {
        let m = &self.0;
        m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
            + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0])
    }

    /// Rotation by `deg` around the line through `p` with direction `axis`.
    pub fn rotation(p: V3, axis: V3, deg: f64) -> Affine {
        let a = axis.norm();
        let (s, c) = deg.to_radians().sin_cos();
        let t = 1.0 - c;
        let r = [
            [t * a.x * a.x + c, t * a.x * a.y - s * a.z, t * a.x * a.z + s * a.y],
            [t * a.x * a.y + s * a.z, t * a.y * a.y + c, t * a.y * a.z - s * a.x],
            [t * a.x * a.z - s * a.y, t * a.y * a.z + s * a.x, t * a.z * a.z + c],
        ];
        let mut m = [[0.0; 4]; 3];
        for i in 0..3 {
            for j in 0..3 {
                m[i][j] = r[i][j];
            }
            // p - R p
            m[i][3] = [p.x, p.y, p.z][i] - (r[i][0] * p.x + r[i][1] * p.y + r[i][2] * p.z);
        }
        Affine(m)
    }
}

/// Plain triangle soup with counter-clockwise (outward) winding.
#[derive(Clone, Debug, Default)]
pub struct Mesh {
    pub tris: Vec<[V3; 3]>,
}

impl Mesh {
    pub fn bounds(&self) -> (V3, V3) {
        let big = f64::INFINITY;
        let mut lo = v3(big, big, big);
        let mut hi = v3(-big, -big, -big);
        for t in &self.tris {
            for p in t {
                lo = lo.min(*p);
                hi = hi.max(*p);
            }
        }
        (lo, hi)
    }

    /// Signed volume (positive for a closed, outward-facing mesh).
    pub fn volume(&self) -> f64 {
        self.tris.iter().map(|t| t[0].dot(t[1].cross(t[2]))).sum::<f64>() / 6.0
    }

    pub fn area(&self) -> f64 {
        self.tris.iter().map(|t| (t[1] - t[0]).cross(t[2] - t[0]).len()).sum::<f64>() / 2.0
    }

    pub fn transformed(&self, m: &Affine) -> Mesh {
        let flip = m.det() < 0.0;
        let tris = self
            .tris
            .iter()
            .map(|t| {
                let (a, b, c) = (m.apply(t[0]), m.apply(t[1]), m.apply(t[2]));
                if flip {
                    [a, c, b]
                } else {
                    [a, b, c]
                }
            })
            .collect();
        Mesh { tris }
    }

    pub fn flipped(mut self) -> Mesh {
        for t in &mut self.tris {
            t.swap(1, 2);
        }
        self
    }

    pub fn append(&mut self, o: &Mesh) {
        self.tris.extend_from_slice(&o.tris);
    }

    /// Make the mesh face outwards if it was built inside out.
    pub fn oriented(self) -> Mesh {
        if self.volume() < 0.0 {
            self.flipped()
        } else {
            self
        }
    }
}

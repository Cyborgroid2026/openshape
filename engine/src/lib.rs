//! OpenShape CAD geometry engine, exposed to the C# UI through a small C ABI (`osc_*`).
//!
//! Solids are immutable meshes kept in a registry and addressed by non-zero handles.

pub mod build;
pub mod csg;
pub mod geom;
pub mod io;
pub mod render;

use build::Frame;
use geom::{v3, Affine, Mesh, V3};
use std::collections::HashMap;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::sync::{Arc, Mutex, OnceLock};

pub struct Solid {
    pub mesh: Mesh,
    pub smooth: Vec<V3>,
}

#[derive(Default)]
struct Registry {
    next: u32,
    solids: HashMap<u32, Arc<Solid>>,
}

fn reg() -> &'static Mutex<Registry> {
    static R: OnceLock<Mutex<Registry>> = OnceLock::new();
    R.get_or_init(|| Mutex::new(Registry { next: 1, solids: HashMap::new() }))
}

pub fn add(mesh: Mesh) -> u32 {
    let smooth = render::smooth_normals(&mesh);
    let s = Arc::new(Solid { mesh, smooth });
    let mut r = reg().lock().unwrap();
    let h = r.next;
    r.next = r.next.wrapping_add(1).max(1);
    r.solids.insert(h, s);
    h
}

pub fn get(h: u32) -> Option<Arc<Solid>> {
    reg().lock().unwrap().solids.get(&h).cloned()
}

#[no_mangle]
pub extern "C" fn osc_free(h: u32) {
    reg().lock().unwrap().solids.remove(&h);
}

#[no_mangle]
pub extern "C" fn osc_count() -> u32 {
    reg().lock().unwrap().solids.len() as u32
}

// ------------------------------------------------------------------ primitives

#[no_mangle]
pub extern "C" fn osc_box(x0: f64, y0: f64, z0: f64, x1: f64, y1: f64, z1: f64) -> u32 {
    let (a, b) = (v3(x0, y0, z0), v3(x1, y1, z1));
    add(build::cuboid(a.min(b), a.max(b)))
}

#[no_mangle]
pub extern "C" fn osc_cylinder(x: f64, y: f64, z: f64, r: f64, h: f64) -> u32 {
    add(build::cylinder(v3(x, y, z), r.abs(), h).oriented())
}

#[no_mangle]
pub extern "C" fn osc_cone(x: f64, y: f64, z: f64, r1: f64, r2: f64, h: f64) -> u32 {
    add(build::cone(v3(x, y, z), r1, r2, h))
}

#[no_mangle]
pub extern "C" fn osc_sphere(x: f64, y: f64, z: f64, r: f64) -> u32 {
    add(build::sphere(v3(x, y, z), r.abs()))
}

#[no_mangle]
pub extern "C" fn osc_torus(x: f64, y: f64, z: f64, major: f64, minor: f64) -> u32 {
    add(build::torus(v3(x, y, z), major.abs(), minor.abs()))
}

// ------------------------------------------------------------------- sketches

unsafe fn frame(p: *const f64) -> Frame {
    let a = std::slice::from_raw_parts(p, 12);
    Frame { o: v3(a[0], a[1], a[2]), u: v3(a[3], a[4], a[5]), v: v3(a[6], a[7], a[8]), n: v3(a[9], a[10], a[11]) }
}

unsafe fn loops(pts: *const f64, counts: *const i32, n: i32) -> Vec<Vec<(f64, f64)>> {
    let counts = std::slice::from_raw_parts(counts, n.max(0) as usize);
    let total: usize = counts.iter().map(|&c| c.max(0) as usize).sum();
    let flat = std::slice::from_raw_parts(pts, total * 2);
    let mut out = Vec::new();
    let mut o = 0;
    for &c in counts {
        let c = c.max(0) as usize;
        out.push((0..c).map(|i| (flat[(o + i) * 2], flat[(o + i) * 2 + 1])).collect());
        o += c;
    }
    out
}

/// # Safety
/// `f` points to 12 doubles; `pts`/`counts` describe `n` loops.
#[no_mangle]
pub unsafe extern "C" fn osc_extrude(f: *const f64, pts: *const f64, counts: *const i32, n: i32, h0: f64, h1: f64) -> u32 {
    let m = build::extrude(&frame(f), &loops(pts, counts, n), h0, h1);
    if m.tris.is_empty() {
        0
    } else {
        add(m)
    }
}

/// # Safety
/// As for [`osc_extrude`].
#[no_mangle]
pub unsafe extern "C" fn osc_revolve(f: *const f64, pts: *const f64, counts: *const i32, n: i32, ax: f64, ay: f64, dx: f64, dy: f64, angle: f64) -> u32 {
    let m = build::revolve(&frame(f), &loops(pts, counts, n), (ax, ay), (dx, dy), angle);
    if m.tris.is_empty() {
        0
    } else {
        add(m)
    }
}

// ------------------------------------------------------------------ operations

/// 0 union, 1 subtract, 2 intersect. Returns 0 when an input is missing.
#[no_mangle]
pub extern "C" fn osc_boolean(a: u32, b: u32, op: u32) -> u32 {
    let (Some(a), Some(b)) = (get(a), get(b)) else { return 0 };
    let op = match op {
        0 => csg::Op::Union,
        1 => csg::Op::Subtract,
        _ => csg::Op::Intersect,
    };
    add(csg::boolean(&a.mesh, &b.mesh, op))
}

/// # Safety
/// `m` points to 12 doubles (3x4 row-major affine matrix).
#[no_mangle]
pub unsafe extern "C" fn osc_transform(h: u32, m: *const f64) -> u32 {
    let Some(s) = get(h) else { return 0 };
    let a = std::slice::from_raw_parts(m, 12);
    let t = Affine([[a[0], a[1], a[2], a[3]], [a[4], a[5], a[6], a[7]], [a[8], a[9], a[10], a[11]]]);
    add(s.mesh.transformed(&t))
}

/// Writes [volume, area, minx, miny, minz, maxx, maxy, maxz]; returns the triangle count (-1 if missing).
///
/// # Safety
/// `out` points to 8 doubles.
#[no_mangle]
pub unsafe extern "C" fn osc_info(h: u32, out: *mut f64) -> i32 {
    let Some(s) = get(h) else { return -1 };
    let o = std::slice::from_raw_parts_mut(out, 8);
    let (lo, hi) = s.mesh.bounds();
    o.copy_from_slice(&[s.mesh.volume(), s.mesh.area(), lo.x, lo.y, lo.z, hi.x, hi.y, hi.z]);
    s.mesh.tris.len() as i32
}

// ------------------------------------------------------------------- viewport

struct Pick {
    w: usize,
    h: usize,
    id: Vec<u32>,
    depth: Vec<f32>,
}

fn last_pick() -> &'static Mutex<Option<Pick>> {
    static P: OnceLock<Mutex<Option<Pick>>> = OnceLock::new();
    P.get_or_init(|| Mutex::new(None))
}

/// Render `n` solids. `cam` = eye, right, up, forward (3 each), focal, cx, cy.
/// `out` receives w*h RGBA8 pixels. opts: bit0 grid, bit1 edges.
///
/// # Safety
/// Arrays have `n` entries (colors 3n), `cam` 15 doubles, `out` w*h*4 bytes.
#[no_mangle]
pub unsafe extern "C" fn osc_render(handles: *const u32, colors: *const f32, flags: *const u32, n: i32, cam: *const f64, w: i32, h: i32, out: *mut u8, opts: u32, grid_step: f64) {
    let n = n.max(0) as usize;
    let (w, h) = (w.max(1) as usize, h.max(1) as usize);
    let hs = std::slice::from_raw_parts(handles, n);
    let cs = std::slice::from_raw_parts(colors, n * 3);
    let fs = std::slice::from_raw_parts(flags, n);
    let c = std::slice::from_raw_parts(cam, 15);
    let camera = render::Camera {
        eye: v3(c[0], c[1], c[2]),
        right: v3(c[3], c[4], c[5]),
        up: v3(c[6], c[7], c[8]),
        fwd: v3(c[9], c[10], c[11]),
        focal: c[12],
        cx: c[13],
        cy: c[14],
    };
    let solids: Vec<Option<Arc<Solid>>> = hs.iter().map(|&k| get(k)).collect();
    let empty = Solid { mesh: Mesh::default(), smooth: Vec::new() };
    let items: Vec<render::Item> = solids
        .iter()
        .enumerate()
        .map(|(i, s)| {
            let s = s.as_deref().unwrap_or(&empty);
            render::Item { mesh: &s.mesh, smooth: &s.smooth, color: [cs[i * 3], cs[i * 3 + 1], cs[i * 3 + 2]], flags: fs[i] }
        })
        .collect();
    let opt = render::Options { grid: opts & 1 != 0, grid_step, edges: opts & 2 != 0 };
    let fr = render::render(&items, &camera, w, h, &opt);
    let px = std::slice::from_raw_parts_mut(out, w * h * 4);
    for (i, col) in fr.color.iter().enumerate() {
        for k in 0..3 {
            px[i * 4 + k] = (col[k].clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
        }
        px[i * 4 + 3] = 255;
    }
    *last_pick().lock().unwrap() = Some(Pick { w, h, id: fr.id, depth: fr.depth });
}

/// Item index + 1 under pixel (x, y) of the last render (0 = background); `depth` gets the view depth.
///
/// # Safety
/// `depth` may be null or point to one double.
#[no_mangle]
pub unsafe extern "C" fn osc_pick(x: i32, y: i32, depth: *mut f64) -> u32 {
    let g = last_pick().lock().unwrap();
    let Some(p) = g.as_ref() else { return 0 };
    if x < 0 || y < 0 || x as usize >= p.w || y as usize >= p.h {
        return 0;
    }
    let o = y as usize * p.w + x as usize;
    if !depth.is_null() {
        *depth = p.depth[o] as f64;
    }
    p.id[o]
}

// ------------------------------------------------------------------------ files

/// format: 0 STL, 1 OBJ, 2 USDA. `names` holds n names separated by '\n'. Returns 1 on success.
///
/// # Safety
/// `handles` has n entries; `names` and `path` are NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn osc_export(handles: *const u32, names: *const c_char, n: i32, path: *const c_char, format: u32) -> i32 {
    let hs = std::slice::from_raw_parts(handles, n.max(0) as usize);
    let names = CStr::from_ptr(names).to_string_lossy().into_owned();
    let names: Vec<&str> = names.split('\n').collect();
    let path = CStr::from_ptr(path).to_string_lossy().into_owned();
    let solids: Vec<Arc<Solid>> = hs.iter().filter_map(|&h| get(h)).collect();
    let bodies: Vec<(&str, &Mesh)> = solids.iter().enumerate().map(|(i, s)| (*names.get(i).unwrap_or(&"Body"), &s.mesh)).collect();
    let Ok(file) = std::fs::File::create(&path) else { return 0 };
    let mut w = std::io::BufWriter::new(file);
    let r = match format {
        0 => io::write_stl(&bodies, &mut w),
        1 => io::write_obj(&bodies, &mut w),
        _ => io::write_usda(&bodies, &mut w),
    };
    use std::io::Write;
    (r.is_ok() && w.flush().is_ok()) as i32
}

/// Import an STL (format 0) or OBJ (1) file as one solid. Returns 0 on failure.
///
/// # Safety
/// `path` is NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn osc_import(path: *const c_char, format: u32) -> u32 {
    let path = CStr::from_ptr(path).to_string_lossy().into_owned();
    let Ok(data) = std::fs::read(&path) else { return 0 };
    let m = if format == 0 { io::read_stl(&data) } else { std::str::from_utf8(&data).ok().and_then(io::read_obj) };
    m.map_or(0, |m| add(m.oriented()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f64::consts::PI;

    fn vol(h: u32) -> f64 {
        let mut o = [0.0; 8];
        unsafe { osc_info(h, o.as_mut_ptr()) };
        o[0]
    }

    fn close(a: f64, b: f64, rel: f64) -> bool {
        (a - b).abs() <= rel * b.abs().max(1e-9)
    }

    #[test]
    fn primitives_have_expected_volume() {
        assert!(close(vol(osc_box(0.0, 0.0, 0.0, 10.0, 20.0, 30.0)), 6000.0, 1e-9));
        assert!(close(vol(osc_cylinder(0.0, 0.0, 0.0, 10.0, 20.0)), PI * 100.0 * 20.0, 0.01));
        assert!(close(vol(osc_sphere(0.0, 0.0, 0.0, 10.0)), 4.0 / 3.0 * PI * 1000.0, 0.02));
        assert!(close(vol(osc_cone(0.0, 0.0, 0.0, 10.0, 0.0, 30.0)), PI * 100.0 * 10.0, 0.01));
        assert!(close(vol(osc_torus(0.0, 0.0, 0.0, 20.0, 5.0)), 2.0 * PI * PI * 20.0 * 25.0, 0.02));
    }

    #[test]
    fn booleans() {
        let a = osc_box(0.0, 0.0, 0.0, 10.0, 10.0, 10.0);
        let b = osc_box(5.0, 5.0, 5.0, 15.0, 15.0, 15.0);
        assert!(close(vol(osc_boolean(a, b, 0)), 2000.0 - 125.0, 1e-6));
        assert!(close(vol(osc_boolean(a, b, 1)), 1000.0 - 125.0, 1e-6));
        assert!(close(vol(osc_boolean(a, b, 2)), 125.0, 1e-6));
        // plate with a through hole
        let plate = osc_box(-20.0, -20.0, 0.0, 20.0, 20.0, 5.0);
        let pin = osc_cylinder(0.0, 0.0, -1.0, 5.0, 7.0);
        let holed = osc_boolean(plate, pin, 1);
        assert!(close(vol(holed), 1600.0 * 5.0 - PI * 25.0 * 5.0, 0.01));
        // disjoint union keeps both
        let far = osc_box(100.0, 0.0, 0.0, 101.0, 1.0, 1.0);
        assert!(close(vol(osc_boolean(a, far, 0)), 1001.0, 1e-9));
    }

    fn xy() -> [f64; 12] {
        [0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]
    }

    #[test]
    fn extrude_region_with_hole() {
        let mut pts = vec![0.0, 0.0, 40.0, 0.0, 40.0, 20.0, 0.0, 20.0];
        pts.extend(build::circle((20.0, 10.0), 5.0, 64).iter().rev().flat_map(|p| [p.0, p.1]));
        let counts = [4, 64];
        let h = unsafe { osc_extrude(xy().as_ptr(), pts.as_ptr(), counts.as_ptr(), 2, 0.0, 10.0) };
        assert!(close(vol(h), 8000.0 - PI * 25.0 * 10.0, 0.01));
        // symmetric extrusion on the XZ plane (normal -Y) is still outward
        let xz = [0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -1.0, 0.0];
        let sq = [0.0, 0.0, 10.0, 0.0, 10.0, 10.0, 0.0, 10.0];
        let h = unsafe { osc_extrude(xz.as_ptr(), sq.as_ptr(), [4].as_ptr(), 1, -5.0, 5.0) };
        assert!(close(vol(h), 1000.0, 1e-9));
        let mut o = [0.0; 8];
        unsafe { osc_info(h, o.as_mut_ptr()) };
        assert!(close(o[3], -5.0, 1e-9) && close(o[6], 5.0, 1e-9));
    }

    #[test]
    fn revolve_rectangle_makes_a_tube() {
        // rectangle 5..10 x 0..20 around the sketch's vertical axis
        let r = [5.0, 0.0, 10.0, 0.0, 10.0, 20.0, 5.0, 20.0];
        let h = unsafe { osc_revolve(xy().as_ptr(), r.as_ptr(), [4].as_ptr(), 1, 0.0, 0.0, 0.0, 1.0, 360.0) };
        assert!(close(vol(h), PI * (100.0 - 25.0) * 20.0, 0.01));
        let half = unsafe { osc_revolve(xy().as_ptr(), r.as_ptr(), [4].as_ptr(), 1, 0.0, 0.0, 0.0, 1.0, -180.0) };
        assert!(close(vol(half), PI * (100.0 - 25.0) * 10.0, 0.01));
    }

    #[test]
    fn mirror_transform_keeps_outward_winding() {
        let a = osc_box(0.0, 0.0, 0.0, 1.0, 2.0, 3.0);
        let m = [-1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0];
        let b = unsafe { osc_transform(a, m.as_ptr()) };
        assert!(close(vol(b), 6.0, 1e-9));
    }

    #[test]
    fn files_round_trip() {
        let dir = std::env::temp_dir();
        let a = osc_box(0.0, 0.0, 0.0, 10.0, 10.0, 10.0);
        let names = std::ffi::CString::new("Body 1").unwrap();
        for (fmt, ext) in [(0u32, "stl"), (1, "obj"), (2, "usda")] {
            let p = std::ffi::CString::new(dir.join(format!("osc_test.{ext}")).to_string_lossy().as_bytes()).unwrap();
            assert_eq!(unsafe { osc_export([a].as_ptr(), names.as_ptr(), 1, p.as_ptr(), fmt) }, 1);
            if fmt < 2 {
                let b = unsafe { osc_import(p.as_ptr(), fmt) };
                assert!(close(vol(b), 1000.0, 1e-5), "{ext}");
            } else {
                let text = std::fs::read_to_string(dir.join("osc_test.usda")).unwrap();
                assert!(text.contains("metersPerUnit = 0.001") && text.contains("def Mesh \"Body_1\""));
            }
        }
    }

    #[test]
    fn render_and_pick() {
        let a = osc_box(-10.0, -10.0, 0.0, 10.0, 10.0, 20.0);
        // camera looking at the origin from +x,-y,+z
        let eye = v3(60.0, -60.0, 50.0);
        let fwd = (v3(0.0, 0.0, 10.0) - eye).norm();
        let right = fwd.cross(v3(0.0, 0.0, 1.0)).norm();
        let up = right.cross(fwd);
        let cam = [eye.x, eye.y, eye.z, right.x, right.y, right.z, up.x, up.y, up.z, fwd.x, fwd.y, fwd.z, 300.0, 100.0, 75.0];
        let mut px = vec![0u8; 200 * 150 * 4];
        unsafe { osc_render([a].as_ptr(), [0.7f32, 0.7, 0.7].as_ptr(), [0u32].as_ptr(), 1, cam.as_ptr(), 200, 150, px.as_mut_ptr(), 3, 10.0) };
        let mut d = 0.0;
        assert_eq!(unsafe { osc_pick(100, 75, &mut d) }, 1);
        assert!(d > 50.0 && d < 110.0);
        assert_eq!(unsafe { osc_pick(2, 2, std::ptr::null_mut()) }, 0);
    }
}

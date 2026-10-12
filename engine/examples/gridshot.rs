use openshape_engine::geom::v3;
use openshape_engine::render::*;
fn main() {
    let (yaw, pitch, dist): (f64, f64, f64) = (-55f64, 28f64, 260.0);
    let f = v3(-pitch.to_radians().cos() * yaw.to_radians().cos(), -pitch.to_radians().cos() * yaw.to_radians().sin(), -pitch.to_radians().sin()).norm();
    let eye = v3(0.0, 0.0, 0.0) - f * dist;
    let right = f.cross(v3(0.0, 0.0, 1.0)).norm();
    let up = right.cross(f);
    let w: usize = std::env::args().nth(2).unwrap().parse().unwrap(); let h: usize = std::env::args().nth(3).unwrap().parse().unwrap();
    let cam = Camera { eye, right, up, fwd: f, focal: h as f64 / 2.0 / (15f64.to_radians().tan()), cx: w as f64 / 2.0, cy: h as f64 / 2.0 };
    let fr = render(&[], &cam, w, h, &Options { grid: true, grid_step: 10.0, edges: true });
    let mut out = format!("P6 {w} {h} 255\n").into_bytes();
    for c in &fr.color { for k in 0..3 { out.push((c[k].clamp(0.0, 1.0) * 255.0) as u8); } }
    std::fs::write(std::env::args().nth(1).unwrap(), out).unwrap();
}

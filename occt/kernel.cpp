// OpenShape CAD B-rep kernel: a thin C ABI (ok_*) over OpenCASCADE for the C# UI.
// Shapes live in a registry and are addressed by non-zero handles.

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_GTransform.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeSolid.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepOffsetAPI_MakeThickSolid.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCone.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeRevol.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepPrimAPI_MakeTorus.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <GCPnts_TangentialDeflection.hxx>
#include <GC_MakeArcOfCircle.hxx>
#include <GProp_GProps.hxx>
#include <IGESControl_Reader.hxx>
#include <Interface_Static.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Poly_Triangulation.hxx>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <ShapeFix_Face.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeFix_Solid.hxx>
#include <Standard_Failure.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_ListOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <gp_Ax3.hxx>
#include <gp_Circ.hxx>
#include <gp_GTrsf.hxx>
#include <gp_Pln.hxx>

#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#define API extern "C" __attribute__((visibility("default")))

namespace {

std::mutex g_lock;
std::unordered_map<uint32_t, TopoDS_Shape> g_shapes;
uint32_t g_next = 1;
thread_local std::string g_error;
thread_local std::vector<double> g_buf;
thread_local std::vector<uint32_t> g_ids;
thread_local std::vector<int32_t> g_counts;

uint32_t put(const TopoDS_Shape& s) {
    if (s.IsNull()) return 0;
    std::lock_guard<std::mutex> l(g_lock);
    uint32_t h = g_next++;
    if (g_next == 0) g_next = 1;
    g_shapes[h] = s;
    return h;
}

bool get(uint32_t h, TopoDS_Shape& out) {
    std::lock_guard<std::mutex> l(g_lock);
    auto it = g_shapes.find(h);
    if (it == g_shapes.end()) {
        g_error = "형상이 없습니다";
        return false;
    }
    out = it->second;
    return true;
}

template <typename F>
uint32_t guarded(F f) {
    try {
        g_error.clear();
        return f();
    } catch (Standard_Failure& e) {
        g_error = e.GetMessageString() ? e.GetMessageString() : "OpenCASCADE 오류";
    } catch (std::exception& e) {
        g_error = e.what();
    } catch (...) {
        g_error = "알 수 없는 형상 오류";
    }
    return 0;
}

struct Frame {
    gp_Pnt o;
    gp_Dir u, v, n;
};

Frame frame(const double* f) {
    Frame r;
    r.o = gp_Pnt(f[0], f[1], f[2]);
    r.u = gp_Dir(f[3], f[4], f[5]);
    r.v = gp_Dir(f[6], f[7], f[8]);
    r.n = gp_Dir(f[9], f[10], f[11]);
    return r;
}

gp_Pnt at(const Frame& f, double x, double y) {
    return f.o.Translated(gp_Vec(f.u) * x + gp_Vec(f.v) * y);
}

double diag(const TopoDS_Shape& s) {
    Bnd_Box b;
    BRepBndLib::Add(s, b);
    if (b.IsVoid()) return 1.0;
    return std::sqrt(b.SquareExtent());
}

/// Nearest sub-shape of a kind to a point (index into the map, 1-based), or 0.
int nearest(const TopTools_IndexedMapOfShape& map, const gp_Pnt& p, double tol) {
    TopoDS_Vertex v = BRepBuilderAPI_MakeVertex(p);
    int best = 0;
    double bd = tol;
    for (int i = 1; i <= map.Extent(); i++) {
        BRepExtrema_DistShapeShape d(v, map(i));
        if (d.IsDone() && d.Value() < bd) {
            bd = d.Value();
            best = i;
        }
    }
    return best;
}

TopoDS_Shape fix(const TopoDS_Shape& s) {
    BRepCheck_Analyzer check(s);
    if (check.IsValid()) return s;
    Handle(ShapeFix_Shape) sf = new ShapeFix_Shape(s);
    sf->Perform();
    return sf->Shape();
}

}  // namespace

// ------------------------------------------------------------------- basics

API void ok_init() {
    // silence the default console printer (STEP statistics etc.)
    Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_Printer));
}

API void ok_free(uint32_t h) {
    std::lock_guard<std::mutex> l(g_lock);
    g_shapes.erase(h);
}

API uint32_t ok_count() {
    std::lock_guard<std::mutex> l(g_lock);
    return (uint32_t)g_shapes.size();
}

API int ok_error(char* buf, int len) {
    int n = (int)g_error.size();
    if (buf && len > 0) {
        int k = std::min(n, len - 1);
        std::memcpy(buf, g_error.data(), k);
        buf[k] = 0;
    }
    return n;
}

// ------------------------------------------------------------------- primitives

API uint32_t ok_box(double x0, double y0, double z0, double x1, double y1, double z1) {
    return guarded([&] {
        return put(BRepPrimAPI_MakeBox(gp_Pnt(std::min(x0, x1), std::min(y0, y1), std::min(z0, z1)),
                                       gp_Pnt(std::max(x0, x1), std::max(y0, y1), std::max(z0, z1))).Shape());
    });
}

API uint32_t ok_cylinder(double x, double y, double z, double r, double h) {
    return guarded([&] { return put(BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(x, y, z), gp::DZ()), r, h).Shape()); });
}

API uint32_t ok_cone(double x, double y, double z, double r1, double r2, double h) {
    return guarded([&] {
        if (std::fabs(r1 - r2) < 1e-9) return put(BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(x, y, z), gp::DZ()), r1, h).Shape());
        return put(BRepPrimAPI_MakeCone(gp_Ax2(gp_Pnt(x, y, z), gp::DZ()), r1, r2, h).Shape());
    });
}

API uint32_t ok_sphere(double x, double y, double z, double r) {
    return guarded([&] { return put(BRepPrimAPI_MakeSphere(gp_Pnt(x, y, z), r).Shape()); });
}

API uint32_t ok_torus(double x, double y, double z, double major, double minor) {
    return guarded([&] { return put(BRepPrimAPI_MakeTorus(gp_Ax2(gp_Pnt(x, y, z), gp::DZ()), major, minor).Shape()); });
}

// ------------------------------------------------------------------- sketch profiles

/// Planar face from loops of sketch curves (first loop outer, the rest holes).
/// kinds: 0 line (x1 y1 x2 y2), 1 three-point arc (x1 y1 xm ym x2 y2), 2 circle (cx cy r).
API uint32_t ok_profile_face(const double* fr, const int32_t* kinds, const double* data, const int32_t* loopCounts, int nloops) {
    return guarded([&]() -> uint32_t {
        Frame f = frame(fr);
        gp_Ax3 ax(f.o, f.n, f.u);
        std::vector<TopoDS_Wire> wires;
        int k = 0;
        const double* d = data;
        for (int l = 0; l < nloops; l++) {
            BRepBuilderAPI_MakeWire mw;
            for (int i = 0; i < loopCounts[l]; i++, k++) {
                switch (kinds[k]) {
                    case 0: {
                        gp_Pnt a = at(f, d[0], d[1]), b = at(f, d[2], d[3]);
                        d += 4;
                        if (a.Distance(b) > 1e-9) mw.Add(BRepBuilderAPI_MakeEdge(a, b).Edge());
                        break;
                    }
                    case 1: {
                        gp_Pnt a = at(f, d[0], d[1]), m = at(f, d[2], d[3]), b = at(f, d[4], d[5]);
                        d += 6;
                        GC_MakeArcOfCircle arc(a, m, b);
                        if (arc.IsDone()) mw.Add(BRepBuilderAPI_MakeEdge(arc.Value()).Edge());
                        else mw.Add(BRepBuilderAPI_MakeEdge(a, b).Edge());
                        break;
                    }
                    default: {
                        gp_Circ c(gp_Ax2(at(f, d[0], d[1]), f.n, f.u), d[2]);
                        d += 3;
                        mw.Add(BRepBuilderAPI_MakeEdge(c).Edge());
                        break;
                    }
                }
            }
            if (!mw.IsDone()) {
                g_error = "프로파일 곡선이 닫혀 있지 않습니다";
                return 0;
            }
            wires.push_back(mw.Wire());
        }
        if (wires.empty()) return 0;
        BRepBuilderAPI_MakeFace mf(gp_Pln(ax), wires[0], true);
        for (size_t i = 1; i < wires.size(); i++) mf.Add(wires[i]);
        if (!mf.IsDone()) {
            g_error = "프로파일에서 면을 만들 수 없습니다";
            return 0;
        }
        Handle(ShapeFix_Face) sf = new ShapeFix_Face(mf.Face());
        sf->FixOrientation();
        sf->Perform();
        return put(sf->Face());
    });
}

/// Extrude a face along (dx, dy, dz) from height h0 to h1.
API uint32_t ok_prism(uint32_t face, double dx, double dy, double dz, double h0, double h1) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape f;
        if (!get(face, f)) return 0;
        gp_Vec n(dx, dy, dz);
        n.Normalize();
        gp_Trsf t;
        t.SetTranslation(n * h0);
        TopoDS_Shape start = BRepBuilderAPI_Transform(f, t, true).Shape();
        return put(BRepPrimAPI_MakePrism(start, n * (h1 - h0)).Shape());
    });
}

API uint32_t ok_revol(uint32_t face, double ox, double oy, double oz, double dx, double dy, double dz, double deg) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape f;
        if (!get(face, f)) return 0;
        BRepPrimAPI_MakeRevol r(f, gp_Ax1(gp_Pnt(ox, oy, oz), gp_Dir(dx, dy, dz)), deg * M_PI / 180.0);
        if (!r.IsDone()) {
            g_error = "회전체를 만들 수 없습니다 (프로파일이 축을 가로지르는지 확인하세요)";
            return 0;
        }
        return put(r.Shape());
    });
}

// ------------------------------------------------------------------- operations

/// 0 union, 1 subtract, 2 intersect.
API uint32_t ok_bool(uint32_t a, uint32_t b, int op) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape sa, sb;
        if (!get(a, sa) || !get(b, sb)) return 0;
        TopoDS_Shape r;
        auto run = [&](BRepAlgoAPI_BooleanOperation& alg) {
            alg.Build();
            if (alg.HasErrors() || !alg.IsDone()) {
                g_error = "불리언 연산에 실패했습니다";
                return false;
            }
            alg.SimplifyResult();
            r = alg.Shape();
            return true;
        };
        bool ok;
        if (op == 0) {
            BRepAlgoAPI_Fuse f(sa, sb);
            ok = run(f);
        } else if (op == 1) {
            BRepAlgoAPI_Cut c(sa, sb);
            ok = run(c);
        } else {
            BRepAlgoAPI_Common c(sa, sb);
            ok = run(c);
        }
        return ok ? put(r) : 0;
    });
}

/// m: 3x4 row-major affine matrix.
API uint32_t ok_transform(uint32_t h, const double* m) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        try {
            gp_Trsf t;
            t.SetValues(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
            return put(BRepBuilderAPI_Transform(s, t, true).Shape());
        } catch (Standard_Failure&) {
            gp_GTrsf g;
            for (int i = 0; i < 3; i++) {
                for (int j = 0; j < 3; j++) g.SetValue(i + 1, j + 1, m[i * 4 + j]);
                g.SetValue(i + 1, 4, m[i * 4 + 3]);
            }
            return put(BRepBuilderAPI_GTransform(s, g, true).Shape());
        }
    });
}

/// Fillet (kind 0) or chamfer (kind 1) the edges nearest to the given points (all edges when n == 0).
API uint32_t ok_blend(uint32_t h, int kind, const double* pts, int n, double size) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        TopTools_IndexedMapOfShape edges;
        TopExp::MapShapes(s, TopAbs_EDGE, edges);
        std::vector<int> pick;
        if (n == 0)
            for (int i = 1; i <= edges.Extent(); i++) pick.push_back(i);
        double tol = 0.5 + 1e-3 * diag(s);
        for (int i = 0; i < n; i++) {
            int e = nearest(edges, gp_Pnt(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]), tol);
            if (e == 0) {
                g_error = "선택한 모서리를 찾을 수 없습니다 (앞의 피쳐가 바뀌었을 수 있습니다)";
                return 0;
            }
            pick.push_back(e);
        }
        TopoDS_Shape r;
        if (kind == 0) {
            BRepFilletAPI_MakeFillet f(s);
            for (int e : pick) f.Add(size, TopoDS::Edge(edges(e)));
            f.Build();
            if (!f.IsDone()) {
                g_error = "모깎기를 만들 수 없습니다 (반지름이 너무 클 수 있습니다)";
                return 0;
            }
            r = f.Shape();
        } else {
            BRepFilletAPI_MakeChamfer c(s);
            for (int e : pick) c.Add(size, TopoDS::Edge(edges(e)));
            c.Build();
            if (!c.IsDone()) {
                g_error = "모따기를 만들 수 없습니다 (거리가 너무 클 수 있습니다)";
                return 0;
            }
            r = c.Shape();
        }
        return put(r);
    });
}

/// Hollow a solid, removing the faces nearest to the points; negative thickness goes inward.
API uint32_t ok_shell(uint32_t h, const double* pts, int n, double thickness) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        TopTools_ListOfShape remove;
        double tol = 0.5 + 1e-3 * diag(s);
        for (int i = 0; i < n; i++) {
            int f = nearest(faces, gp_Pnt(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]), tol);
            if (f == 0) {
                g_error = "선택한 면을 찾을 수 없습니다";
                return 0;
            }
            remove.Append(faces(f));
        }
        BRepOffsetAPI_MakeThickSolid t;
        t.MakeThickSolidByJoin(s, remove, thickness, 1e-3);
        t.Build();
        if (!t.IsDone()) {
            g_error = "쉘을 만들 수 없습니다 (두께를 줄여 보세요)";
            return 0;
        }
        return put(t.Shape());
    });
}

/// [volume, area, minx, miny, minz, maxx, maxy, maxz]; returns the solid count (-1 if missing).
API int ok_info(uint32_t h, double* out) {
    TopoDS_Shape s;
    if (!get(h, s)) return -1;
    try {
        GProp_GProps v, a;
        BRepGProp::VolumeProperties(s, v);
        BRepGProp::SurfaceProperties(s, a);
        Bnd_Box b;
        BRepBndLib::AddOptimal(s, b, false, false);
        double x0 = 0, y0 = 0, z0 = 0, x1 = 0, y1 = 0, z1 = 0;
        if (!b.IsVoid()) b.Get(x0, y0, z0, x1, y1, z1);
        double r[8] = {v.Mass(), a.Mass(), x0, y0, z0, x1, y1, z1};
        std::memcpy(out, r, sizeof r);
        int n = 0;
        for (TopExp_Explorer e(s, TopAbs_SOLID); e.More(); e.Next()) n++;
        return n;
    } catch (...) {
        return -1;
    }
}

// ------------------------------------------------------------------- display data

/// Triangulate; returns the triangle count. Fetch with ok_mesh_copy.
API int ok_mesh(uint32_t h, double relDeflection) {
    TopoDS_Shape s;
    if (!get(h, s)) return -1;
    g_buf.clear();
    g_ids.clear();
    try {
        double defl = std::max(1e-3, relDeflection * diag(s));
        BRepMesh_IncrementalMesh mesh(s, defl, false, 0.35, true);
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        for (int i = 1; i <= faces.Extent(); i++) {
            const TopoDS_Face& face = TopoDS::Face(faces(i));
            TopLoc_Location loc;
            Handle(Poly_Triangulation) tri = BRep_Tool::Triangulation(face, loc);
            if (tri.IsNull()) continue;
            bool rev = face.Orientation() == TopAbs_REVERSED;
            const gp_Trsf& t = loc.Transformation();
            for (int k = 1; k <= tri->NbTriangles(); k++) {
                int a, b, c;
                tri->Triangle(k).Get(a, b, c);
                if (rev) std::swap(b, c);
                for (int idx : {a, b, c}) {
                    gp_Pnt p = tri->Node(idx).Transformed(t);
                    g_buf.push_back(p.X());
                    g_buf.push_back(p.Y());
                    g_buf.push_back(p.Z());
                }
                g_ids.push_back((uint32_t)i);
            }
        }
    } catch (...) {
        g_error = "형상을 삼각형으로 나눌 수 없습니다";
        return -1;
    }
    return (int)g_ids.size();
}

API void ok_mesh_copy(double* xyz, uint32_t* faceIds) {
    std::memcpy(xyz, g_buf.data(), g_buf.size() * sizeof(double));
    std::memcpy(faceIds, g_ids.data(), g_ids.size() * sizeof(uint32_t));
}

/// Polylines of every edge; returns the edge count. Fetch with ok_edges_copy (points, per-edge counts).
API int ok_edges(uint32_t h, double relDeflection) {
    TopoDS_Shape s;
    if (!get(h, s)) return -1;
    g_buf.clear();
    g_counts.clear();
    try {
        double defl = std::max(1e-3, relDeflection * diag(s));
        TopTools_IndexedMapOfShape edges;
        TopExp::MapShapes(s, TopAbs_EDGE, edges);
        for (int i = 1; i <= edges.Extent(); i++) {
            const TopoDS_Edge& e = TopoDS::Edge(edges(i));
            int n = 0;
            if (!BRep_Tool::Degenerated(e)) {
                BRepAdaptor_Curve c(e);
                GCPnts_TangentialDeflection pts(c, 0.2, defl);
                for (int k = 1; k <= pts.NbPoints(); k++) {
                    gp_Pnt p = pts.Value(k);
                    g_buf.push_back(p.X());
                    g_buf.push_back(p.Y());
                    g_buf.push_back(p.Z());
                    n++;
                }
            }
            g_counts.push_back(n);
        }
    } catch (...) {
        return -1;
    }
    return (int)g_counts.size();
}

API int ok_edges_points() { return (int)(g_buf.size() / 3); }

API void ok_edges_copy(double* xyz, int32_t* counts) {
    std::memcpy(xyz, g_buf.data(), g_buf.size() * sizeof(double));
    std::memcpy(counts, g_counts.data(), g_counts.size() * sizeof(int32_t));
}

/// Sketch frame of the planar face nearest to (x, y, z): origin (world origin projected), u, v, outward n.
API int ok_face_frame(uint32_t h, double x, double y, double z, double* out) {
    TopoDS_Shape s;
    if (!get(h, s)) return 0;
    try {
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        int f = nearest(faces, gp_Pnt(x, y, z), 0.5 + 1e-3 * diag(s));
        if (f == 0) return 0;
        const TopoDS_Face& face = TopoDS::Face(faces(f));
        BRepAdaptor_Surface surf(face, false);
        if (surf.GetType() != GeomAbs_Plane) {
            g_error = "평면인 면만 스케치할 수 있습니다";
            return 0;
        }
        gp_Ax3 ax = surf.Plane().Position();
        gp_Dir n = ax.Direction();
        if (face.Orientation() == TopAbs_REVERSED) n.Reverse();
        gp_Dir u = ax.XDirection();
        if (std::fabs(u.Dot(n)) > 1e-9) u = gp_Dir(gp_Vec(u) - gp_Vec(n) * u.Dot(n));
        gp_Dir v = n.Crossed(u);
        gp_Pnt loc = ax.Location();
        double d = gp_Vec(gp_Pnt(0, 0, 0), loc).Dot(gp_Vec(n));
        gp_Pnt o = gp_Pnt(0, 0, 0).Translated(gp_Vec(n) * d);
        double r[12] = {o.X(), o.Y(), o.Z(), u.X(), u.Y(), u.Z(), v.X(), v.Y(), v.Z(), n.X(), n.Y(), n.Z()};
        std::memcpy(out, r, sizeof r);
        return f;
    } catch (...) {
        return 0;
    }
}

/// Joint origin on the face nearest to (x, y, z): planar faces give their centre and normal,
/// cylinders/cones their axis (at the picked height), spheres their centre. out = origin, axis.
API int ok_joint_frame(uint32_t h, double x, double y, double z, double* out) {
    TopoDS_Shape s;
    if (!get(h, s)) return 0;
    try {
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        gp_Pnt pick(x, y, z);
        int f = nearest(faces, pick, 0.5 + 1e-3 * diag(s));
        if (f == 0) return 0;
        const TopoDS_Face& face = TopoDS::Face(faces(f));
        BRepAdaptor_Surface surf(face, true);
        gp_Pnt o;
        gp_Dir d;
        switch (surf.GetType()) {
            case GeomAbs_Plane: {
                GProp_GProps g;
                BRepGProp::SurfaceProperties(face, g);
                o = g.CentreOfMass();
                d = surf.Plane().Axis().Direction();
                if (face.Orientation() == TopAbs_REVERSED) d.Reverse();
                break;
            }
            case GeomAbs_Cylinder:
            case GeomAbs_Cone: {
                gp_Ax1 ax = surf.GetType() == GeomAbs_Cylinder ? surf.Cylinder().Axis() : surf.Cone().Axis();
                d = ax.Direction();
                gp_Vec v(ax.Location(), pick);
                o = ax.Location().Translated(gp_Vec(d) * v.Dot(gp_Vec(d)));
                break;
            }
            case GeomAbs_Sphere:
                o = surf.Sphere().Location();
                d = gp::DZ();
                break;
            case GeomAbs_Torus:
                o = surf.Torus().Location();
                d = surf.Torus().Axis().Direction();
                break;
            default:
                o = pick;
                d = gp::DZ();
                break;
        }
        double r[6] = {o.X(), o.Y(), o.Z(), d.X(), d.Y(), d.Z()};
        std::memcpy(out, r, sizeof r);
        return (int)surf.GetType() + 1;
    } catch (...) {
        return 0;
    }
}

// ------------------------------------------------------------------- files

/// Read STEP (kind 0) or IGES (1); returns a handle to everything read.
API uint32_t ok_read(const char* path, int kind) {
    return guarded([&]() -> uint32_t {
        if (kind == 0) {
            STEPControl_Reader r;
            if (r.ReadFile(path) != IFSelect_RetDone) {
                g_error = "STEP 파일을 읽을 수 없습니다";
                return 0;
            }
            r.TransferRoots();
            return put(fix(r.OneShape()));
        }
        IGESControl_Reader r;
        if (r.ReadFile(path) != IFSelect_RetDone) {
            g_error = "IGES 파일을 읽을 수 없습니다";
            return 0;
        }
        r.TransferRoots();
        return put(fix(r.OneShape()));
    });
}

/// Split a shape into its solids (shells are closed into solids when possible). Returns the count.
API int ok_solids(uint32_t h, uint32_t* out, int max) {
    TopoDS_Shape s;
    if (!get(h, s)) return -1;
    int n = 0;
    try {
        for (TopExp_Explorer e(s, TopAbs_SOLID); e.More() && n < max; e.Next()) out[n++] = put(e.Current());
        if (n == 0)
            for (TopExp_Explorer e(s, TopAbs_SHELL); e.More() && n < max; e.Next()) {
                BRepBuilderAPI_MakeSolid ms(TopoDS::Shell(e.Current()));
                if (!ms.IsDone()) continue;
                Handle(ShapeFix_Solid) fs = new ShapeFix_Solid(ms.Solid());
                fs->Perform();
                out[n++] = put(fs->Solid());
            }
    } catch (...) {
    }
    return n;
}

API int ok_write_step(const uint32_t* hs, int n, const char* path) {
    try {
        g_error.clear();
        TopoDS_Compound c;
        BRep_Builder b;
        b.MakeCompound(c);
        for (int i = 0; i < n; i++) {
            TopoDS_Shape s;
            if (get(hs[i], s)) b.Add(c, s);
        }
        Interface_Static::SetCVal("write.step.unit", "MM");
        STEPControl_Writer w;
        if (w.Transfer(c, STEPControl_AsIs) != IFSelect_RetDone) return 0;
        return w.Write(path) == IFSelect_RetDone ? 1 : 0;
    } catch (...) {
        g_error = "STEP 파일을 쓸 수 없습니다";
        return 0;
    }
}

/// Closed triangle mesh (9 doubles per triangle) to a solid by sewing.
API uint32_t ok_from_mesh(const double* tris, int n) {
    return guarded([&]() -> uint32_t {
        BRepBuilderAPI_Sewing sew(1e-6);
        for (int i = 0; i < n; i++) {
            const double* t = tris + i * 9;
            gp_Pnt a(t[0], t[1], t[2]), b(t[3], t[4], t[5]), c(t[6], t[7], t[8]);
            if (a.Distance(b) < 1e-12 || b.Distance(c) < 1e-12 || a.Distance(c) < 1e-12) continue;
            BRepBuilderAPI_MakePolygon p(a, b, c, true);
            BRepBuilderAPI_MakeFace f(p.Wire(), true);
            if (f.IsDone()) sew.Add(f.Face());
        }
        sew.Perform();
        TopoDS_Shape sewn = sew.SewedShape();
        for (TopExp_Explorer e(sewn, TopAbs_SHELL); e.More(); e.Next()) {
            BRepBuilderAPI_MakeSolid ms(TopoDS::Shell(e.Current()));
            if (!ms.IsDone()) continue;
            Handle(ShapeFix_Solid) fs = new ShapeFix_Solid(ms.Solid());
            fs->Perform();
            return put(fs->Solid());
        }
        return put(sewn);
    });
}

// ------------------------------------------------------------------- sketch solver (SolveSpace)

#include <slvs.h>

/// Solve a 2D sketch in place.
/// params: values (updated); pts: (paramX, paramY) per point; ents: (type, a, b, c) per entity,
///   type 0 line(ptA, ptB), 1 circle(center pt, radius param), 2 arc(center, start, end);
/// cons: (type, value, ptA, ptB, entA, entB) per constraint (-1 = unused);
/// dragged: param indices the solver should move as little as possible.
/// Returns the SolveSpace result (0 = okay) and writes the remaining degrees of freedom.
API int ok_solve(double* params, int nparams, const int32_t* pts, int npts, const int32_t* ents, int nents,
                 const double* cons, int ncons, const int32_t* dragged, int ndragged, int32_t* dof) {
    std::vector<Slvs_Param> P;
    std::vector<Slvs_Entity> E;
    std::vector<Slvs_Constraint> C;
    const Slvs_hGroup g1 = 1, g2 = 2;
    // workplane: origin + identity orientation, fixed (group 1)
    P.push_back(Slvs_MakeParam(1, g1, 0));
    P.push_back(Slvs_MakeParam(2, g1, 0));
    P.push_back(Slvs_MakeParam(3, g1, 0));
    P.push_back(Slvs_MakeParam(4, g1, 1));
    P.push_back(Slvs_MakeParam(5, g1, 0));
    P.push_back(Slvs_MakeParam(6, g1, 0));
    P.push_back(Slvs_MakeParam(7, g1, 0));
    E.push_back(Slvs_MakePoint3d(1, g1, 1, 2, 3));
    E.push_back(Slvs_MakeNormal3d(2, g1, 4, 5, 6, 7));
    E.push_back(Slvs_MakeWorkplane(3, g1, 1, 2));
    const Slvs_hEntity wp = 3, normal = 2;
    const uint32_t PB = 100;            // param handle = PB + index
    const uint32_t PTB = 1000;          // point entity handle = PTB + index
    const uint32_t EB = 100000;         // entity handle = EB + index * 2 (+1 for a circle's radius)
    for (int i = 0; i < nparams; i++) P.push_back(Slvs_MakeParam(PB + i, g2, params[i]));
    for (int i = 0; i < npts; i++) E.push_back(Slvs_MakePoint2d(PTB + i, g2, wp, PB + pts[i * 2], PB + pts[i * 2 + 1]));
    for (int i = 0; i < nents; i++) {
        const int32_t* e = ents + i * 4;
        uint32_t h = EB + i * 2;
        if (e[0] == 0) E.push_back(Slvs_MakeLineSegment(h, g2, wp, PTB + e[1], PTB + e[2]));
        else if (e[0] == 1) {
            E.push_back(Slvs_MakeDistance(h + 1, g2, wp, PB + e[2]));
            E.push_back(Slvs_MakeCircle(h, g2, wp, PTB + e[1], normal, h + 1));
        } else E.push_back(Slvs_MakeArcOfCircle(h, g2, wp, normal, PTB + e[1], PTB + e[2], PTB + e[3]));
    }
    auto pt = [&](double v) -> Slvs_hEntity { return v < 0 ? 0 : PTB + (uint32_t)v; };
    auto en = [&](double v) -> Slvs_hEntity { return v < 0 ? 0 : EB + (uint32_t)v * 2; };
    for (int i = 0; i < ncons; i++) {
        const double* c = cons + i * 6;
        int type;
        switch ((int)c[0]) {
            case 0: type = SLVS_C_POINTS_COINCIDENT; break;
            case 1: type = SLVS_C_HORIZONTAL; break;
            case 2: type = SLVS_C_VERTICAL; break;
            case 3: type = SLVS_C_PT_PT_DISTANCE; break;
            case 4: type = SLVS_C_DIAMETER; break;
            case 5: type = SLVS_C_PARALLEL; break;
            case 6: type = SLVS_C_PERPENDICULAR; break;
            case 7: type = SLVS_C_EQUAL_LENGTH_LINES; break;
            case 8: type = SLVS_C_ANGLE; break;
            case 9: type = SLVS_C_EQUAL_RADIUS; break;
            case 10: type = SLVS_C_WHERE_DRAGGED; break;
            case 11: type = SLVS_C_PT_ON_LINE; break;
            case 12: type = SLVS_C_PT_ON_CIRCLE; break;
            case 13: type = SLVS_C_AT_MIDPOINT; break;
            default: continue;
        }
        C.push_back(Slvs_MakeConstraint(i + 1, g2, type, wp, c[1], pt(c[2]), pt(c[3]), en(c[4]), en(c[5])));
    }
    Slvs_System sys = {};
    sys.param = P.data();
    sys.params = (int)P.size();
    sys.entity = E.data();
    sys.entities = (int)E.size();
    sys.constraint = C.data();
    sys.constraints = (int)C.size();
    std::vector<Slvs_hConstraint> failed(C.size() + 1);
    sys.failed = failed.data();
    sys.faileds = (int)failed.size();
    sys.calculateFaileds = 1;
    for (int i = 0; i < ndragged && i < 4; i++) sys.dragged[i] = PB + dragged[i];
    Slvs_Solve(&sys, g2);
    if (sys.result == SLVS_RESULT_OKAY)
        for (int i = 0; i < nparams; i++) params[i] = P[7 + i].val;
    if (dof) *dof = sys.dof;
    return sys.result;
}

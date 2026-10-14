// OpenShape CAD B-rep kernel: a thin C ABI (ok_*) over OpenCASCADE for the C# UI.
// Shapes live in a registry and are addressed by non-zero handles.

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepAlgoAPI_Splitter.hxx>
#include <BRepLib.hxx>
#include <BRepOffsetAPI_DraftAngle.hxx>
#include <BRepOffsetAPI_MakeOffset.hxx>
#include <BRepOffsetAPI_MakePipeShell.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <ElSLib.hxx>
#include <GCE2d_MakeSegment.hxx>
#include <Geom_CylindricalSurface.hxx>
#include <HLRAlgo_Projector.hxx>
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
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
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <BRepTools.hxx>
#include <BRepFill_Filling.hxx>
#include <BRepOffsetAPI_MakePipe.hxx>
#include <BRepOffsetAPI_ThruSections.hxx>
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

#include <array>
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

namespace {
/// Wire from sketch curves (kinds/data as for ok_profile_face), advancing the cursors.
bool build_wire(const Frame& f, const int32_t*& kinds, const double*& d, int count, TopoDS_Wire& out) {
    BRepBuilderAPI_MakeWire mw;
    for (int i = 0; i < count; i++, kinds++) {
        switch (*kinds) {
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
    if (!mw.IsDone()) return false;
    out = mw.Wire();
    return true;
}
}  // namespace

/// Open or closed wire from sketch curves.
API uint32_t ok_wire(const double* fr, const int32_t* kinds, const double* data, int count) {
    return guarded([&]() -> uint32_t {
        Frame f = frame(fr);
        TopoDS_Wire w;
        if (!build_wire(f, kinds, data, count, w)) {
            g_error = "곡선이 이어져 있지 않습니다";
            return 0;
        }
        return put(w);
    });
}

/// Loft through closed profiles (faces or wires) in order.
API uint32_t ok_loft(const uint32_t* hs, int n, int solid) {
    return guarded([&]() -> uint32_t {
        BRepOffsetAPI_ThruSections loft(solid != 0, false, 1e-6);
        for (int i = 0; i < n; i++) {
            TopoDS_Shape s;
            if (!get(hs[i], s)) return 0;
            if (s.ShapeType() == TopAbs_FACE) loft.AddWire(BRepTools::OuterWire(TopoDS::Face(s)));
            else if (s.ShapeType() == TopAbs_WIRE) loft.AddWire(TopoDS::Wire(s));
            else {
                g_error = "로프트에는 닫힌 프로파일이 필요합니다";
                return 0;
            }
        }
        loft.CheckCompatibility(false);
        loft.Build();
        if (!loft.IsDone()) {
            g_error = "로프트를 만들 수 없습니다";
            return 0;
        }
        return put(loft.Shape());
    });
}

/// Sweep a profile (face or wire) along a path wire.
API uint32_t ok_sweep(uint32_t profile, uint32_t path) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape pr, pa;
        if (!get(profile, pr) || !get(path, pa)) return 0;
        BRepOffsetAPI_MakePipe pipe(TopoDS::Wire(pa), pr);
        pipe.Build();
        if (!pipe.IsDone()) {
            g_error = "스윕을 만들 수 없습니다 (경로가 프로파일을 지나는지 확인하세요)";
            return 0;
        }
        return put(pipe.Shape());
    });
}

/// Planar face (or filled surface) bounded by a closed wire.
API uint32_t ok_patch(uint32_t wire) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape w;
        if (!get(wire, w)) return 0;
        BRepBuilderAPI_MakeFace mf(TopoDS::Wire(w), true);
        if (mf.IsDone()) return put(mf.Face());
        BRepFill_Filling fill;
        for (TopExp_Explorer e(w, TopAbs_EDGE); e.More(); e.Next()) fill.Add(TopoDS::Edge(e.Current()), GeomAbs_C0);
        fill.Build();
        if (!fill.IsDone()) {
            g_error = "패치를 만들 수 없습니다";
            return 0;
        }
        return put(fill.Face());
    });
}

/// Thicken a surface (or offset a solid) into a solid.
API uint32_t ok_thicken(uint32_t h, double t) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        BRepOffsetAPI_MakeThickSolid th;
        th.MakeThickSolidBySimple(s, t);
        th.Build();
        if (!th.IsDone()) {
            g_error = "두껍게 만들 수 없습니다";
            return 0;
        }
        TopoDS_Shape r = th.Shape();
        GProp_GProps g;
        BRepGProp::VolumeProperties(r, g);
        if (g.Mass() < 0) r.Reverse();   // offsetting a surface can leave the solid inside out
        return put(r);
    });
}

/// Press/pull: extrude the planar face nearest to the point along its normal by d (join if d > 0, cut if d < 0).
API uint32_t ok_press_pull(uint32_t h, double x, double y, double z, double d) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        int f = nearest(faces, gp_Pnt(x, y, z), 0.5 + 1e-3 * diag(s));
        if (f == 0) {
            g_error = "면을 찾을 수 없습니다";
            return 0;
        }
        const TopoDS_Face& face = TopoDS::Face(faces(f));
        BRepAdaptor_Surface surf(face, false);
        if (surf.GetType() != GeomAbs_Plane) {
            g_error = "평면인 면만 누르기/당기기 할 수 있습니다";
            return 0;
        }
        gp_Dir n = surf.Plane().Axis().Direction();
        if (face.Orientation() == TopAbs_REVERSED) n.Reverse();
        TopoDS_Shape tool = BRepPrimAPI_MakePrism(face, gp_Vec(n) * d).Shape();
        TopoDS_Shape r;
        if (d >= 0) {
            BRepAlgoAPI_Fuse op(s, tool);
            op.SimplifyResult();
            r = op.Shape();
        } else {
            BRepAlgoAPI_Cut op(s, tool);
            op.SimplifyResult();
            r = op.Shape();
        }
        return put(r);
    });
}

/// Edge flange frame: for the straight edge nearest to the point, writes origin (edge start),
/// outward direction (in the face plane, away from the face), face normal, edge direction, and the edge length.
API int ok_edge_flange_frame(uint32_t h, double x, double y, double z, double* out) {
    TopoDS_Shape s;
    if (!get(h, s)) return 0;
    try {
        TopTools_IndexedMapOfShape edges;
        TopExp::MapShapes(s, TopAbs_EDGE, edges);
        gp_Pnt pick(x, y, z);
        int e = nearest(edges, pick, 0.5 + 1e-3 * diag(s));
        if (e == 0) return 0;
        const TopoDS_Edge& edge = TopoDS::Edge(edges(e));
        BRepAdaptor_Curve c(edge);
        if (c.GetType() != GeomAbs_Line) {
            g_error = "직선 모서리를 선택하세요";
            return 0;
        }
        gp_Pnt a = c.Value(c.FirstParameter()), b = c.Value(c.LastParameter());
        gp_Dir dir(gp_Vec(a, b));
        // the planar face on this edge whose normal points most "up" is the sheet's top face
        TopTools_IndexedDataMapOfShapeListOfShape ef;
        TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, ef);
        const TopTools_ListOfShape& fl = ef.FindFromKey(edge);
        gp_Dir best_n;
        gp_Pnt best_c;
        double best_area = -1;
        for (TopTools_ListIteratorOfListOfShape it(fl); it.More(); it.Next()) {
            const TopoDS_Face& face = TopoDS::Face(it.Value());
            BRepAdaptor_Surface surf(face, false);
            if (surf.GetType() != GeomAbs_Plane) continue;
            GProp_GProps g;
            BRepGProp::SurfaceProperties(face, g);
            if (g.Mass() > best_area) {
                best_area = g.Mass();
                best_n = surf.Plane().Axis().Direction();
                if (face.Orientation() == TopAbs_REVERSED) best_n.Reverse();
                best_c = g.CentreOfMass();
            }
        }
        if (best_area < 0) return 0;
        gp_Dir out_dir = best_n.Crossed(dir);
        gp_Vec toEdge(best_c, a);
        if (toEdge.Dot(gp_Vec(out_dir)) < 0) out_dir.Reverse();
        double r[13] = {a.X(), a.Y(), a.Z(), out_dir.X(), out_dir.Y(), out_dir.Z(), best_n.X(), best_n.Y(), best_n.Z(), dir.X(), dir.Y(), dir.Z(), a.Distance(b)};
        std::memcpy(out, r, sizeof r);
        return 1;
    } catch (...) {
        return 0;
    }
}

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

/// Mass properties with unit density: [volume, cx, cy, cz, Ixx, Iyy, Izz, Ixy, Iyz, Ixz] where the
/// inertia tensor is about the centre of mass (multiply by density to get mass units).
API int ok_mass(uint32_t h, double* out) {
    TopoDS_Shape s;
    if (!get(h, s)) return 0;
    try {
        GProp_GProps g;
        BRepGProp::VolumeProperties(s, g);
        gp_Pnt c = g.CentreOfMass();
        gp_Mat m = g.MatrixOfInertia();
        double r[10] = {g.Mass(), c.X(), c.Y(), c.Z(), m(1, 1), m(2, 2), m(3, 3), m(1, 2), m(2, 3), m(1, 3)};
        std::memcpy(out, r, sizeof r);
        return 1;
    } catch (...) {
        return 0;
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

// ------------------------------------------------------------------- drawings (hidden line removal)

namespace {
thread_local std::vector<int32_t> g_kinds;

/// Append the polyline of every edge of s to g_buf/g_counts with the given kind.
void add_polylines(const TopoDS_Shape& s, int kind, double defl) {
    if (s.IsNull()) return;
    BRepLib::BuildCurves3d(s);
    for (TopExp_Explorer e(s, TopAbs_EDGE); e.More(); e.Next()) {
        const TopoDS_Edge& edge = TopoDS::Edge(e.Current());
        if (BRep_Tool::Degenerated(edge)) continue;
        BRepAdaptor_Curve c(edge);
        GCPnts_TangentialDeflection pts(c, 0.1, defl);
        int n = 0;
        for (int k = 1; k <= pts.NbPoints(); k++) {
            gp_Pnt p = pts.Value(k);
            g_buf.push_back(p.X());
            g_buf.push_back(p.Y());
            g_buf.push_back(p.Z());
            n++;
        }
        if (n < 2) {
            g_buf.resize(g_buf.size() - n * 3);
            continue;
        }
        g_counts.push_back(n);
        g_kinds.push_back(kind);
    }
}
}  // namespace

/// Orthographic hidden-line projection of shapes. dir points from the viewer into the scene and
/// right is the sheet's x direction (sheet y = -dir x right). Projected points are (x, y, 0).
/// Kinds: 0 visible sharp edge, 1 visible outline, 2 visible tangent edge, 3 hidden edge, 4 hidden outline.
/// Returns the polyline count; fetch with ok_edges_points / ok_edges_copy and ok_hlr_kinds.
API int ok_hlr(const uint32_t* hs, int n, double dx, double dy, double dz, double rx, double ry, double rz, double defl) {
    g_buf.clear();
    g_counts.clear();
    g_kinds.clear();
    try {
        g_error.clear();
        TopoDS_Compound c;
        BRep_Builder b;
        b.MakeCompound(c);
        for (int i = 0; i < n; i++) {
            TopoDS_Shape s;
            if (get(hs[i], s)) b.Add(c, s);
        }
        gp_Dir toViewer(-dx, -dy, -dz), right(rx, ry, rz);
        HLRAlgo_Projector proj(gp_Ax2(gp_Pnt(0, 0, 0), toViewer, right));
        Handle(HLRBRep_Algo) algo = new HLRBRep_Algo();
        algo->Add(c);
        algo->Projector(proj);
        algo->Update();
        algo->Hide();
        HLRBRep_HLRToShape out(algo);
        double d = std::max(1e-3, defl);
        add_polylines(out.VCompound(), 0, d);
        add_polylines(out.OutLineVCompound(), 1, d);
        add_polylines(out.Rg1LineVCompound(), 2, d);
        add_polylines(out.HCompound(), 3, d);
        add_polylines(out.OutLineHCompound(), 4, d);
    } catch (Standard_Failure& e) {
        g_error = e.GetMessageString() ? e.GetMessageString() : "투영 뷰를 만들 수 없습니다";
        return -1;
    } catch (...) {
        g_error = "투영 뷰를 만들 수 없습니다";
        return -1;
    }
    return (int)g_counts.size();
}

API void ok_hlr_kinds(int32_t* out) { std::memcpy(out, g_kinds.data(), g_kinds.size() * sizeof(int32_t)); }

// ------------------------------------------------------------------- more modelling

/// Split a shape by the infinite plane through o with normal nrm. Returns the split result (use ok_solids).
API uint32_t ok_split(uint32_t h, double ox, double oy, double oz, double nx, double ny, double nz) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        gp_Pln pl(gp_Pnt(ox, oy, oz), gp_Dir(nx, ny, nz));
        Bnd_Box box;
        BRepBndLib::Add(s, box);
        double x0, y0, z0, x1, y1, z1;
        box.Get(x0, y0, z0, x1, y1, z1);
        double u, v;
        ElSLib::Parameters(pl, gp_Pnt((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), u, v);
        double d = diag(s) + 10;
        TopoDS_Face tool = BRepBuilderAPI_MakeFace(pl, u - d, u + d, v - d, v + d).Face();
        BRepAlgoAPI_Splitter sp;
        TopTools_ListOfShape args, tools;
        args.Append(s);
        tools.Append(tool);
        sp.SetArguments(args);
        sp.SetTools(tools);
        sp.Build();
        if (sp.HasErrors() || !sp.IsDone()) {
            g_error = "본체를 분할할 수 없습니다";
            return 0;
        }
        return put(sp.Shape());
    });
}

/// Draft the faces nearest to pts by deg, pulling along the outward normal of the planar face
/// nearest to (px, py, pz), which is also the neutral plane.
API uint32_t ok_draft(uint32_t h, const double* pts, int n, double px, double py, double pz, double deg) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape s;
        if (!get(h, s)) return 0;
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        double tol = 0.5 + 1e-3 * diag(s);
        int nf = nearest(faces, gp_Pnt(px, py, pz), tol);
        if (nf == 0) {
            g_error = "고정 평면을 찾을 수 없습니다";
            return 0;
        }
        const TopoDS_Face& neutral = TopoDS::Face(faces(nf));
        BRepAdaptor_Surface surf(neutral, false);
        if (surf.GetType() != GeomAbs_Plane) {
            g_error = "고정 평면은 평면인 면이어야 합니다";
            return 0;
        }
        gp_Pln pl = surf.Plane();
        gp_Dir pull = pl.Axis().Direction();
        if (neutral.Orientation() == TopAbs_REVERSED) pull.Reverse();
        BRepOffsetAPI_DraftAngle draft(s);
        for (int i = 0; i < n; i++) {
            int f = nearest(faces, gp_Pnt(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]), tol);
            if (f == 0 || f == nf) continue;
            draft.Add(TopoDS::Face(faces(f)), pull, deg * M_PI / 180.0, pl);
            if (!draft.AddDone()) {
                g_error = "이 면에는 기울기를 줄 수 없습니다 (고정 평면에 수직인 면을 선택하세요)";
                return 0;
            }
        }
        draft.Build();
        if (!draft.IsDone()) {
            g_error = "기울기를 만들 수 없습니다";
            return 0;
        }
        return put(draft.Shape());
    });
}

namespace {
/// Solid swept along a wire with a circular section of radius r at the wire's start.
TopoDS_Shape pipe_along(const TopoDS_Wire& w, double r) {
    BRepTools_WireExplorer we(w);
    if (!we.More()) throw Standard_Failure("경로가 비어 있습니다");
    const TopoDS_Edge& e0 = we.Current();
    BRepAdaptor_Curve c(e0);
    bool rev = e0.Orientation() == TopAbs_REVERSED;
    double t = rev ? c.LastParameter() : c.FirstParameter();
    gp_Pnt p;
    gp_Vec d;
    c.D1(t, p, d);
    if (rev) d.Reverse();
    TopoDS_Wire section = BRepBuilderAPI_MakeWire(BRepBuilderAPI_MakeEdge(gp_Circ(gp_Ax2(p, gp_Dir(d)), r)).Edge()).Wire();
    BRepOffsetAPI_MakePipeShell ps(w);
    ps.SetMode(false);
    ps.SetTransitionMode(BRepBuilderAPI_RoundCorner);
    ps.Add(section);
    ps.Build();
    if (!ps.IsDone()) throw Standard_Failure("파이프를 만들 수 없습니다 (반지름이 경로의 굽힘보다 작은지 확인하세요)");
    ps.MakeSolid();
    return ps.Shape();
}
}  // namespace

/// Pipe of outer radius r along a wire; hollow when inner > 0.
API uint32_t ok_pipe(uint32_t wire, double r, double inner) {
    return guarded([&]() -> uint32_t {
        TopoDS_Shape w;
        if (!get(wire, w)) return 0;
        TopoDS_Shape outer = pipe_along(TopoDS::Wire(w), r);
        if (inner <= 1e-9) return put(outer);
        BRepAlgoAPI_Cut cut(outer, pipe_along(TopoDS::Wire(w), inner));
        if (cut.HasErrors()) {
            g_error = "속이 빈 파이프를 만들 수 없습니다";
            return 0;
        }
        return put(cut.Shape());
    });
}

/// Coil: a circular section of radius r swept along a helix of radius R about the Z axis through
/// (cx, cy, cz), rising pitch per turn.
API uint32_t ok_coil(double cx, double cy, double cz, double R, double pitch, double turns, double r) {
    return guarded([&]() -> uint32_t {
        Handle(Geom_CylindricalSurface) cyl = new Geom_CylindricalSurface(gp_Ax3(gp_Pnt(cx, cy, cz), gp::DZ(), gp::DX()), R);
        gp_Dir2d dir(2 * M_PI, pitch);
        double len = turns * std::sqrt(4 * M_PI * M_PI + pitch * pitch);
        Handle(Geom2d_TrimmedCurve) seg = GCE2d_MakeSegment(gp_Lin2d(gp_Pnt2d(0, 0), dir), 0, len).Value();
        TopoDS_Edge e = BRepBuilderAPI_MakeEdge(seg, cyl).Edge();
        BRepLib::BuildCurves3d(e);
        TopoDS_Wire w = BRepBuilderAPI_MakeWire(e).Wire();
        BRepOffsetAPI_MakePipeShell ps(w);
        ps.SetMode(gp::DZ());   // constant binormal keeps the section upright along the helix
        gp_Pnt p0(cx + R, cy, cz);
        gp_Dir t(0, 2 * M_PI * R, pitch);
        ps.Add(BRepBuilderAPI_MakeWire(BRepBuilderAPI_MakeEdge(gp_Circ(gp_Ax2(p0, t), r)).Edge()).Wire());
        ps.Build();
        if (!ps.IsDone()) {
            g_error = "코일을 만들 수 없습니다";
            return 0;
        }
        ps.MakeSolid();
        return put(ps.Shape());
    });
}

/// Sew surface bodies together; closed results become solids.
API uint32_t ok_sew(const uint32_t* hs, int n, double tol) {
    return guarded([&]() -> uint32_t {
        BRepBuilderAPI_Sewing sew(tol);
        for (int i = 0; i < n; i++) {
            TopoDS_Shape s;
            if (!get(hs[i], s)) return 0;
            sew.Add(s);
        }
        sew.Perform();
        TopoDS_Shape sewn = sew.SewedShape();
        TopoDS_Compound c;
        BRep_Builder b;
        b.MakeCompound(c);
        int shells = 0;
        for (TopExp_Explorer e(sewn, TopAbs_SHELL); e.More(); e.Next()) {
            shells++;
            const TopoDS_Shell& sh = TopoDS::Shell(e.Current());
            if (BRep_Tool::IsClosed(sh)) {
                BRepBuilderAPI_MakeSolid ms(sh);
                if (ms.IsDone()) {
                    Handle(ShapeFix_Solid) fs = new ShapeFix_Solid(ms.Solid());
                    fs->Perform();
                    TopoDS_Shape solid = fs->Solid();
                    GProp_GProps g;
                    BRepGProp::VolumeProperties(solid, g);
                    if (g.Mass() < 0) solid.Reverse();
                    b.Add(c, solid);
                    continue;
                }
            }
            b.Add(c, sh);
        }
        if (shells == 0) return put(sewn);
        return put(c);
    });
}

namespace {
/// Closed polylines of a planar face's boundary offset by dist (positive grows outward) into
/// g_buf/g_counts; only the outer loop when outer_only. Returns the loop count.
int offset_face(const TopoDS_Face& fc, double dist, double defl, bool outer_only) {
    TopoDS_Shape r;
    if (std::fabs(dist) < 1e-9) r = fc;
    else {
        BRepOffsetAPI_MakeOffset mo;
        if (outer_only) {
            mo.Init(GeomAbs_Arc);
            mo.AddWire(BRepTools::OuterWire(fc));
        } else mo.Init(fc, GeomAbs_Arc);
        mo.Perform(dist);
        if (!mo.IsDone()) return 0;   // offset collapsed: nothing left
        r = mo.Shape();
    }
    if (std::fabs(dist) < 1e-9 && outer_only) r = BRepTools::OuterWire(fc);
    for (TopExp_Explorer ew(r, TopAbs_WIRE); ew.More(); ew.Next()) {
        int n = 0;
        for (BRepTools_WireExplorer we(TopoDS::Wire(ew.Current())); we.More(); we.Next()) {
            const TopoDS_Edge& edge = we.Current();
            if (BRep_Tool::Degenerated(edge)) continue;
            BRepAdaptor_Curve c(edge);
            GCPnts_TangentialDeflection pts(c, 0.1, std::max(1e-3, defl));
            int m = pts.NbPoints();
            bool rev = edge.Orientation() == TopAbs_REVERSED;
            for (int k = 0; k < m; k++) {
                if (k == 0 && n > 0) continue;   // shared with the previous edge's end
                gp_Pnt p = pts.Value(rev ? m - k : k + 1);
                g_buf.push_back(p.X());
                g_buf.push_back(p.Y());
                g_buf.push_back(p.Z());
                n++;
            }
        }
        if (n >= 2) g_counts.push_back(n);
        else g_buf.resize(g_buf.size() - n * 3);
    }
    return (int)g_counts.size();
}
}  // namespace

/// 2D offset of a planar face's boundary (positive grows outward). Returns the loop count; each loop
/// is one closed polyline, fetched with ok_edges_points / ok_edges_copy.
API int ok_offset2d(uint32_t face, double dist, double defl) {
    g_buf.clear();
    g_counts.clear();
    try {
        g_error.clear();
        TopoDS_Shape f;
        if (!get(face, f)) return -1;
        for (TopExp_Explorer e(f, TopAbs_FACE); e.More(); e.Next()) return offset_face(TopoDS::Face(e.Current()), dist, defl, false);
        return -1;
    } catch (...) {
        return 0;
    }
}

/// Offset of the planar face of h nearest to (x, y, z) (only its outer loop when outerOnly);
/// loops as for ok_offset2d. -1 when there is no planar face there.
API int ok_face_offset(uint32_t h, double x, double y, double z, double dist, double defl, int outerOnly) {
    g_buf.clear();
    g_counts.clear();
    try {
        g_error.clear();
        TopoDS_Shape s;
        if (!get(h, s)) return -1;
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        int f = nearest(faces, gp_Pnt(x, y, z), 0.5 + 1e-3 * diag(s));
        if (f == 0) return -1;
        const TopoDS_Face& face = TopoDS::Face(faces(f));
        if (BRepAdaptor_Surface(face, false).GetType() != GeomAbs_Plane) {
            g_error = "평면인 면을 선택하세요";
            return -1;
        }
        return offset_face(face, dist, defl, outerOnly != 0);
    } catch (...) {
        return 0;
    }
}

/// Vertical cylindrical holes of a solid: 5 doubles each (centre x, y, radius, top z, bottom z),
/// with the halves of split cylinders merged. Returns the hole count.
API int ok_holes(uint32_t h, double* out, int max) {
    TopoDS_Shape s;
    if (!get(h, s)) return -1;
    std::vector<std::array<double, 5>> holes;
    try {
        TopTools_IndexedMapOfShape faces;
        TopExp::MapShapes(s, TopAbs_FACE, faces);
        for (int i = 1; i <= faces.Extent(); i++) {
            const TopoDS_Face& face = TopoDS::Face(faces(i));
            BRepAdaptor_Surface surf(face, true);
            if (surf.GetType() != GeomAbs_Cylinder) continue;
            gp_Cylinder cyl = surf.Cylinder();
            gp_Dir ax = cyl.Axis().Direction();
            if (std::fabs(ax.Z()) < 0.999) continue;
            // inside surface: the face normal points at the axis
            double u = (surf.FirstUParameter() + surf.LastUParameter()) / 2, v = (surf.FirstVParameter() + surf.LastVParameter()) / 2;
            gp_Pnt p;
            gp_Vec du, dv;
            surf.D1(u, v, p, du, dv);
            gp_Vec nrm = du.Crossed(dv);
            if (face.Orientation() == TopAbs_REVERSED) nrm.Reverse();
            gp_Pnt c = cyl.Axis().Location();
            gp_Vec radial(gp_Pnt(c.X(), c.Y(), p.Z()), p);
            if (nrm.Dot(radial) >= 0) continue;
            Bnd_Box b;
            BRepBndLib::Add(face, b);
            double x0, y0, z0, x1, y1, z1;
            b.Get(x0, y0, z0, x1, y1, z1);
            double r = cyl.Radius();
            bool merged = false;
            for (auto& hh : holes)
                if (std::fabs(hh[0] - c.X()) < 1e-4 && std::fabs(hh[1] - c.Y()) < 1e-4 && std::fabs(hh[2] - r) < 1e-4) {
                    hh[3] = std::max(hh[3], z1);
                    hh[4] = std::min(hh[4], z0);
                    merged = true;
                }
            if (!merged) holes.push_back({c.X(), c.Y(), r, z1, z0});
        }
    } catch (...) {
        return -1;
    }
    int n = 0;
    for (auto& hh : holes) {
        if (n >= max) break;
        std::memcpy(out + n * 5, hh.data(), sizeof(double) * 5);
        n++;
    }
    return n;
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
            case 14: type = SLVS_C_PT_LINE_DISTANCE; break;
            case 15: type = SLVS_C_SYMMETRIC_LINE; break;
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

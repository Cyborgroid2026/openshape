using System.Globalization;
using System.Text;

namespace OpenShape;

/// <summary>Sheet metal flat pattern (unfolded with K = 0.44) and its DXF outline.</summary>
public static class FlatPattern
{
    /// <summary>The unfolded body: base plates plus each flange laid flat beyond its edge.</summary>
    public static Solid Build(SheetInfo sheet)
    {
        Solid? flat = null;
        foreach (var (frame, region) in sheet.Base)
        {
            var part = Solid.Extrude(frame, region, 0, sheet.Thickness);
            flat = flat == null ? part : flat.Boolean(part, BoolOp.Union);
        }
        foreach (var fl in sheet.Flanges)
        {
            // the flange's developed strip lies in the sheet plane, from the edge outwards; the edge
            // point is on the top face, so the strip goes down by the thickness
            var n = fl.Dir.Cross(fl.Outward).Norm();
            var plane = new PlaneFrame(fl.Origin, fl.Dir, fl.Outward, n);
            var rect = new List<P2> { new(0, 0), new(fl.Length, 0), new(fl.Length, fl.Developed), new(0, fl.Developed) };
            var loop = rect.Select((q, i) => { var q2 = rect[(i + 1) % 4]; return new Piece(0, new[] { q.X, q.Y, q2.X, q2.Y }); }).ToList();
            var region = new List<Profile> { new(0, rect, 1, new List<int>(), loop) };
            bool up = n.Dot(fl.Normal) > 0;
            var strip = Solid.Extrude(plane, region, up ? -sheet.Thickness : 0, up ? 0 : sheet.Thickness);
            flat = flat == null ? strip : flat.Boolean(strip, BoolOp.Union);
        }
        return flat ?? throw new InvalidOperationException("판금 본체가 아닙니다.");
    }

    /// <summary>Outline of the flat pattern's top face as DXF LINE entities (millimetres).</summary>
    public static string Dxf(Solid flat, SheetInfo sheet)
    {
        var f = sheet.Base[0].frame;
        double top = (f.O + f.N * sheet.Thickness).Dot(f.N);
        var sb = new StringBuilder();
        sb.Append("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n");
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        int lines = 0;
        foreach (var edge in flat.Edges)
        {
            if (edge.Length < 2 || edge.Any(p => Math.Abs(p.Dot(f.N) - top) > 1e-6)) continue;
            for (int k = 0; k + 1 < edge.Length; k++)
            {
                var a = f.Project(edge[k]);
                var b = f.Project(edge[k + 1]);
                sb.Append($"0\nLINE\n8\nOUTLINE\n10\n{F(a.X)}\n20\n{F(a.Y)}\n30\n0\n11\n{F(b.X)}\n21\n{F(b.Y)}\n31\n0\n");
                lines++;
            }
        }
        sb.Append("0\nENDSEC\n0\nEOF\n");
        return lines == 0 ? "" : sb.ToString();
    }
}

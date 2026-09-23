using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using HalconDotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DP.LabelInspection.HalconGeometryProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length != 1 || Directory.Exists(args[0]))
                throw new ArgumentException("Supply a NEW output directory.");
            Directory.CreateDirectory(args[0]);
            HOperatorSet.GetSystem("version", out var version);
            using (version)
                Console.WriteLine("HALCON=" + version.S);
            // 仅供隔离探针使用：保留HALCON隐式当前图像域之外的几何。
            HOperatorSet.SetSystem("clip_region", "false");
            HOperatorSet.GenRectangle1(out var outer, 20, 20, 160, 180);
            using (outer)
            {
                HOperatorSet.GenRectangle1(out var hole, 60, 60, 100, 110);
                using (hole)
                {
                    HOperatorSet.Difference(outer, hole, out var ring);
                    using (ring)
                    {
                        HOperatorSet.GenCircle(out var island, 210, 260, 22);
                        using (island)
                        {
                            HOperatorSet.Union2(ring, island, out var combined);
                            using (combined)
                            {
                                HOperatorSet.GenRectangle1(out var pixel, 200, 20, 200, 20);
                                using (pixel)
                                {
                                    HOperatorSet.GenEmptyRegion(out var empty);
                                    using (empty)
                                    {
                                        HOperatorSet.ConcatObj(combined, pixel, out var pair);
                                        using (pair)
                                        {
                                            HOperatorSet.ConcatObj(pair, empty, out var all);
                                            using (all)
                                            {
                                                HOperatorSet.WriteObject(
                                                    all,
                                                    Path.Combine(args[0], "regions.hobj")
                                                );
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            using (var rows = new HTuple(new[] { 30.25, 55.5, 100.125 }))
            using (var cols = new HTuple(new[] { 230.75, 285.25, 300.5 }))
            {
                HOperatorSet.GenContourPolygonXld(out var open, rows, cols);
                using (open)
                using (var closedRows = new HTuple(new[] { 185.25, 210.125, 240.75, 185.25 }))
                using (var closedCols = new HTuple(new[] { 130.125, 155.875, 105.375, 130.125 }))
                {
                    HOperatorSet.GenContourPolygonXld(out var closed, closedRows, closedCols);
                    using (closed)
                    {
                        HOperatorSet.ConcatObj(open, closed, out var pair);
                        using (pair)
                        {
                            HOperatorSet.GenImageConst(out var image, "byte", 400, 300);
                            using (image)
                            {
                                HOperatorSet.GenRectangle1(out var square, 190, 320, 245, 375);
                                using (square)
                                {
                                    HOperatorSet.PaintRegion(square, image, out var painted, 255, "fill");
                                    using (painted)
                                    {
                                        HOperatorSet.EdgesSubPix(painted, out var edges, "canny", 1, 20, 40);
                                        using (edges)
                                        {
                                            HOperatorSet.ConcatObj(pair, edges, out var all);
                                            using (all)
                                                HOperatorSet.WriteObject(
                                                    all,
                                                    Path.Combine(args[0], "contours.hobj")
                                                );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            CanvasGeometry snapshot;
            var attributes = new JArray();
            HOperatorSet.ReadObject(out var regionFile, Path.Combine(args[0], "regions.hobj"));
            HOperatorSet.ReadObject(out var contourFile, Path.Combine(args[0], "contours.hobj"));
            using (regionFile)
            using (contourFile)
            {
                snapshot = new CanvasGeometry(
                    ExtractRegions(regionFile),
                    ExtractContours(contourFile, attributes)
                );
            }

            // 全部源HObject现已释放，后续检查仅使用独立.NET数据。
            Require(snapshot.Regions.Count == 3, "Lost region object tuple structure.");
            Require(
                snapshot.Regions[0].Contains(25, 25)
                    && !snapshot.Regions[0].Contains(80, 80)
                    && snapshot.Regions[0].Contains(260, 210),
                "Hole/island membership changed."
            );
            Require(
                snapshot.Regions[1].AreaPixels == 1 && snapshot.Regions[2].Runs.Count == 0,
                "Single pixel or empty region changed."
            );
            Require(
                snapshot.Contours[0].Points[0].X == 230.75
                    && snapshot.Contours[0].Points[0].Y == 30.25
                    && !snapshot.Contours[0].Closed
                    && snapshot.Contours[1].Closed,
                "Subpixel coordinates or open/closed topology changed."
            );
            Require(
                attributes.Any(a => ((JObject)a["pointAttributes"]!).Properties().Any()),
                "No actual XLD point attributes were exercised."
            );
            File.WriteAllText(
                Path.Combine(args[0], "geometry.json"),
                JsonConvert.SerializeObject(snapshot, Formatting.Indented)
            );
            File.WriteAllText(Path.Combine(args[0], "xld-attributes.json"), attributes.ToString());
            var restored = JsonConvert.DeserializeObject<CanvasGeometry>(
                File.ReadAllText(Path.Combine(args[0], "geometry.json"))
            )!;
            Require(
                restored.Regions[0].AreaPixels == snapshot.Regions[0].AreaPixels
                    && restored.Contours[0].Points[0].X == 230.75,
                "Neutral JSON roundtrip lost geometry."
            );
            Console.WriteLine(
                $"PASS region objects={snapshot.Regions.Count}; runs={snapshot.Regions.Sum(r => r.Runs.Count)}; pixel area={snapshot.Regions.Sum(r => r.AreaPixels)}; exact HALCON XOR=0 per object"
            );
            Console.WriteLine(
                $"PASS XLD contours={snapshot.Contours.Count}; points={snapshot.Contours.Sum(c => c.Points.Count)}; subpixel/open/closed preserved; attributes={attributes}"
            );
            Console.WriteLine(
                "PASS native file read, owned extraction, reconstruction, JSON and use after source disposal."
            );
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static IEnumerable<CanvasRegion> ExtractRegions(HObject source)
    {
        var result = new List<CanvasRegion>();
        HOperatorSet.CountObj(source, out var count);
        using (count)
            for (int i = 1; i <= count.I; i++)
            {
                HOperatorSet.SelectObj(source, out var single, i);
                using (single)
                {
                    HOperatorSet.GetRegionRuns(single, out var rows, out var begins, out var ends);
                    using (rows)
                    using (begins)
                    using (ends)
                    {
                        var runs = Enumerable
                            .Range(0, rows.Length)
                            .Select(k => new CanvasRun(rows[k].I, begins[k].I, checked(ends[k].I + 1)))
                            .ToArray();
                        var region = new CanvasRegion("region-" + i, runs);
                        result.Add(region);
                        HOperatorSet.AreaCenter(single, out var area, out var row, out var col);
                        using (area)
                        using (row)
                        using (col)
                            Require(region.AreaPixels == area.D, "Region run area differs from HALCON.");
                        using var portableRows = new HTuple(region.Runs.Select(r => r.Row).ToArray());
                        using var portableBegins = new HTuple(
                            region.Runs.Select(r => r.StartColumn).ToArray()
                        );
                        using var portableEnds = new HTuple(
                            region.Runs.Select(r => r.EndColumnExclusive - 1).ToArray()
                        );
                        HOperatorSet.GenRegionRuns(
                            out var reconstructed,
                            portableRows,
                            portableBegins,
                            portableEnds
                        );
                        using (reconstructed)
                        {
                            HOperatorSet.SymmDifference(single, reconstructed, out var difference);
                            using (difference)
                            {
                                HOperatorSet.AreaCenter(difference, out var a, out var r, out var c);
                                using (a)
                                using (r)
                                using (c)
                                    Require(a.D == 0, "Region roundtrip XOR is nonzero.");
                            }
                        }
                    }
                }
            }

        return result;
    }

    private static IEnumerable<CanvasPolyline> ExtractContours(HObject source, JArray attributes)
    {
        var result = new List<CanvasPolyline>();
        HOperatorSet.CountObj(source, out var count);
        using (count)
            for (int i = 1; i <= count.I; i++)
            {
                HOperatorSet.SelectObj(source, out var single, i);
                using (single)
                {
                    HOperatorSet.GetObjClass(single, out var objectClass);
                    using (objectClass)
                        Require(
                            objectClass.S == "xld_cont",
                            "Only XLD contours are supported in this probe, not every XLD subtype."
                        );
                    HOperatorSet.GetContourXld(single, out var rows, out var cols);
                    using (rows)
                    using (cols)
                    {
                        HOperatorSet.TestClosedXld(single, out var closed);
                        using (closed)
                            result.Add(
                                new CanvasPolyline(
                                    "contour-" + i,
                                    Enumerable
                                        .Range(0, rows.Length)
                                        .Select(k => new CanvasPoint(cols[k].D, rows[k].D)),
                                    closed.I != 0
                                )
                            );
                        var portable = result.Last();
                        using var portableRows = new HTuple(portable.Points.Select(p => p.Y).ToArray());
                        using var portableCols = new HTuple(portable.Points.Select(p => p.X).ToArray());
                        HOperatorSet.GenContourPolygonXld(out var reconstructed, portableRows, portableCols);
                        using (reconstructed)
                        {
                            HOperatorSet.GetContourXld(reconstructed, out var rr, out var cc);
                            using (rr)
                            using (cc)
                                Require(
                                    rr.DArr.SequenceEqual(rows.DArr) && cc.DArr.SequenceEqual(cols.DArr),
                                    "XLD coordinate reconstruction changed points."
                                );
                        }
                    }

                    var points = new JObject();
                    var globals = new JObject();
                    HOperatorSet.QueryContourAttribsXld(single, out var pointNames);
                    using (pointNames)
                        for (int k = 0; k < pointNames.Length; k++)
                        {
                            string name = pointNames[k].S;
                            HOperatorSet.GetContourAttribXld(single, name, out var values);
                            using (values)
                                points[name] = TupleJson(values);
                        }

                    HOperatorSet.QueryContourGlobalAttribsXld(single, out var globalNames);
                    using (globalNames)
                        for (int k = 0; k < globalNames.Length; k++)
                        {
                            string name = globalNames[k].S;
                            HOperatorSet.GetContourGlobalAttribXld(single, name, out var values);
                            using (values)
                                globals[name] = TupleJson(values);
                        }

                    attributes.Add(
                        new JObject
                        {
                            { "id", "contour-" + i },
                            { "pointAttributes", points },
                            { "globalAttributes", globals },
                        }
                    );
                }
            }

        return result;
    }

    private static JArray TupleJson(HTuple values)
    {
        var array = new JArray();
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value.Type == HTupleType.STRING)
                array.Add(value.S);
            else if (value.Type == HTupleType.INTEGER)
                array.Add(value.L);
            else if (value.Type == HTupleType.DOUBLE)
                array.Add(value.D);
            else
                throw new NotSupportedException("Nonportable XLD attribute type: " + value.Type);
        }

        return array;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

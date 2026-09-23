using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.LegacyBenchmark;

/// <summary>缓存的原型几何路径；HALCON类型和句柄不进入渲染模块。</summary>
internal sealed class CanvasGeometryLayer : IDisposable
{
    private readonly List<Tuple<GraphicsPath, Color, bool>> _paths =
        new List<Tuple<GraphicsPath, Color, bool>>();

    internal CanvasGeometryLayer(CanvasGeometry geometry)
    {
        try
        {
            foreach (var region in geometry.Regions)
            {
                var path = new GraphicsPath(FillMode.Winding);
                _paths.Add(Tuple.Create(path, Color.FromArgb(unchecked((int)region.FillArgb)), true));
                foreach (var run in region.Runs)
                    path.AddRectangle(
                        new RectangleF(run.StartColumn, run.Row, run.EndColumnExclusive - run.StartColumn, 1)
                    );
            }

            foreach (var contour in geometry.Contours)
            {
                var path = new GraphicsPath();
                _paths.Add(Tuple.Create(path, Color.FromArgb(unchecked((int)contour.StrokeArgb)), false));
                // HALCON风格的整数轮廓坐标表示像素中心，而图像单元从整数边缘开始。
                var points = contour
                    .Points.Select(p => new PointF((float)(p.X + .5), (float)(p.Y + .5)))
                    .ToArray();
                if (points.Length == 1)
                    path.AddEllipse(points[0].X - .25f, points[0].Y - .25f, .5f, .5f);
                else if (points.Length > 1)
                {
                    path.AddLines(points);
                    if (contour.Closed)
                        path.CloseFigure();
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Draw(Graphics graphics, RectangleF viewport, float scale)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(viewport, CombineMode.Intersect);
            graphics.TranslateTransform(viewport.X, viewport.Y);
            graphics.ScaleTransform(scale, scale);
            foreach (var item in _paths)
            {
                if (item.Item1.PointCount == 0)
                    continue;
                if (item.Item3)
                {
                    graphics.SmoothingMode = SmoothingMode.None;
                    using var brush = new SolidBrush(item.Item2);
                    graphics.FillPath(brush, item.Item1);
                }
                else
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var pen = new Pen(item.Item2, 2 / scale);
                    graphics.DrawPath(pen, item.Item1);
                }
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    public void Dispose()
    {
        foreach (var item in _paths)
            item.Item1.Dispose();
        _paths.Clear();
    }
}

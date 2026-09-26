using System;
using OpenCvSharp;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>把标签图配准到参考图：先整体仿射（相位相关初值 + ECC，半分辨率），再逐行在±12像素内模板匹配微调。</summary>
internal sealed class LabelAligner : IDisposable
{
    private const int Search = 12;

    private readonly Mat _reference;

    /// <summary>以参考图（灰度）创建，参考图由本对象拥有。</summary>
    internal LabelAligner(Mat reference)
    {
        _reference = reference;
    }

    /// <summary>返回配准到参考图坐标系、尺寸相同的新图（调用方释放）。</summary>
    internal Mat Align(Mat input)
    {
        using var actual = new Mat();
        int dh = _reference.Rows - input.Rows,
            dw = _reference.Cols - input.Cols;
        using (var padded = new Mat())
        {
            Cv2.CopyMakeBorder(input, padded, 0, Math.Max(0, dh), 0, Math.Max(0, dw), BorderTypes.Replicate);
            new Mat(padded, new Rect(0, 0, _reference.Cols, _reference.Rows)).CopyTo(actual);
        }

        using var r = new Mat();
        using var a = new Mat();
        Cv2.Resize(_reference, r, new Size(), .5, .5, InterpolationFlags.Area);
        Cv2.Resize(actual, a, new Size(), .5, .5, InterpolationFlags.Area);
        using var rf = new Mat();
        using var af = new Mat();
        r.ConvertTo(rf, MatType.CV_32F);
        a.ConvertTo(af, MatType.CV_32F);
        using var window = new Mat();
        var shift = Cv2.PhaseCorrelate(rf, af, window, out _);
        using var warp = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        warp.Set(0, 0, 1f);
        warp.Set(1, 1, 1f);
        warp.Set(0, 2, (float)shift.X);
        warp.Set(1, 2, (float)shift.Y);
        try
        {
            Cv2.FindTransformECC(
                rf,
                af,
                warp,
                MotionTypes.Affine,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 100, 1e-5),
                null,
                5
            );
        }
        catch (OpenCVException)
        {
            // ECC不收敛时保留相位相关的平移。
        }

        warp.Set(0, 2, warp.At<float>(0, 2) * 2);
        warp.Set(1, 2, warp.At<float>(1, 2) * 2);
        var aligned = new Mat();
        Cv2.WarpAffine(
            actual,
            aligned,
            warp,
            _reference.Size(),
            InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
            BorderTypes.Replicate
        );
        return aligned;
    }

    /// <summary>行位置微调：参考图该行内容在配准图±12像素内的最佳匹配偏移。</summary>
    internal Point LineShift(Mat aligned, Rect line)
    {
        var outer = new Rect(
            line.X - Search,
            line.Y - Search,
            line.Width + 2 * Search,
            line.Height + 2 * Search
        );
        if (
            outer.X < 0
            || outer.Y < 0
            || outer.Right > aligned.Cols
            || outer.Bottom > aligned.Rows
            || line.Right > _reference.Cols
            || line.Bottom > _reference.Rows
        )
        {
            return new Point(0, 0);
        }

        using var template = new Mat(_reference, line);
        using var area = new Mat(aligned, outer);
        using var result = new Mat();
        Cv2.MatchTemplate(area, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out _, out _, out Point best);
        return new Point(best.X - Search, best.Y - Search);
    }

    /// <summary>释放参考图。</summary>
    public void Dispose()
    {
        _reference.Dispose();
    }
}

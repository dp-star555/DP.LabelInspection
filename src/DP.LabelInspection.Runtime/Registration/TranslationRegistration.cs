using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

internal static class TranslationRegistration
{
    internal static Tuple<int, int>? Translation(Mat actual, Mat reference, InspectionRecipe recipe)
    {
        var fixedRegion = recipe.Regions.FirstOrDefault(r => r.Kind == ERegionKind.Fixed);
        if (fixedRegion == null || fixedRegion.Bounds.Width < 20 || fixedRegion.Bounds.Height < 20)
        {
            return null;
        }

        using var a = new Mat(actual, CvImages.Rect(fixedRegion.Bounds));
        using var r = new Mat(reference, CvImages.Rect(fixedRegion.Bounds));
        using var mask = new Mat(r.Rows, r.Cols, MatType.CV_8UC1, Scalar.All(255));
        foreach (
            var ignore in recipe.Regions.Where(v =>
                v.Kind == ERegionKind.Ignore && v.Bounds.Intersects(fixedRegion.Bounds)
            )
        )
        {
            int x = Math.Max(ignore.Bounds.X, fixedRegion.Bounds.X) - fixedRegion.Bounds.X,
                y = Math.Max(ignore.Bounds.Y, fixedRegion.Bounds.Y) - fixedRegion.Bounds.Y;
            int right =
                    Math.Min(
                        ignore.Bounds.X + ignore.Bounds.Width,
                        fixedRegion.Bounds.X + fixedRegion.Bounds.Width
                    ) - fixedRegion.Bounds.X,
                bottom =
                    Math.Min(
                        ignore.Bounds.Y + ignore.Bounds.Height,
                        fixedRegion.Bounds.Y + fixedRegion.Bounds.Height
                    ) - fixedRegion.Bounds.Y;
            using var cut = new Mat(mask, new Rect(x, y, right - x, bottom - y));
            cut.SetTo(Scalar.All(0));
        }

        Cv2.MeanStdDev(r, out _, out Scalar std, mask);
        if (std.Val0 < 10 || Cv2.CountNonZero(mask) < 200)
        {
            return null;
        }

        using var warp = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        warp.Set(0, 0, 1f);
        warp.Set(1, 1, 1f);
        try
        {
            double score = Cv2.FindTransformECC(
                r,
                a,
                warp,
                MotionTypes.Translation,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 80, .00001),
                mask,
                1
            );
            float x = warp.At<float>(0, 2),
                y = warp.At<float>(1, 2);
            if (
                double.IsNaN(score)
                || score < .75
                || float.IsNaN(x)
                || float.IsNaN(y)
                || Math.Abs(x) > 12
                || Math.Abs(y) > 12
            )
            {
                return null;
            }

            return Tuple.Create((int)Math.Round(x), (int)Math.Round(y));
        }
        catch (OpenCVException)
        {
            return null;
        }
    }
}

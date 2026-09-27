using System;
using System.Collections.Generic;
using System.Linq;
using HalconDotNet;

namespace DP.LabelInspection.AnomalyBenchmark.Halcon;

/// <summary>一个字符键的HALCON变差模型：训练后取均值图与标准差图（real），标准差不低于floor。</summary>
internal sealed class VariationModel : IDisposable
{
    private readonly HObject _mean;
    private readonly HObject _deviation;
    private readonly int _width;
    private readonly int _height;

    internal VariationModel(IEnumerable<string> images, double floor)
    {
        var paths = images.ToList();
        if (paths.Count == 0)
        {
            throw new ArgumentException("No training images.");
        }

        HOperatorSet.ReadImage(out HObject first, paths[0]);
        HOperatorSet.GetImageSize(first, out HTuple width, out HTuple height);
        first.Dispose();
        _width = width.I;
        _height = height.I;
        HOperatorSet.CreateVariationModel(width, height, "byte", "standard", out HTuple model);
        try
        {
            foreach (string path in paths)
            {
                HOperatorSet.ReadImage(out HObject image, path);
                using (image)
                {
                    HOperatorSet.TrainVariationModel(image, model);
                }
            }

            HOperatorSet.GetVariationModel(out HObject mean, out HObject variation, model);
            using (mean)
            using (variation)
            {
                HOperatorSet.ConvertImageType(mean, out _mean, "real");
                HOperatorSet.ConvertImageType(variation, out HObject deviation, "real");
                using (deviation)
                {
                    HOperatorSet.GenImageProto(deviation, out HObject lower, floor);
                    using (lower)
                    {
                        HOperatorSet.MaxImage(deviation, lower, out _deviation);
                    }
                }
            }
        }
        finally
        {
            HOperatorSet.ClearVariationModel(model);
        }
    }

    /// <summary>±shift内平移取最小的最大偏差比（先smooth×smooth均值平滑）。</summary>
    internal double Score(string path, int smooth, int shift)
    {
        HOperatorSet.ReadImage(out HObject raw, path);
        using (raw)
        {
            HOperatorSet.ConvertImageType(raw, out HObject test, "real");
            using (test)
            {
                int s = shift,
                    r2 = _height - 1 - s,
                    c2 = _width - 1 - s;
                HOperatorSet.CropRectangle1(_mean, out HObject mean, s, s, r2, c2);
                HOperatorSet.CropRectangle1(_deviation, out HObject deviation, s, s, r2, c2);
                using (mean)
                using (deviation)
                {
                    double best = double.MaxValue;
                    for (int dy = -s; dy <= s; dy++)
                    {
                        for (int dx = -s; dx <= s; dx++)
                        {
                            HOperatorSet.CropRectangle1(
                                test,
                                out HObject moved,
                                s + dy,
                                s + dx,
                                r2 + dy,
                                c2 + dx
                            );
                            using (moved)
                            {
                                best = Math.Min(best, MaximumRatio(moved, mean, deviation, smooth));
                            }
                        }
                    }

                    return best;
                }
            }
        }
    }

    private static double MaximumRatio(HObject test, HObject mean, HObject deviation, int smooth)
    {
        HOperatorSet.SubImage(test, mean, out HObject difference, 1.0, 0.0);
        using (difference)
        {
            HOperatorSet.AbsImage(difference, out HObject absolute);
            using (absolute)
            {
                HOperatorSet.DivImage(absolute, deviation, out HObject ratio, 1.0, 0.0);
                using (ratio)
                {
                    HObject smoothed;
                    if (smooth > 1)
                    {
                        HOperatorSet.MeanImage(ratio, out smoothed, smooth, smooth);
                    }
                    else
                    {
                        HOperatorSet.CopyObj(ratio, out smoothed, 1, 1);
                    }

                    using (smoothed)
                    {
                        HOperatorSet.GetDomain(smoothed, out HObject domain);
                        using (domain)
                        {
                            HOperatorSet.MinMaxGray(domain, smoothed, 0, out _, out HTuple maximum, out _);
                            return maximum.D;
                        }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _mean.Dispose();
        _deviation.Dispose();
    }
}

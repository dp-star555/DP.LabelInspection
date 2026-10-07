using System;
using System.Linq;
using V = DP.Vision;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 按放置从原图取样：输出配方尺寸的画布，只在每个 ROI（外扩边距）内双线性取样，其余像素为0。
/// 整数像素平移时直接拷贝像素，不做插值。
/// </summary>
internal static class PlacedSampler
{
    internal static V.ImageFrame Sample(V.ImageFrame actual, InspectionRecipe recipe, InspectionPlacement placement, int margin)
    {
        foreach (var region in recipe.Regions)
        {
            // ROI 本身必须完整落在原图内，否则与未放置时“定位后ROI超出原图”一致地拒绝。
            var b = region.Bounds;
            foreach (var (cx, cy) in new[] { (b.X, b.Y), (b.X + b.Width, b.Y), (b.X, b.Y + b.Height), (b.X + b.Width, b.Y + b.Height) })
            {
                var (ix, iy) = placement.Map(cx, cy);
                if (ix < 0 || iy < 0 || ix > actual.Image.Info.Width || iy > actual.Image.Info.Height)
                    throw new InvalidOperationException($"placed_roi_outside_image: ROI“{region.Name}”放置后超出原图。");
            }
        }

        return Sample(actual, recipe.Width, recipe.Height, placement,
            recipe.Regions.Select(r => (Math.Max(0, r.Bounds.X - margin), Math.Max(0, r.Bounds.Y - margin),
                Math.Min(recipe.Width, r.Bounds.X + r.Bounds.Width + margin), Math.Min(recipe.Height, r.Bounds.Y + r.Bounds.Height + margin))),
            actual.FrameId + "#placed");
    }

    internal static V.ImageFrame Sample(V.ImageFrame actual, int width, int height, InspectionPlacement placement,
        System.Collections.Generic.IEnumerable<(int X0, int Y0, int X1, int Y1)> areas, string frameId)
    {
        var info = actual.Image.Info;
        int channels = info.BytesPerPixel;
        var source = new byte[info.ByteLength];
        actual.Image.CopyTo(0, source, 0, source.Length);
        var target = new byte[checked(width * height * channels)];
        var filled = new bool[width * height];
        bool integer = placement.IsIntegerTranslation;
        int dx = (int)placement.Tx, dy = (int)placement.Ty;
        foreach (var (x0, y0, x1, y1) in areas)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int index = y * width + x;
                    if (filled[index])
                        continue;
                    filled[index] = true;
                    int offset = index * channels;
                    if (integer)
                    {
                        int sx = Clamp(x + dx, 0, info.Width - 1), sy = Clamp(y + dy, 0, info.Height - 1);
                        Buffer.BlockCopy(source, (sy * info.Width + sx) * channels, target, offset, channels);
                        continue;
                    }

                    var (px, py) = placement.Map(x + 0.5, y + 0.5);
                    Bilinear(source, info.Width, info.Height, channels, px - 0.5, py - 0.5, target, offset);
                }
            }
        }

        using var image = V.VisionImage.CopyFrom(new V.ImageInfo(width, height, info.Layout), target);
        return new V.ImageFrame(frameId, image);
    }

    private static void Bilinear(byte[] source, int width, int height, int channels, double x, double y, byte[] target, int offset)
    {
        x = Math.Max(0, Math.Min(width - 1, x));
        y = Math.Max(0, Math.Min(height - 1, y));
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        int nx = Math.Min(width - 1, ix + 1), ny = Math.Min(height - 1, iy + 1);
        double fx = x - ix, fy = y - iy;
        for (int c = 0; c < channels; c++)
        {
            double top = source[(iy * width + ix) * channels + c] * (1 - fx) + source[(iy * width + nx) * channels + c] * fx;
            double bottom = source[(ny * width + ix) * channels + c] * (1 - fx) + source[(ny * width + nx) * channels + c] * fx;
            double value = top * (1 - fy) + bottom * fy;
            target[offset + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }
    }

    private static int Clamp(int value, int minimum, int maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
}

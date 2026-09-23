using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length != 7)
                throw new ArgumentException("image output-directory x y width height text");
            if (Directory.Exists(args[1]))
                throw new IOException("Use a new output directory.");
            Directory.CreateDirectory(args[1]);
            var codec = new OpenCvImageCodec();
            var image = codec.Decode(File.ReadAllBytes(args[0]));
            var roi = new PixelRect(
                int.Parse(args[2]),
                int.Parse(args[3]),
                int.Parse(args[4]),
                int.Parse(args[5])
            );
            var text = args[6];
            using var backend = new OpenCvInspectionBackend();
            using var engine = new InspectionEngine(backend);
            var conservative = new CharacterSegmenter().Segment(image, roi, text);
            var result = await engine.ExtractGlyphCandidatesAsync(image, roi, text);
            var segmentation = result.Segmentation;
            var message =
                "inspection: "
                + conservative.Status
                + " / "
                + conservative.Reason
                + Environment.NewLine
                + "candidate: "
                + segmentation.Status
                + " / "
                + segmentation.Basis
                + " / "
                + segmentation.Reason
                + " / count="
                + segmentation.Characters.Count;
            Console.WriteLine(message);
            File.WriteAllText(Path.Combine(args[1], "result.txt"), message);
            File.WriteAllBytes(Path.Combine(args[1], "source-roi.png"), codec.EncodePng(image.Crop(roi)));
            foreach (var p in segmentation.Characters)
                File.WriteAllBytes(
                    Path.Combine(args[1], p.TokenIndex + "-" + p.Character + ".png"),
                    codec.EncodePng(p.Patch)
                );
            if (segmentation.Characters.Count != text.Count(FieldSettings.IsAlphanumeric))
                throw new InvalidOperationException(
                    "Quick builder returned no complete reviewed candidate sequence."
                );
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

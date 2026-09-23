using System;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Runtime.Codes;
using DP.LabelInspection.Storage;

namespace DP.LabelInspection.BarcodeRegression;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: BarcodeRegression <original.png> <recipe.json> <new-output-directory>"
            );
            return 2;
        }

        try
        {
            if (Directory.Exists(args[2]))
            {
                throw new IOException("Choose a new output directory.");
            }

            var codec = new OpenCvImageCodec();
            var store = new InspectionStore(args[2], codec);
            var frame = codec.Decode(File.ReadAllBytes(args[0]));
            var original = store.DeserializeRecipe(File.ReadAllText(args[1]));
            // 隔离实际条码范围，不用重新生成的码图替换原像素。
            var recipe = new InspectionRecipe(
                "barcode-local-print",
                frame.Width,
                frame.Height,
                EInspectionMode.Free,
                EAlignmentMode.AssumeAligned,
                original.Regions.Where(r => r.Kind == ERegionKind.Barcode),
                original.Options
            );
            using var backend = new OpenCvInspectionBackend(barcode: new ZxingBarcodeDecoder());
            using var engine = new InspectionEngine(backend);
            var request = new InspectionRequest(frame, recipe);
            var report = engine.Inspect(request);
            var annotated = codec.Annotate(frame, report);
            string job = store.SaveReport(request, report, annotated);
            int number = 0;
            foreach (var region in report.Analysis.Regions)
            {
                Console.WriteLine(
                    region.RegionName
                        + ": "
                        + string.Join(",", region.Barcodes.Select(b => b.Format + "=" + b.Text))
                );
                foreach (var f in region.Findings)
                {
                    Console.WriteLine(
                        $"  {f.Verdict} {f.Code} box={f.Bounds} area={f.AreaPixels}: {f.Message}"
                    );
                }

                var bounds = recipe.Regions.Single(r => r.Name == region.RegionName).Bounds;
                File.WriteAllBytes(
                    Path.Combine(args[2], "region-" + (++number) + ".annotated.png"),
                    codec.EncodePng(annotated.Crop(bounds))
                );
            }

            Console.WriteLine(
                $"SUMMARY verdict={report.Verdict}; missing={report.Analysis.Regions.Sum(r => r.Findings.Count(f => f.Code == "barcode_missing_ink" || f.Code == "barcode_ink_loss" || f.Code == "qr_missing_ink"))}; extra={report.Analysis.Regions.Sum(r => r.Findings.Count(f => f.Code == "barcode_extra_ink" || f.Code == "qr_extra_ink" || f.Code == "qr_quiet_zone_ink"))}; job={job}"
            );
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}

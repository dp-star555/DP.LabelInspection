using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Runtime;
using DP.LabelInspection.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

public sealed partial class FieldBindingTests
{
    private sealed class Backend(
        string text = "A",
        string barcode = "A",
        int count = 1,
        double confidence = .99,
        double contrast = 255
    ) : IInspectionBackend
    {
        public string Name => "controlled observations";
        public EInspectionCapabilities Capabilities =>
            EInspectionCapabilities.Quality
            | EInspectionCapabilities.Ocr
            | EInspectionCapabilities.CharacterSegmentation
            | EInspectionCapabilities.GlyphComparison
            | EInspectionCapabilities.BarcodeDecode
            | EInspectionCapabilities.BarcodeStructure;

        public BackendAnalysis Analyze(InspectionRequest request, CancellationToken token)
        {
            var rect = request.Recipe.Regions[0].Bounds;
            var recognition = new TextLineRecognition(
                rect,
                "test",
                320,
                48,
                new[] { new CtcStep(1, (float)confidence) },
                new[] { new CtcToken(text, 0, 1, (float)confidence) }
            );
            return new BackendAnalysis(
                contrast,
                100,
                new[]
                {
                    new RegionInspectionResult(
                        "text",
                        new[]
                        {
                            new InspectionFinding(
                                "ocr_identity_review",
                                "raw hypothesis",
                                EInspectionVerdict.Review
                            ),
                        },
                        recognition
                    ),
                    new RegionInspectionResult(
                        "barcode",
                        Array.Empty<InspectionFinding>(),
                        barcodes: Enumerable
                            .Range(0, count)
                            .Select(_ => new BarcodeObservation(
                                barcode,
                                "CODE128",
                                request.Recipe.Regions[1].Bounds
                            ))
                    ),
                }
            );
        }

        public void Dispose() { }
    }
}

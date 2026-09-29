using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using Bridge = DP.LabelInspection.Adapter.Vision.AlgorithmContractAdapter;

namespace DP.LabelInspection.Runtime;

/// <summary>使用可替换中立算法进行分阶段标签检测的运行时组装入口。</summary>
/// <remarks>业务调度由Core负责；除非明确转移所有权，原图和注入算法仍由宿主拥有。</remarks>
public sealed partial class OpenCvInspectionBackend
    : IInspectionBackend,
        IGlyphCandidateBackend,
        IRoiWorkflowBackend
{
    private bool _disposed;
    private readonly DP.Vision.Algorithms.ITextLineRecognizer? _recognizer;
    private readonly bool _ownsRecognizer;
    private readonly IGlyphLibraryRepository? _libraries;
    private readonly DP.Vision.Algorithms.IBarcodeReader? _barcode;
    private readonly DP.Vision.Algorithms.ICharacterSegmenter _segmenter;
    private readonly DP.Vision.Algorithms.ITranslationRegistrar _registrar =
        new DP.Vision.OpenCv.OpenCvTranslationRegistrar();
    private readonly DP.Vision.Algorithms.IGlyphComparer _comparer;
    private readonly DP.Vision.Algorithms.ILinearBarcodeQualityInspector _linearQuality;
    private readonly DP.Vision.Algorithms.IQrQualityInspector _qrQuality;
    private readonly RegionQualityAlgorithms _qualityAlgorithms;
    private readonly DP.Vision.Algorithms.ITextQualityInspector? _textQuality;
    private readonly DP.Vision.Algorithms.ICharacterMatcher _matcher;
    private readonly IAnomalyLibraryRepository? _anomalyModels;
    private readonly AnomalyModelCache _anomalyCache = new AnomalyModelCache();
    private readonly PinnedRevisionCache<GlyphLibrarySnapshot> _glyphLibraries =
        new PinnedRevisionCache<GlyphLibrarySnapshot>(8);
    private readonly Dictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector> _anomalyDetectors;

    /// <summary>除非明确转移所有权，参考及算法均为借用。</summary>
    /// <param name = "recognizer">可选真实单行识别器。</param>
    /// <param name = "ownsRecognizer">是否把识别器释放责任交给后台，默认不转移。</param>
    /// <param name = "libraries">可选固定版本字库仓库，仅借用。</param>
    /// <param name = "barcode">可选Vision原图读码器，仅借用。</param>
    /// <param name = "segmenter">可选物理分割器，null使用默认实现。</param>
    /// <param name = "comparer">可选独立单字比较器，null使用默认实现。</param>
    /// <param name = "linearQuality">可选Vision一维码质量策略，null使用默认实现。</param>
    /// <param name = "qrQuality">可选Vision QR质量策略，null使用默认实现。</param>
    /// <param name = "anomalyModels">可选固定版本异常模型库（方法B），仅借用。</param>
    /// <param name = "anomalyDetectors">
    /// 按特征来源提供的额外异常检测实现（例如CNN骨干网络，键为其FeatureSource），仅借用；手工特征实现始终内置。
    /// </param>
    public OpenCvInspectionBackend(
        DP.Vision.Algorithms.ITextLineRecognizer? recognizer = null,
        bool ownsRecognizer = false,
        IGlyphLibraryRepository? libraries = null,
        DP.Vision.Algorithms.IBarcodeReader? barcode = null,
        DP.Vision.Algorithms.ICharacterSegmenter? segmenter = null,
        DP.Vision.Algorithms.IGlyphComparer? comparer = null,
        DP.Vision.Algorithms.ILinearBarcodeQualityInspector? linearQuality = null,
        DP.Vision.Algorithms.IQrQualityInspector? qrQuality = null,
        IAnomalyLibraryRepository? anomalyModels = null,
        IReadOnlyDictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>? anomalyDetectors = null
    )
        : this(
            new RegionQualityAlgorithms(
                new DP.Vision.OpenCv.OpenCvInkInspector(),
                new DP.Vision.OpenCv.OpenCvInkInspector()
            ),
            recognizer,
            ownsRecognizer,
            libraries,
            barcode,
            segmenter,
            comparer,
            linearQuality,
            qrQuality,
            anomalyModels: anomalyModels,
            anomalyDetectors: anomalyDetectors
        ) { }

    /// <summary>分别选择固定、空白、文字、配对及码策略进行组装，注入实现由宿主拥有。</summary>
    /// <param name = "qualityAlgorithms">宿主明确选择的固定及空白质量算法组合。</param>
    /// <param name = "recognizer">可选真实单行识别器。</param>
    /// <param name = "ownsRecognizer">是否把识别器释放责任交给后台。</param>
    /// <param name = "libraries">可选固定版本字库仓库，仅借用。</param>
    /// <param name = "barcode">可选真实条码解码器，仅借用。</param>
    /// <param name = "segmenter">可选物理分割器，供默认文字组合策略使用。</param>
    /// <param name = "comparer">可选单字比较器，供默认文字组合策略使用。</param>
    /// <param name = "linearQuality">可选Vision一维码质量策略，null使用默认实现。</param>
    /// <param name = "qrQuality">可选Vision QR质量策略，null使用默认实现。</param>
    /// <param name = "textQuality">可选整段文字质量替代策略，null使用默认组合。</param>
    /// <param name = "matcher">可选字符到参考配对策略，供默认文字组合使用。</param>
    /// <param name = "anomalyModels">可选固定版本异常模型库（方法B），仅借用。</param>
    /// <param name = "anomalyDetectors">按特征来源提供的额外异常检测实现，仅借用；手工特征实现始终内置。</param>
    public static OpenCvInspectionBackend WithQualityAlgorithms(
        RegionQualityAlgorithms qualityAlgorithms,
        DP.Vision.Algorithms.ITextLineRecognizer? recognizer = null,
        bool ownsRecognizer = false,
        IGlyphLibraryRepository? libraries = null,
        DP.Vision.Algorithms.IBarcodeReader? barcode = null,
        DP.Vision.Algorithms.ICharacterSegmenter? segmenter = null,
        DP.Vision.Algorithms.IGlyphComparer? comparer = null,
        DP.Vision.Algorithms.ILinearBarcodeQualityInspector? linearQuality = null,
        DP.Vision.Algorithms.IQrQualityInspector? qrQuality = null,
        DP.Vision.Algorithms.ITextQualityInspector? textQuality = null,
        DP.Vision.Algorithms.ICharacterMatcher? matcher = null,
        IAnomalyLibraryRepository? anomalyModels = null,
        IReadOnlyDictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>? anomalyDetectors = null
    )
    {
        return new OpenCvInspectionBackend(
            qualityAlgorithms,
            recognizer,
            ownsRecognizer,
            libraries,
            barcode,
            segmenter,
            comparer,
            linearQuality,
            qrQuality,
            textQuality,
            matcher,
            anomalyModels,
            anomalyDetectors
        );
    }

    private OpenCvInspectionBackend(
        RegionQualityAlgorithms qualityAlgorithms,
        DP.Vision.Algorithms.ITextLineRecognizer? recognizer,
        bool ownsRecognizer,
        IGlyphLibraryRepository? libraries,
        DP.Vision.Algorithms.IBarcodeReader? barcode,
        DP.Vision.Algorithms.ICharacterSegmenter? segmenter,
        DP.Vision.Algorithms.IGlyphComparer? comparer,
        DP.Vision.Algorithms.ILinearBarcodeQualityInspector? linearQuality,
        DP.Vision.Algorithms.IQrQualityInspector? qrQuality,
        DP.Vision.Algorithms.ITextQualityInspector? textQuality = null,
        DP.Vision.Algorithms.ICharacterMatcher? matcher = null,
        IAnomalyLibraryRepository? anomalyModels = null,
        IReadOnlyDictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>? anomalyDetectors = null
    )
    {
        _textQuality = textQuality;
        _anomalyModels = anomalyModels;
        _anomalyDetectors = new Dictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>(
            StringComparer.Ordinal
        )
        {
            [DP.Vision.Algorithms.PatchAnomalyModel.Handcrafted] =
                new DP.Vision.OpenCv.OpenCvPatchAnomalyDetector(),
        };
        foreach (
            var pair in anomalyDetectors
                ?? new Dictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>()
        )
        {
            _anomalyDetectors[pair.Key] = pair.Value ?? throw new ArgumentException("Null anomaly detector.");
        }
        _matcher = matcher ?? new DP.Vision.Algorithms.OrdinalCharacterMatcher();
        _qualityAlgorithms = qualityAlgorithms ?? throw new ArgumentNullException(nameof(qualityAlgorithms));
        _recognizer = recognizer;
        _ownsRecognizer = ownsRecognizer;
        _libraries = libraries;
        _barcode = barcode;
        _segmenter = segmenter ?? new DP.Vision.OpenCv.OpenCvCharacterSegmenter();
        _comparer = comparer ?? new DP.Vision.OpenCv.OpenCvGlyphComparer();
        _linearQuality = linearQuality ?? new DP.Vision.OpenCv.OpenCvBarcodePrintInspector();
        _qrQuality = qrQuality ?? new DP.Vision.OpenCv.OpenCvQrPrintInspector();
    }

    /// <inheritdoc/>
    public GlyphCandidateExtraction ExtractGlyphCandidates(
        DP.Vision.IImageSource frame,
        PixelRect bounds,
        string? confirmedText,
        CancellationToken token
    )
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenCvInspectionBackend));
        }

        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame.Info.Width, frame.Info.Height))
        {
            throw new ArgumentException("Candidate ROI outside image.");
        }

        token.ThrowIfCancellationRequested();
        if (
            confirmedText != null
            && (
                confirmedText.Length == 0
                || confirmedText.Length > 128
                || confirmedText.Any(c => c < 32 || c > 126)
            )
        )
        {
            throw new ArgumentException(
                "Confirmed line must contain 1–128 printable ASCII characters in a single horizontal line; only letters/digits become library candidates."
            );
        }

        TextLineRecognition? recognition = null;
        if (confirmedText == null)
        {
            var reader = _recognizer ?? throw new InvalidOperationException(
                "请先加载OCR模型，或输入人工确认的单行文字后重新切割。 "
            );
            recognition = Bridge.ToLabel(reader.Recognize(frame, bounds, token));
        }

        string text = confirmedText ?? recognition!.Text;
        using var measured = _segmenter is DP.Vision.Algorithms.IGlyphCandidateSegmenter candidates
            ? candidates.SegmentCandidates(frame, bounds, text, token)
            : _segmenter.Segment(frame, bounds, text, token);
        token.ThrowIfCancellationRequested();
        return new GlyphCandidateExtraction(recognition, confirmedText, Bridge.ToLabel(measured));
    }

    /// <inheritdoc/>
    public string Name =>
        "OpenCvSharp 4.10 / staged label inspection"
        + (_recognizer == null ? "" : " + injected OCR");

    /// <inheritdoc/>
    public EInspectionCapabilities Capabilities =>
        EInspectionCapabilities.Quality
        | EInspectionCapabilities.FixedDifference
        | EInspectionCapabilities.BlankSpots
        | EInspectionCapabilities.CharacterSegmentation
        | EInspectionCapabilities.GlyphComparison
        | EInspectionCapabilities.TranslationAlignment
        | EInspectionCapabilities.BarcodeStructure
        | (
            _barcode == null ? EInspectionCapabilities.None : EInspectionCapabilities.BarcodeDecode
        )
        | (
            _recognizer == null ? EInspectionCapabilities.None : EInspectionCapabilities.Ocr
        );

    private static PixelRect LegacyBounds(DP.Vision.RectD bounds, PixelRect scope)
    {
        if (
            bounds.X != Math.Floor(bounds.X)
            || bounds.Y != Math.Floor(bounds.Y)
            || bounds.Width != Math.Floor(bounds.Width)
            || bounds.Height != Math.Floor(bounds.Height)
            || bounds.Width <= 0
            || bounds.Height <= 0
            || bounds.X < scope.X
            || bounds.Y < scope.Y
            || bounds.Right > (long)scope.X + scope.Width
            || bounds.Bottom > (long)scope.Y + scope.Height
        )
        {
            throw new InvalidOperationException(
                "Independent ink measurement returned nonintegral or out-of-scope evidence; it cannot be silently rounded into the legacy report."
            );
        }

        return new PixelRect(
            checked((int)bounds.X),
            checked((int)bounds.Y),
            checked((int)bounds.Width),
            checked((int)bounds.Height)
        );
    }

    /// <summary>只释放明确转移的依赖，不保留原始图像帧。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsRecognizer)
        {
            _recognizer?.Dispose();
        }
    }
}

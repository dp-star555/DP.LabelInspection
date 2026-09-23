using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.LabelInspection.Contracts;
using OpenCvSharp;

namespace DP.LabelInspection.Runtime;

/// <summary>使用可替换中立算法进行分阶段标签检测的运行时组装入口。</summary>
/// <remarks>业务调度由Core负责；除非明确转移所有权，原图和注入算法仍由宿主拥有。</remarks>
public sealed partial class OpenCvInspectionBackend
    : IInspectionBackend,
        IGlyphCandidateBackend,
        IRoiWorkflowBackend
{
    private bool _disposed;
    private readonly ITextLineRecognizer? _recognizer;
    private readonly bool _ownsRecognizer;
    private readonly IGlyphLibraryRepository? _libraries;
    private readonly IBarcodeDecoder? _barcode;
    private readonly ITextRegionDetector? _detector;
    private readonly bool _ownsDetector;
    private readonly ICharacterSegmenter _segmenter;
    private readonly IGlyphComparer _comparer;
    private readonly IBarcodePrintInspector _barcodePrint;
    private readonly RegionQualityAlgorithms _qualityAlgorithms;
    private readonly DP.Vision.Algorithms.ITextQualityInspector? _textQuality;
    private readonly DP.Vision.Algorithms.ICharacterMatcher _matcher;

    /// <summary>除非明确转移所有权，参考及算法均为借用。</summary>
    /// <param name = "recognizer">可选真实单行识别器。</param>
    /// <param name = "ownsRecognizer">是否把识别器释放责任交给后台，默认不转移。</param>
    /// <param name = "libraries">可选固定版本字库仓库，仅借用。</param>
    /// <param name = "barcode">可选真实条码解码器，仅借用。</param>
    /// <param name = "detector">可选文本候选检测器。</param>
    /// <param name = "ownsDetector">是否把检测器释放责任交给后台，默认不转移。</param>
    /// <param name = "segmenter">可选物理分割器，null使用默认实现。</param>
    /// <param name = "comparer">可选独立单字比较器，null使用默认实现。</param>
    /// <param name = "barcodePrint">可选码印刷策略，null使用默认实现。</param>
    public OpenCvInspectionBackend(
        ITextLineRecognizer? recognizer = null,
        bool ownsRecognizer = false,
        IGlyphLibraryRepository? libraries = null,
        IBarcodeDecoder? barcode = null,
        ITextRegionDetector? detector = null,
        bool ownsDetector = false,
        ICharacterSegmenter? segmenter = null,
        IGlyphComparer? comparer = null,
        IBarcodePrintInspector? barcodePrint = null
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
            detector,
            ownsDetector,
            segmenter,
            comparer,
            barcodePrint
        ) { }

    /// <summary>分别选择固定、空白、文字、配对及码策略进行组装，注入实现由宿主拥有。</summary>
    /// <param name = "qualityAlgorithms">宿主明确选择的固定及空白质量算法组合。</param>
    /// <param name = "recognizer">可选真实单行识别器。</param>
    /// <param name = "ownsRecognizer">是否把识别器释放责任交给后台。</param>
    /// <param name = "libraries">可选固定版本字库仓库，仅借用。</param>
    /// <param name = "barcode">可选真实条码解码器，仅借用。</param>
    /// <param name = "detector">可选文本候选检测器。</param>
    /// <param name = "ownsDetector">是否把检测器释放责任交给后台。</param>
    /// <param name = "segmenter">可选物理分割器，供默认文字组合策略使用。</param>
    /// <param name = "comparer">可选单字比较器，供默认文字组合策略使用。</param>
    /// <param name = "barcodePrint">可选码印刷策略，null使用默认实现。</param>
    /// <param name = "textQuality">可选整段文字质量替代策略，null使用默认组合。</param>
    /// <param name = "matcher">可选字符到参考配对策略，供默认文字组合使用。</param>
    public static OpenCvInspectionBackend WithQualityAlgorithms(
        RegionQualityAlgorithms qualityAlgorithms,
        ITextLineRecognizer? recognizer = null,
        bool ownsRecognizer = false,
        IGlyphLibraryRepository? libraries = null,
        IBarcodeDecoder? barcode = null,
        ITextRegionDetector? detector = null,
        bool ownsDetector = false,
        ICharacterSegmenter? segmenter = null,
        IGlyphComparer? comparer = null,
        IBarcodePrintInspector? barcodePrint = null,
        DP.Vision.Algorithms.ITextQualityInspector? textQuality = null,
        DP.Vision.Algorithms.ICharacterMatcher? matcher = null
    )
    {
        return new OpenCvInspectionBackend(
            qualityAlgorithms,
            recognizer,
            ownsRecognizer,
            libraries,
            barcode,
            detector,
            ownsDetector,
            segmenter,
            comparer,
            barcodePrint,
            textQuality,
            matcher
        );
    }

    private OpenCvInspectionBackend(
        RegionQualityAlgorithms qualityAlgorithms,
        ITextLineRecognizer? recognizer,
        bool ownsRecognizer,
        IGlyphLibraryRepository? libraries,
        IBarcodeDecoder? barcode,
        ITextRegionDetector? detector,
        bool ownsDetector,
        ICharacterSegmenter? segmenter,
        IGlyphComparer? comparer,
        IBarcodePrintInspector? barcodePrint,
        DP.Vision.Algorithms.ITextQualityInspector? textQuality = null,
        DP.Vision.Algorithms.ICharacterMatcher? matcher = null
    )
    {
        _textQuality = textQuality;
        _matcher = matcher ?? new DP.Vision.Algorithms.OrdinalCharacterMatcher();
        _qualityAlgorithms = qualityAlgorithms ?? throw new ArgumentNullException(nameof(qualityAlgorithms));
        _recognizer = recognizer;
        _ownsRecognizer = ownsRecognizer;
        _libraries = libraries;
        _barcode = barcode;
        _detector = detector;
        _ownsDetector = ownsDetector;
        _segmenter = segmenter ?? new CharacterSegmenter();
        _comparer = comparer ?? new GlyphComparer();
        _barcodePrint = barcodePrint ?? new OpenCvBarcodePrintInspector();
    }

    /// <inheritdoc/>
    public GlyphCandidateExtraction ExtractGlyphCandidates(
        ImageFrame frame,
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

        if (!bounds.Fits(frame))
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
            recognition = (
                _recognizer
                ?? throw new InvalidOperationException(
                    "请先加载OCR模型，或输入人工确认的单行文字后重新切割。 "
                )
            ).Recognize(frame, bounds, token);
        }

        string text = confirmedText ?? recognition!.Text;
        var segmentation = _segmenter is IGlyphCandidateSegmenter candidates
            ? candidates.SegmentCandidates(frame, bounds, text, token)
            : _segmenter.Segment(frame, bounds, text, token);
        token.ThrowIfCancellationRequested();
        return new GlyphCandidateExtraction(recognition, confirmedText, segmentation);
    }

    /// <inheritdoc/>
    public string Name =>
        "OpenCvSharp 4.10 / staged label inspection" + (_recognizer == null ? "" : " + injected OCR");

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
            _detector != null || _barcode != null
                ? EInspectionCapabilities.Discovery
                : EInspectionCapabilities.None
        )
        | (_barcode == null ? EInspectionCapabilities.None : EInspectionCapabilities.BarcodeDecode)
        | (_recognizer == null ? EInspectionCapabilities.None : EInspectionCapabilities.Ocr);

    /// <summary>既有分析入口委托到同一分阶段Core，不另建第二套单体流程。</summary>
    /// <param name = "request">不可变检测请求，交由Core执行相同的分阶段流程。</param>
    /// <param name = "cancellationToken">协作式取消标记。</param>
    public BackendAnalysis Analyze(InspectionRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenCvInspectionBackend));
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var engine = new DP.LabelInspection.Core.InspectionEngine(this, ownsBackend: false);
        return engine.Inspect(request, cancellationToken).Analysis;
    }

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

    private static Mat Gray(ImageFrame frame)
    {
        var bytes = frame.CopyPixels();
        using var raw = new Mat(
            frame.Height,
            frame.Width,
            frame.Format == EImagePixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC3
        );
        Marshal.Copy(bytes, 0, raw.Data, bytes.Length);
        if (frame.Format == EImagePixelFormat.Gray8)
        {
            return raw.Clone();
        }

        var output = new Mat();
        try
        {
            Cv2.CvtColor(raw, output, ColorConversionCodes.BGR2GRAY);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>只释放明确转移的依赖，不保留原始图像帧。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ownsRecognizer)
            {
                _recognizer?.Dispose();
            }
        }
        finally
        {
            if (_ownsDetector)
            {
                _detector?.Dispose();
            }
        }
    }
}

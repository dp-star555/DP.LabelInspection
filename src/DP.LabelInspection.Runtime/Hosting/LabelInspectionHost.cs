using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.LabelInspection.Contracts;
using DP.LabelInspection.Core;
using DP.LabelInspection.Storage;

namespace DP.LabelInspection.Runtime;

/// <summary>
/// 宿主组装根：统一创建存储、图像编解码、方法B训练实现与模板定位，并按需创建检测引擎，
/// 使WinForms、WPF和生产宿主的组装方式一致。本对象拥有它创建的CNN骨干网络；引擎由调用方释放。
/// </summary>
public sealed class LabelInspectionHost : IDisposable
{
    private readonly DP.Vision.OpenCv.OpenCvCnnPatchAnomalyDetector? _cnn;
    private readonly Dictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector> _anomalyDetectors;
    private readonly DP.Vision.Algorithms.ITextLineRecognizer? _borrowedRecognizer;
    private readonly DP.Vision.Algorithms.IAnomalyImplementation[] _implementations;
    private bool _disposed;

    private LabelInspectionHost(LabelInspectionHostOptions options,
        DP.Vision.Algorithms.ITextLineRecognizer? borrowedRecognizer = null,
        string? anomalyFeatureSource = null, DP.Vision.Algorithms.IPatchAnomalyDetector? borrowedAnomaly = null)
    {
        Codec = new OpenCvImageCodec();
        Store = new InspectionStore(options.DataRoot, Codec);
        foreach (string seed in options.SeedLibraryFiles)
        {
            Store.InstallSeed(File.ReadAllText(seed));
        }

        _anomalyDetectors = new Dictionary<string, DP.Vision.Algorithms.IPatchAnomalyDetector>(
            StringComparer.Ordinal
        );
        _borrowedRecognizer = borrowedRecognizer;
        if (borrowedAnomaly != null)
            _anomalyDetectors[anomalyFeatureSource!] = borrowedAnomaly;
        else if (!string.IsNullOrWhiteSpace(options.AnomalyBackbone))
        {
            _cnn = new DP.Vision.OpenCv.OpenCvCnnPatchAnomalyDetector(options.AnomalyBackbone!);
            _anomalyDetectors[_cnn.FeatureSource] = _cnn;
        }

        var trainer = borrowedAnomaly ?? _cnn;
        AnomalyTrainer = trainer == null ? new RegionAnomalyDetector() : new RegionAnomalyDetector(trainer);
        _implementations = new DP.Vision.Algorithms.AnomalyImplementationRegistry(options.AnomalyImplementations).Implementations.ToArray();
        var trainers = new Dictionary<string, IAnomalyModelTrainer>(StringComparer.Ordinal) { [trainer == null ? "OpenCV · 手工Patch" : "OpenCV · CNN Patch"] = AnomalyTrainer };
        foreach (var implementation in _implementations)
            if (implementation is DP.Vision.Algorithms.IAnomalyTrainer) trainers.Add(implementation.DisplayName, new RegionAnomalyDetector(implementation));
        AnomalyTrainers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IAnomalyModelTrainer>(trainers);
        TemplateLocator = new DP.Vision.OpenCv.OpenCvTemplateLocator();
        RecognitionModel = options.RecognitionModel;
        MaximumParallelRois = options.MaximumParallelRois;
    }

    /// <summary>按参数组装宿主。</summary>
    /// <param name = "options">组装参数。</param>
    public static LabelInspectionHost Create(LabelInspectionHostOptions options)
    {
        return new LabelInspectionHost(options ?? throw new ArgumentNullException(nameof(options)));
    }

    /// <summary>使用外部模型租约装配宿主；宿主和引擎不释放借用的模型。</summary>
    /// <param name="options">存储和执行配置；未借用的模型仍可按路径独立创建。</param>
    /// <param name="recognizer">借用的OCR识别器；null时保持按路径创建的行为。</param>
    /// <param name="anomalyFeatureSource">借用异常检测器的特征来源，与库模型身份一致。</param>
    /// <param name="anomalyDetector">借用的异常骨干检测器；调用方负责串行化和释放。</param>
    /// <returns>不拥有外部模型的宿主；调用方必须先释放引擎，再归还模型租约。</returns>
    public static LabelInspectionHost CreateWithBorrowedModels(LabelInspectionHostOptions options,
        DP.Vision.Algorithms.ITextLineRecognizer? recognizer,
        string? anomalyFeatureSource, DP.Vision.Algorithms.IPatchAnomalyDetector? anomalyDetector)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (anomalyDetector != null && string.IsNullOrWhiteSpace(anomalyFeatureSource))
            throw new ArgumentException("借用异常检测器必须提供特征来源。", nameof(anomalyFeatureSource));
        return new LabelInspectionHost(options, recognizer, anomalyFeatureSource, anomalyDetector);
    }

    /// <summary>PNG编解码与报告标注图。</summary>
    public OpenCvImageCodec Codec { get; }

    /// <summary>配方、报告、单字库与异常模型库存储。</summary>
    public InspectionStore Store { get; }

    /// <summary>方法B训练实现（手工特征，或配置了骨干网络时的CNN特征）。</summary>
    public IAnomalyModelTrainer AnomalyTrainer { get; }

    /// <summary>可选训练能力（不包含仅推理实现）；选择训练器不修改生产配方绑定。</summary>
    public IReadOnlyDictionary<string, IAnomalyModelTrainer> AnomalyTrainers { get; }

    /// <summary>批量训练样本框自动对齐所用的模板定位。</summary>
    public DP.Vision.Algorithms.ITemplateLocator TemplateLocator { get; }

    /// <summary>创建的引擎每次检测最多并行执行的ROI数。</summary>
    public int MaximumParallelRois { get; }

    /// <summary>组装时指定的OCR模型路径。</summary>
    public string? RecognitionModel { get; }

    /// <summary>
    /// 创建一个拥有其后台的检测引擎：单字库与异常模型库来自<see cref = "Store"/>，读码使用ZXing，
    /// 指定OCR模型时加载识别器（由后台拥有）。可多次调用，例如更换OCR模型；旧引擎由调用方释放。
    /// </summary>
    /// <param name = "recognitionModel">OCR模型路径；null时使用组装参数中的模型，仍为null则不加载OCR。</param>
    public InspectionEngine CreateEngine(string? recognitionModel = null) =>
        CreateEngineFromRepositories(Store, Store.AnomalyLibraries, recognitionModel);

    /// <summary>使用宿主捕获的不可变资源仓创建引擎，检测过程中不重新从可变目录读取库版本。</summary>
    /// <param name="libraries">本轮字库快照仓，不由引擎释放。</param>
    /// <param name="anomalyModels">本轮异常库快照仓，不由引擎释放。</param>
    /// <param name="recognitionModel">可选识别模型；空时使用宿主配置。</param>
    /// <param name="preloadAnomalyModels">生产选定资产预热；null保持按需加载。</param>
    /// <param name="cancellationToken">模型装配取消，原生运行安全返回后释放。</param>
    /// <returns>由调用方拥有的检测引擎。</returns>
    public InspectionEngine CreateEngineFromRepositories(IGlyphLibraryRepository libraries,
        IAnomalyLibraryRepository anomalyModels, string? recognitionModel = null, IEnumerable<AnomalyModelEntry>? preloadAnomalyModels = null, System.Threading.CancellationToken cancellationToken = default)
    {
        if (libraries == null) throw new ArgumentNullException(nameof(libraries));
        if (anomalyModels == null) throw new ArgumentNullException(nameof(anomalyModels));
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LabelInspectionHost));
        }

        string? model = recognitionModel ?? RecognitionModel;
        var borrowed = recognitionModel == null && _borrowedRecognizer != null;
        var recognizer = borrowed ? _borrowedRecognizer : string.IsNullOrWhiteSpace(model)
            ? null
            : new DP.Vision.PPOcr.Onnx.OnnxTextLineRecognizer(
                model!,
                new DP.Vision.OpenCv.OpenCvTextLinePreprocessor()
            );
        try
        {
            var backend = new OpenCvInspectionBackend(
                    ownsRecognizer: !borrowed,
                    libraries: libraries,
                    anomalyModels: anomalyModels,
                    anomalyDetectors: _anomalyDetectors,
                    anomalyImplementations: _implementations,
                    barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder(),
                    recognizer: recognizer
                );
            try
            {
                if (preloadAnomalyModels != null) backend.PrepareAnomalyModels(preloadAnomalyModels, cancellationToken);
                return new InspectionEngine(backend, true) { MaximumParallelRois = MaximumParallelRois };
            }
            catch { backend.Dispose(); throw; }
        }
        catch
        {
            if (!borrowed) recognizer?.Dispose();
            throw;
        }
    }

    /// <summary>保存批量训练采集（异常模型训练工程）。</summary>
    /// <param name = "session">采集会话。</param>
    /// <param name = "path">工程文件路径。</param>
    public void SaveTrainingProject(AnomalyTrainingSession session, string path)
    {
        AnomalyTrainingProject.Save(session, path, Codec);
    }

    /// <summary>打开批量训练采集，并接上本宿主的模板定位。</summary>
    /// <param name = "path">工程文件路径。</param>
    /// <param name = "regions">当前配方ROI。</param>
    public (
        AnomalyTrainingSession Session,
        IReadOnlyDictionary<AnomalyTrainingSample, int[]> Excluded
    ) LoadTrainingProject(string path, IEnumerable<InspectionRegion> regions)
    {
        var session = AnomalyTrainingProject.Load(path, Codec, regions, out var excluded);
        session.Locator = TemplateLocator;
        return (session, excluded);
    }

    /// <summary>释放本宿主创建的CNN骨干网络；已创建的引擎由调用方先释放。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cnn?.Dispose();
    }
}

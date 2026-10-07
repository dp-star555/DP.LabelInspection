using System;
using System.Collections.Generic;
using System.IO;
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
    private bool _disposed;

    private LabelInspectionHost(LabelInspectionHostOptions options)
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
        if (!string.IsNullOrWhiteSpace(options.AnomalyBackbone))
        {
            _cnn = new DP.Vision.OpenCv.OpenCvCnnPatchAnomalyDetector(options.AnomalyBackbone!);
            _anomalyDetectors[_cnn.FeatureSource] = _cnn;
        }

        AnomalyTrainer = _cnn == null ? new RegionAnomalyDetector() : new RegionAnomalyDetector(_cnn);
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

    /// <summary>PNG编解码与报告标注图。</summary>
    public OpenCvImageCodec Codec { get; }

    /// <summary>配方、报告、单字库与异常模型库存储。</summary>
    public InspectionStore Store { get; }

    /// <summary>方法B训练实现（手工特征，或配置了骨干网络时的CNN特征）。</summary>
    public IAnomalyModelTrainer AnomalyTrainer { get; }

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
    /// <returns>由调用方拥有的检测引擎。</returns>
    public InspectionEngine CreateEngineFromRepositories(IGlyphLibraryRepository libraries,
        IAnomalyLibraryRepository anomalyModels, string? recognitionModel = null)
    {
        if (libraries == null) throw new ArgumentNullException(nameof(libraries));
        if (anomalyModels == null) throw new ArgumentNullException(nameof(anomalyModels));
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LabelInspectionHost));
        }

        string? model = recognitionModel ?? RecognitionModel;
        var recognizer = string.IsNullOrWhiteSpace(model)
            ? null
            : new DP.Vision.PPOcr.Onnx.OnnxTextLineRecognizer(
                model!,
                new DP.Vision.OpenCv.OpenCvTextLinePreprocessor()
            );
        try
        {
            return new InspectionEngine(
                new OpenCvInspectionBackend(
                    ownsRecognizer: true,
                    libraries: libraries,
                    anomalyModels: anomalyModels,
                    anomalyDetectors: _anomalyDetectors,
                    barcode: new DP.Vision.Zxing.ZxingBarcodeDecoder(),
                    recognizer: recognizer
                ),
                true
            )
            {
                MaximumParallelRois = MaximumParallelRois,
            };
        }
        catch
        {
            recognizer?.Dispose();
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>一个独立区域；不支持的类型必须明确报告，不能静默跳过，具体失败状态由执行策略决定。</summary>
public sealed class InspectionRegion
{
    /// <summary>创建检测区域。</summary>
    /// <param name = "name">唯一且非空的名称。</param>
    /// <param name = "kind">检查类型。</param>
    /// <param name = "bounds">原图像素范围。</param>
    /// <param name = "singleLine">是否明确声明水平单行，不是自动检测的多行文本。</param>
    /// <param name = "field">可选内容规则及固定版本的字符类别。</param>
    /// <param name = "anomaly">可选固定版本异常模型绑定（方法B），适用于除忽略区外的所有类型。</param>
    public InspectionRegion(
        string name,
        ERegionKind kind,
        PixelRect bounds,
        bool singleLine = false,
        FieldSettings? field = null,
        AnomalySettings? anomaly = null
    )
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            throw new ArgumentException("A region needs a name.", nameof(name));
        }

        if (!Enum.IsDefined(typeof(ERegionKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("Empty region.", nameof(bounds));
        }

        if (singleLine && kind != ERegionKind.Text)
        {
            throw new ArgumentException("Single-line layout requires Text.", nameof(singleLine));
        }

        if (field != null && kind != ERegionKind.Text && kind != ERegionKind.Barcode)
        {
            throw new ArgumentException("Field settings require text or barcode.");
        }

        if (kind == ERegionKind.Barcode && field?.EqualCells == true)
        {
            throw new ArgumentException("Equal cells require text.");
        }

        if (anomaly != null && kind == ERegionKind.Ignore)
        {
            throw new ArgumentException("Ignore regions take no anomaly model.", nameof(anomaly));
        }

        Name = name;
        Kind = kind;
        Anomaly = anomaly;
        Bounds = bounds;
        SingleLine = singleLine;
        Field = field ?? new FieldSettings();
        Tasks = new RoiInspectionTasks(
            kind == ERegionKind.Barcode || kind == ERegionKind.Text && !Field.EqualCells,
            kind == ERegionKind.Fixed
                || kind == ERegionKind.Blank
                || kind == ERegionKind.Text && (Field.LibraryId != null || Field.EqualCells)
                || kind == ERegionKind.Barcode && Field.BarcodePrint.Enabled
        );
    }

    private RoiInspectionTasks _tasks = null!;

    /// <summary>检测项目的唯一配置依据；旧配方按类型补默认值，明确赋值时同步历史码质量标志。</summary>
    public RoiInspectionTasks Tasks
    {
        get => _tasks;
        private set
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (Kind == ERegionKind.Barcode && Field.BarcodePrint.Enabled != value.CheckQuality)
            {
                var f = Field;
                var p = f.BarcodePrint;
                Field = new FieldSettings(
                    f.LibraryId,
                    f.LibraryRevision,
                    f.Expected,
                    f.Pattern,
                    f.AllowedCharacters,
                    f.MinimumLength,
                    f.MaximumLength,
                    f.EqualCells,
                    f.MaximumDifference,
                    f.GlyphTolerance,
                    f.MinimumConfidence,
                    new BarcodePrintOptions(
                        value.CheckQuality,
                        p.MinimumArea,
                        p.MinimumFraction,
                        p.EdgeTolerance,
                        p.CheckQrQuietZone,
                        p.DetectInkLoss,
                        p.MinimumInkLoss
                    ),
                    f.BarcodeType
                );
            }

            _tasks = value;
        }
    }

    /// <summary>复制ROI并独立选择检测项目，保留其余配置。</summary>
    /// <param name = "tasks">明确选择的数据及质量项目，生成新ROI，不修改当前实例。</param>
    public InspectionRegion WithTasks(RoiInspectionTasks tasks)
    {
        return new InspectionRegion(
            Name,
            Kind,
            Bounds,
            SingleLine,
            Kind == ERegionKind.Text || Kind == ERegionKind.Barcode ? Field : null,
            Anomaly
        )
        {
            Tasks = tasks ?? throw new ArgumentNullException(nameof(tasks)),
        };
    }

    /// <summary>复制ROI并替换异常模型绑定（方法B），保留检测项目及其余配置。</summary>
    /// <param name = "anomaly">新的固定版本绑定；null表示解除绑定。</param>
    public InspectionRegion WithAnomaly(AnomalySettings? anomaly)
    {
        return new InspectionRegion(
            Name,
            Kind,
            Bounds,
            SingleLine,
            Kind == ERegionKind.Text || Kind == ERegionKind.Barcode ? Field : null,
            anomaly
        )
        {
            Tasks = Tasks,
        };
    }

    /// <summary>固定版本异常模型绑定（方法B）；未绑定时为null。</summary>
    public AnomalySettings? Anomaly { get; }

    /// <summary>不可变字段规则及固定版本的字库绑定。</summary>
    public FieldSettings Field { get; private set; }

    /// <summary>仅明确声明的水平单行文字ROI为true。</summary>
    public bool SingleLine { get; }

    /// <summary>区域标识。</summary>
    public string Name { get; }

    /// <summary>检查类型。</summary>
    public ERegionKind Kind { get; }

    /// <summary>原图像素矩形。</summary>
    public PixelRect Bounds { get; }
}

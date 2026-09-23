using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.LabelInspection.Contracts;

/// <summary>原始输入坐标中的诊断证据；代码用于稳定识别，消息只用于解释。</summary>
public sealed class InspectionFinding
{
    /// <summary>创建诊断证据；定位缺陷与执行阻断必须明确区分。</summary>
    /// <param name = "code">稳定的机器可读诊断代码。</param>
    /// <param name = "message">面向用户的说明，不参与判定计算。</param>
    /// <param name = "verdict">该条证据的严重程度。</param>
    /// <param name = "bounds">可选原图像素范围，不是归一化字符坐标。</param>
    /// <param name = "areaPixels">有实际意义时提供的原图面积，单位为平方像素。</param>
    public InspectionFinding(
        string code,
        string message,
        EInspectionVerdict verdict,
        PixelRect? bounds = null,
        int? areaPixels = null
    )
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Finding code is required.", nameof(code));
        }

        if (!Enum.IsDefined(typeof(EInspectionVerdict), verdict))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict));
        }

        if (areaPixels < 0 || (bounds.HasValue && (bounds.Value.Width < 1 || bounds.Value.Height < 1)))
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        Code = code;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Verdict = verdict;
        Bounds = bounds;
        AreaPixels = areaPixels;
    }

    /// <summary>机器可读的稳定诊断标识。</summary>
    public string Code { get; }

    /// <summary>解释性文本，不能通过解析消息推导判定。</summary>
    public string Message { get; }

    /// <summary>本条证据的严重程度。</summary>
    public EInspectionVerdict Verdict { get; }

    /// <summary>原始输入图像中的位置，不是归一化字符坐标。</summary>
    public PixelRect? Bounds { get; }

    /// <summary>有物理意义时提供的原图平方像素面积。</summary>
    public int? AreaPixels { get; }

    /// <summary>显式的算法或调度阻断标志；即使带范围框，也不能计为可定位印刷缺陷。</summary>
    public bool IsExecutionBlocker { get; private set; }

    /// <summary>返回带明确阻断角色的新副本，避免严重程度适配丢失执行语义。</summary>
    /// <param name = "value">是否标记为执行阻断，默认true。</param>
    /// <returns>保留原始代码、消息、位置和面积的新证据，不修改当前实例。</returns>
    public InspectionFinding WithExecutionBlocker(bool value = true)
    {
        return new InspectionFinding(Code, Message, Verdict, Bounds, AreaPixels)
        {
            IsExecutionBlocker = value,
        };
    }
}

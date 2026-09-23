using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>与UI无关的检测输入，不要求文件路径或原生图像对象。</summary>
public sealed class InspectionRequest
{
    /// <summary>创建不可变请求。</summary>
    /// <param name = "actual">独立图像快照。</param>
    /// <param name = "recipe">固定配方。</param>
    /// <param name = "reference">可选参考快照。</param>
    /// <param name = "cycleId">宿主采集周期标识，用于匹配业务数据。</param>
    /// <param name = "taskData">可选的本周期不可变数据。</param>
    public InspectionRequest(
        ImageFrame actual,
        InspectionRecipe recipe,
        ImageFrame? reference = null,
        string? cycleId = null,
        TaskDataSnapshot? taskData = null
    )
    {
        Actual = actual ?? throw new ArgumentNullException(nameof(actual));
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (cycleId != null && (string.IsNullOrWhiteSpace(cycleId) || cycleId.Length > 200))
        {
            throw new ArgumentException("Invalid capture-cycle identity.", nameof(cycleId));
        }

        Reference = reference;
        CycleId = cycleId;
        TaskData = taskData;
    }

    /// <summary>宿主独立提供的采集周期。</summary>
    public string? CycleId { get; }

    /// <summary>固定的外部预期值，不从OCR推断。</summary>
    public TaskDataSnapshot? TaskData { get; }

    /// <summary>实际图像。</summary>
    public ImageFrame Actual { get; }

    /// <summary>配方快照。</summary>
    public InspectionRecipe Recipe { get; }

    /// <summary>可选的整张标签参考。</summary>
    public ImageFrame? Reference { get; }
}

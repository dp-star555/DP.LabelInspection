using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>
/// 每个ROI显式选择的检测项目；算法能力缺失不能静默取消已选要求。
/// 质量判断有两种彼此独立、可单独或同时启用的方法：A为按ROI类型的规则质检（单字比对、码印刷、固定/空白墨迹比较），
/// B为仅用良品训练的局部块异常检测；同时启用时任一方法NG即该ROI质量NG。
/// </summary>
public sealed class RoiInspectionTasks
{
    /// <summary>创建彼此独立的数据读取和印刷质量要求。</summary>
    /// <param name = "readData">是否要求实际OCR或码数据读取。</param>
    /// <param name = "checkQuality">是否要求完整执行按类型的规则质检（方法A）。</param>
    /// <param name = "detectAnomaly">是否要求执行局部块异常检测（方法B），需绑定固定版本的异常模型；旧配方缺省为false。</param>
    public RoiInspectionTasks(bool readData, bool checkQuality, bool detectAnomaly = false)
    {
        ReadData = readData;
        CheckQuality = checkQuality;
        DetectAnomaly = detectAnomaly;
    }

    /// <summary>是否要求真实OCR或码数据；等格字符标签本身不等于实际读取结果。</summary>
    public bool ReadData { get; }

    /// <summary>是否要求完整执行按ROI类型的规则质检（方法A）。</summary>
    public bool CheckQuality { get; }

    /// <summary>是否要求执行局部块异常检测（方法B），与方法A相互独立。</summary>
    public bool DetectAnomaly { get; }
}

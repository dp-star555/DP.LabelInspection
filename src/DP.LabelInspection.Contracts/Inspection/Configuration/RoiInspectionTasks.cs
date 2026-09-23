using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>每个ROI显式选择的检测项目；算法能力缺失不能静默取消已选要求。</summary>
public sealed class RoiInspectionTasks
{
    /// <summary>创建彼此独立的数据读取和印刷质量要求。</summary>
    /// <param name = "readData">是否要求实际OCR或码数据读取。</param>
    /// <param name = "checkQuality">是否要求完整执行印刷质量检查。</param>
    public RoiInspectionTasks(bool readData, bool checkQuality)
    {
        ReadData = readData;
        CheckQuality = checkQuality;
    }

    /// <summary>是否要求真实OCR或码数据；等格字符标签本身不等于实际读取结果。</summary>
    public bool ReadData { get; }

    /// <summary>是否要求完整执行印刷质量测量。</summary>
    public bool CheckQuality { get; }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>分阶段后台契约；Core负责ROI调度及内容关卡，后台负责具体算法。</summary>
public interface IRoiWorkflowBackend
{
    /// <summary>为一次请求创建会话；在ROI前提检查完成前不执行图像算法。</summary>
    /// <param name = "request">本轮不可变请求，包含原图、配方及已固定的参考。</param>
    /// <returns>本轮专用会话，调用方结束后必须Dispose。</returns>
    IRoiInspectionSession OpenSession(InspectionRequest request);
}

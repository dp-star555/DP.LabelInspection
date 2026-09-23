using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.LabelInspection.Contracts;

/// <summary>可选的码质量策略结构读取依赖声明。</summary>
public interface IBarcodePrintRequirements
{
    /// <summary>质量检查无法脱离解码器结构或码族信息执行时为true。</summary>
    /// <param name = "kind">配置要求的码族，判断该质量策略是否需要预先读取结构。</param>
    bool RequiresReading(EBarcodeKind kind);
}

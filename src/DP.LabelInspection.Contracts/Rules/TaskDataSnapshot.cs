using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.LabelInspection.Contracts;

/// <summary>从宿主管理的来源复制的本周期数据，不由SDK自行获取。</summary>
public sealed class TaskDataSnapshot
{
    /// <summary>创建有界且会过期的业务数据快照；Source用于溯源，不是要访问的网络地址。</summary>
    /// <param name = "cycleId">宿主关联的采集周期标识，须与请求一致。</param>
    /// <param name = "source">数据来源说明，仅用于溯源，不会作为网络地址执行。</param>
    /// <param name = "capturedAt">数据获取时间。</param>
    /// <param name = "validUntil">明确有效期结束时间。</param>
    /// <param name = "values">本周期字段集合，按序数键复制为不可变快照。</param>
    public TaskDataSnapshot(
        string cycleId,
        string source,
        DateTimeOffset capturedAt,
        DateTimeOffset validUntil,
        IReadOnlyDictionary<string, string> values
    )
    {
        if (
            string.IsNullOrWhiteSpace(cycleId)
            || string.IsNullOrWhiteSpace(source)
            || cycleId.Length > 200
            || source.Length > 500
            || validUntil < capturedAt
            || values == null
            || values.Count > 128
        )
        {
            throw new ArgumentException("Invalid task snapshot.");
        }

        if (
            values.Any(p =>
                string.IsNullOrWhiteSpace(p.Key)
                || p.Key.Length > 200
                || p.Value == null
                || p.Value.Length > 4096
            )
        )
        {
            throw new ArgumentException("Invalid task field.");
        }

        CycleId = cycleId;
        Source = source;
        CapturedAt = capturedAt;
        ValidUntil = validUntil;
        Values = new ReadOnlyDictionary<string, string>(
            values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
        );
    }

    /// <summary>宿主为这些值关联的采集周期。</summary>
    public string CycleId { get; }

    /// <summary>来源说明，例如MES或工单标识。</summary>
    public string Source { get; }

    /// <summary>数据获取时间戳。</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>明确的过期时间。</summary>
    public DateTimeOffset ValidUntil { get; }

    /// <summary>采用序数键比较的不可变字段值集合。</summary>
    public IReadOnlyDictionary<string, string> Values { get; }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Core;

/// <summary>按ROI执行前提、读取、内容比较和质量阶段；单个ROI失败不会终止其他独立ROI。</summary>
internal static class RoiWorkflow
{
    /// <summary>调度本轮检测，按依赖顺序处理引导源，再按配方顺序返回区域结果。</summary>
    /// <param name = "request">本轮不可变原图、配方、引导数据及参考资源。</param>
    /// <param name = "backend">提供能力和模型元数据的后台，应与staged对应同一实现。</param>
    /// <param name = "staged">用于创建本轮算法会话的分阶段后台。</param>
    /// <param name = "token">协作式取消标记；取消传播到调用方，不转换成局部缺陷。</param>
    /// <returns>包含全部配置ROI结果和明确执行状态的报告；无有效项目不能放行。</returns>
    internal static InspectionReport Run(
        InspectionRequest request,
        IInspectionBackend backend,
        IRoiWorkflowBackend staged,
        CancellationToken token
    )
    {
        var watch = Stopwatch.StartNew();
        var configured = request.Recipe.Regions.Where(r => r.Kind != ERegionKind.Ignore).ToArray();
        using var session = staged.OpenSession(request);
        var results = new Dictionary<string, RegionInspectionResult>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roi in configured)
        {
            token.ThrowIfCancellationRequested();
            Process(roi);
        }

        var global = new List<InspectionFinding>();
        if (configured.Length == 0)
        {
            global.Add(
                new InspectionFinding(
                    "no_inspection_tasks",
                    "没有有效的检测ROI，不能空跑后放行。",
                    EInspectionVerdict.Ng
                )
            );
        }

        var ordered = configured.Select(r => results[r.Name]).ToArray();
        var verdict = global
            .Concat(ordered.SelectMany(r => r.Findings))
            .Any(f => f.Verdict != EInspectionVerdict.Ok)
            ? EInspectionVerdict.Ng
            : EInspectionVerdict.Ok;
        return new InspectionReport(
            backend.Name,
            verdict,
            new BackendAnalysis(double.NaN, double.NaN, ordered, session.OffsetX, session.OffsetY),
            global,
            watch.Elapsed.TotalMilliseconds
        );
        RegionInspectionResult Process(InspectionRegion config)
        {
            if (results.TryGetValue(config.Name, out var saved))
            {
                return saved;
            }

            // 质量判断的两种方法彼此独立、可任选：A为按类型的规则质检，B为局部块异常检测；同时启用时任一NG即NG。
            bool quality = config.Tasks.CheckQuality;
            bool anomaly = config.Tasks.DetectAnomaly;
            bool data = config.Tasks.ReadData;
            var bindings = request.Recipe.Bindings.Where(b => b.Target == config.Name).ToArray();
            bool comparison =
                bindings.Length > 0
                || data
                    && (
                        config.Field.Expected != null
                        || config.Field.Pattern != null
                        || config.Field.AllowedCharacters != null
                        || config.Field.MinimumLength > 0
                        || config.Field.MaximumLength != 128
                    );
            bool read = data;
            var pre = ERoiStageState.NotExecuted;
            var reading = read ? ERoiStageState.NotExecuted : ERoiStageState.NotRequested;
            var compare = comparison ? ERoiStageState.NotExecuted : ERoiStageState.NotRequested;
            var inspect = quality || anomaly ? ERoiStageState.NotExecuted : ERoiStageState.NotRequested;
            var findings = new List<InspectionFinding>();
            var evidence = new RegionInspectionResult(config.Name, Array.Empty<InspectionFinding>());
            RegionAnomalyEvidence? anomalyEvidence = null;
            var region = config;
            string phase = "prerequisites";
            RegionInspectionResult Finish()
            {
                return new RegionInspectionResult(
                    config.Name,
                    findings,
                    evidence.Recognition,
                    evidence.Segmentation,
                    evidence.Glyphs,
                    evidence.Barcodes
                )
                    .WithAnomaly(anomalyEvidence)
                    .WithExecution(new RoiExecution(pre, reading, compare, inspect));
            }

            void Fail(string code, string message)
            {
                findings.Add(new InspectionFinding(code, message, EInspectionVerdict.Ng, region.Bounds));
            }

            if (!active.Add(config.Name))
            {
                Fail("binding_cycle", "ROI引导值依赖循环，不能取得独立先行读数。");
                pre = ERoiStageState.Failed;
                return Finish();
            }

            try
            {
                read = data || quality && session.QualityNeedsReading(config);
                reading = read ? ERoiStageState.NotExecuted : ERoiStageState.NotRequested;
                if (!data && !quality && !anomaly)
                {
                    Fail("no_roi_tasks", "该检测ROI没有选择数据或质量（A规则质检/B异常检测）项目。");
                }

                if ((config.Kind == ERegionKind.Fixed || config.Kind == ERegionKind.Blank) && data)
                {
                    Fail("data_not_applicable", "固定和空白ROI不能配置数据读取。");
                }

                if (
                    !data
                    && (
                        bindings.Length > 0
                        || !config.Field.EqualCells && config.Field.Expected != null
                        || config.Field.Pattern != null
                        || config.Field.AllowedCharacters != null
                        || config.Field.MinimumLength > 0
                        || config.Field.MaximumLength != 128
                    )
                )
                {
                    Fail("data_not_enabled", "配置了引导值，但没有启用数据读取。");
                }

                foreach (var binding in bindings.Where(b => b.Source == EBindingSource.TaskData))
                {
                    if (!TaskValue(binding.Key, out _, out var reason))
                    {
                        Fail("binding_unavailable", reason);
                    }
                }

                findings.AddRange(session.Validate(config, read, quality, token).Select(NgUnlessInfo));
                if (anomaly)
                {
                    if (session is IRoiAnomalySession b)
                    {
                        findings.AddRange(b.ValidateAnomaly(config, token).Select(NgUnlessInfo));
                    }
                    else
                    {
                        Fail("anomaly_unavailable", "选择了局部块异常检测（方法B），但检测后台不支持。");
                    }
                }

                if (findings.Any(f => f.Verdict == EInspectionVerdict.Ng))
                {
                    pre = ERoiStageState.Failed;
                    return results[config.Name] = Finish();
                }

                pre = ERoiStageState.Passed;
                // 先解析并验证图像来源的引导数据，再运行目标ROI的读取或质量算法。
                // 前置失败只阻止当前ROI的后续阶段，其他独立ROI仍继续执行。
                var guides = new List<string>();
                foreach (var binding in bindings)
                {
                    if (binding.Source == EBindingSource.TaskData)
                    {
                        TaskValue(binding.Key, out var value, out _);
                        guides.Add(value!);
                        continue;
                    }

                    var source = configured.SingleOrDefault(r => r.Name == binding.Key);
                    if (source == null)
                    {
                        Fail("binding_unavailable", "来源ROI不存在。");
                        break;
                    }

                    if (!source.Tasks.ReadData)
                    {
                        Fail("binding_unavailable", "来源ROI没有启用数据读取：" + binding.Key);
                        break;
                    }

                    var sourceResult = Process(source);
                    if (
                        !source.Tasks.ReadData
                        || sourceResult.Execution?.Data != ERoiStageState.Passed
                        || !TryValue(sourceResult, out var guide)
                    )
                    {
                        Fail("binding_unavailable", "来源ROI未取得可靠实际数据：" + binding.Key);
                        break;
                    }

                    guides.Add(guide!);
                }

                if (findings.Any(f => f.Verdict == EInspectionVerdict.Ng))
                {
                    pre = ERoiStageState.Failed;
                    return results[config.Name] = Finish();
                }

                region = session.Locate(config, token);
                if (read)
                {
                    phase = "read";
                    evidence = session.Read(region, token);
                    EnsureName(evidence, config.Name);
                    findings.AddRange(evidence.Findings.Select(NgUnlessInfo));
                    bool good = TryValue(evidence, out var value);
                    if (config.Kind == ERegionKind.Text)
                    {
                        good &=
                            evidence.Recognition != null
                            && evidence.Recognition.Confidence >= config.Field.MinimumConfidence;
                    }

                    if (
                        config.Kind == ERegionKind.Barcode
                        && evidence.Barcodes.Count == 1
                        && !Matches(evidence.Barcodes[0].Format, config.Field.BarcodeType)
                    )
                    {
                        good = false;
                        Fail("barcode_type_mismatch", "实际码制与ROI类型不一致。");
                    }

                    if (!good || findings.Any(f => f.Verdict == EInspectionVerdict.Ng))
                    {
                        reading = ERoiStageState.Failed;
                        Fail(
                            config.Kind == ERegionKind.Text ? "ocr_unreliable" : "barcode_not_decoded",
                            "未取得唯一、可信的实际读数；后续质量未执行。"
                        );
                        return results[config.Name] = Finish();
                    }

                    reading = ERoiStageState.Passed;
                    if (data)
                    {
                        phase = "comparison";
                        Rules(value!, config.Field, Fail);
                        foreach (var binding in bindings.Where(b => b.Source == EBindingSource.TaskData))
                        {
                            if (!TaskValue(binding.Key, out _, out var reason))
                            {
                                Fail("binding_unavailable", reason);
                            }
                        }

                        foreach (var guide in guides)
                        {
                            if (!string.Equals(value, guide, StringComparison.Ordinal))
                            {
                                Fail(
                                    "binding_mismatch",
                                    $"实际=[{value}]，引导值=[{guide}]；未执行后续质量检查。"
                                );
                            }
                        }

                        if (findings.Any(f => f.Verdict == EInspectionVerdict.Ng))
                        {
                            if (comparison)
                            {
                                compare = ERoiStageState.Failed;
                            }
                            else
                            {
                                reading = ERoiStageState.Failed;
                            }

                            return results[config.Name] = Finish();
                        }

                        if (comparison)
                        {
                            compare = ERoiStageState.Passed;
                        }

                        foreach (var binding in bindings)
                        {
                            findings.Add(
                                new InspectionFinding(
                                    "binding_match",
                                    "实际读数与引导来源 "
                                        + binding.Source
                                        + ":"
                                        + binding.Key
                                        + " 一致；跨ROI一致性不是独立业务真值。",
                                    EInspectionVerdict.Ok,
                                    region.Bounds
                                )
                            );
                        }

                        findings.Add(
                            new InspectionFinding(
                                comparison ? "content_match" : "data_read",
                                $"实际读数=[{value}]；"
                                    + (
                                        comparison
                                            ? "引导/格式检查通过，质量独立判断。"
                                            : "未配置引导值比较。"
                                    ),
                                EInspectionVerdict.Ok,
                                region.Bounds
                            )
                        );
                    }
                }

                bool completed = true;
                if (quality)
                {
                    phase = "quality";
                    var measured = session.InspectQuality(region, evidence, token);
                    EnsureName(measured.Evidence, config.Name);
                    var returned = measured.Evidence;
                    evidence = new RegionInspectionResult(
                        config.Name,
                        returned.Findings,
                        evidence.Recognition ?? returned.Recognition,
                        returned.Segmentation,
                        returned.Glyphs,
                        evidence.Barcodes.Count > 0 ? evidence.Barcodes : returned.Barcodes
                    );
                    findings.AddRange(evidence.Findings.Select(NgUnlessInfo));
                    completed &= measured.Completed;
                    if (!measured.Completed)
                    {
                        Fail(
                            "quality_incomplete",
                            "选中的印刷质量检查未完整执行；查看阻断原因，不将其伪装成局部缺陷。"
                        );
                    }
                }

                if (anomaly)
                {
                    // 方法B不依赖读取，也不因方法A已发现缺陷而跳过，便于对照两种方法的证据。
                    phase = "quality";
                    var measured = ((IRoiAnomalySession)session).InspectAnomaly(region, token);
                    EnsureName(measured.Evidence, config.Name);
                    anomalyEvidence = measured.Evidence.Anomaly;
                    findings.AddRange(measured.Evidence.Findings.Select(NgUnlessInfo));
                    completed &= measured.Completed;
                    if (!measured.Completed)
                    {
                        Fail("anomaly_incomplete", "选中的局部块异常检测（方法B）未完整执行；查看阻断原因。");
                    }
                }

                if (quality || anomaly)
                {
                    inspect =
                        completed && !findings.Any(f => f.Verdict == EInspectionVerdict.Ng)
                            ? ERoiStageState.Passed
                            : ERoiStageState.Failed;
                    if (inspect == ERoiStageState.Passed)
                    {
                        findings.Add(
                            new InspectionFinding(
                                "quality_pass",
                                "本ROI所选质量检查（"
                                    + (
                                        quality && anomaly ? "A规则质检+B异常检测"
                                        : quality ? "A规则质检"
                                        : "B异常检测"
                                    )
                                    + "）已完整通过。",
                                EInspectionVerdict.Ok,
                                region.Bounds
                            )
                        );
                    }
                }

                return results[config.Name] = Finish();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                Fail("roi_execution_failed", error.GetType().Name + ": " + error.Message);
                if (phase == "prerequisites")
                {
                    pre = ERoiStageState.Failed;
                }
                else if (phase == "read")
                {
                    reading = ERoiStageState.Failed;
                }
                else if (phase == "comparison")
                {
                    compare = ERoiStageState.Failed;
                }
                else
                {
                    inspect = ERoiStageState.Failed;
                }

                return results[config.Name] = Finish();
            }
            finally
            {
                active.Remove(config.Name);
            }
        }

        bool TaskValue(string key, out string? value, out string reason)
        {
            value = null;
            var data = request.TaskData;
            var now = DateTimeOffset.UtcNow;
            if (data == null)
            {
                reason = "未提供本周期任务引导数据。";
                return false;
            }

            if (
                string.IsNullOrWhiteSpace(request.CycleId)
                || !string.Equals(request.CycleId, data.CycleId, StringComparison.Ordinal)
            )
            {
                reason = "图像与引导数据的周期不一致。";
                return false;
            }

            if (now < data.CapturedAt || now > data.ValidUntil)
            {
                reason = "引导数据尚未生效或已经过期。";
                return false;
            }

            if (!data.Values.TryGetValue(key, out value))
            {
                reason = "缺少任务引导字段：" + key;
                return false;
            }

            reason = "";
            return true;
        }
    }

    private static void EnsureName(RegionInspectionResult result, string expected)
    {
        if (result.RegionName != expected)
        {
            throw new InvalidOperationException("Algorithm returned another ROI's evidence.");
        }
    }

    private static InspectionFinding NgUnlessInfo(InspectionFinding f)
    {
        return f.Verdict == EInspectionVerdict.Review
            ? new InspectionFinding(
                f.Code,
                f.Message,
                EInspectionVerdict.Ng,
                f.Bounds,
                f.AreaPixels
            ).WithExecutionBlocker(f.IsExecutionBlocker)
            : f;
    }

    private static bool TryValue(RegionInspectionResult result, out string? value)
    {
        value = result.Recognition?.Text ?? (result.Barcodes.Count == 1 ? result.Barcodes[0].Text : null);
        return !string.IsNullOrEmpty(value);
    }

    private static bool Matches(string format, EBarcodeKind kind)
    {
        return kind == EBarcodeKind.Auto
            || (
                kind == EBarcodeKind.QrCode
                    ? format == "QR_CODE"
                    : new[]
                    {
                        "CODE_128",
                        "CODE_39",
                        "CODE_93",
                        "EAN_13",
                        "EAN_8",
                        "UPC_A",
                        "UPC_E",
                        "ITF",
                        "CODABAR",
                        "MSI",
                        "PLESSEY",
                        "RSS_14",
                        "RSS_EXPANDED",
                    }.Contains(format)
            );
    }

    private static void Rules(string text, FieldSettings field, Action<string, string> fail)
    {
        if (field.Expected != null && !string.Equals(text, field.Expected, StringComparison.Ordinal))
        {
            fail("content_mismatch", $"实际=[{text}]，引导值=[{field.Expected}]；原始读数未修改。");
            if (text.Length == field.Expected.Length)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != field.Expected[i])
                    {
                        fail(
                            "content_character_mismatch",
                            $"文本偏移{i}：实际=[{text[i]}]，引导=[{field.Expected[i]}]。这是原始字符串位置，不是物理字符缺陷框。"
                        );
                    }
                }
            }
        }

        if (text.Length < field.MinimumLength || text.Length > field.MaximumLength)
        {
            fail("length_mismatch", "实际数据长度不符合配置。");
        }

        if (field.AllowedCharacters != null && text.Any(c => !field.AllowedCharacters.Contains(c)))
        {
            fail("charset_mismatch", "实际数据包含不允许字符。");
        }

        if (
            field.Pattern != null
            && !Regex.IsMatch(
                text,
                "\\A(?:" + field.Pattern + ")\\z",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            )
        )
        {
            fail("pattern_mismatch", "实际数据不符合整段格式。");
        }
    }
}

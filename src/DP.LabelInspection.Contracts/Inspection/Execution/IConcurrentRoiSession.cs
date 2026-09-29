namespace DP.LabelInspection.Contracts;

/// <summary>
/// 声明会话的各阶段方法可被多个ROI并发调用（线程安全）。引擎仅在设置了大于1的ROI并行度、且会话实现本接口时，
/// 才并行执行不从其他ROI取引导值的ROI；实现方须保证其注入的算法同样线程安全。
/// </summary>
public interface IConcurrentRoiSession : IRoiInspectionSession { }

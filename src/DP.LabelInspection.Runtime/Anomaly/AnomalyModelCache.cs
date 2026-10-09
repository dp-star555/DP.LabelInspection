using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.LabelInspection.Contracts;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;

namespace DP.LabelInspection.Runtime;

/// <summary>有界模型运行实例缓存；厂商解析、原生所有权与在途租约集中在这里，业务不再解析DPPA。</summary>
internal sealed class AnomalyModelCache : IDisposable
{
    private readonly object _gate = new object();
    private readonly PinnedRevisionCache<AnomalyLibrarySnapshot> _libraries = new PinnedRevisionCache<AnomalyLibrarySnapshot>(8);
    private readonly Dictionary<string, Slot> _models = new Dictionary<string, Slot>(StringComparer.Ordinal);
    private bool _disposed;
    private const int Capacity = 128;
    private const long ByteBudget = 512L * 1024 * 1024;
    internal AnomalyLibrarySnapshot Library(IAnomalyLibraryRepository repository, string id, int revision)
        => _libraries.Get(id, revision, () => repository.LoadAnomalyLibrary(id, revision));
    internal Lease Acquire(AnomalyModelEntry entry, AnomalyImplementationRegistry implementations, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string key = entry.FeatureSource + ":" + entry.Sha256;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AnomalyModelCache));
            if (!_models.TryGetValue(key, out var slot))
            {
                while (_models.Count >= Capacity || _models.Values.Sum(s => (long)s.Bytes) + entry.Length > ByteBudget)
                {
                    var idle = _models.Where(p => p.Value.Users == 0).OrderBy(p => p.Value.LastUsed).FirstOrDefault();
                    if (idle.Value == null) throw new InvalidOperationException("异常模型缓存容量已满且实例仍在使用，请减少同时绑定的模型或分批运行。");
                    _models.Remove(idle.Key); idle.Value.Runtime.Dispose();
                }
                var bytes = entry.CopyModel();
                // 仅旧Patch格式通过兼容Adapter进入；新原生包由相应实现解释。
                var asset = AnomalyModelAsset.IsAsset(bytes) ? AnomalyModelAsset.FromBytes(bytes)
                    : PatchAnomalyImplementation.Capture(PatchAnomalyModel.FromBytes(bytes), entry.Width, entry.Height);
                if (asset.ImplementationId != entry.FeatureSource) throw new System.IO.InvalidDataException("模型资产与库条目实现身份不匹配。");
                var runtime = implementations.Resolve(asset.ImplementationId).Load(asset, token);
                if (runtime.Asset.ContentSha256 != asset.ContentSha256)
                { runtime.Dispose(); throw new System.IO.InvalidDataException("异常实现替换了请求的模型身份。"); }
                slot = new Slot(runtime, entry.Length); _models.Add(key, slot);
            }
            slot.Users++; return new Lease(this, slot);
        }
    }
    private void Return(Slot slot)
    {
        lock (_gate) { slot.Users--; slot.LastUsed = DateTime.UtcNow; if (_disposed && slot.Users == 0) slot.Runtime.Dispose(); }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var slot in _models.Values.Where(s => s.Users == 0)) slot.Runtime.Dispose();
            _models.Clear();
        }
    }
    internal sealed class Slot
    {
        internal Slot(ILoadedAnomalyModel runtime, int bytes) { Runtime = runtime; Bytes = bytes; LastUsed = DateTime.UtcNow; }
        internal readonly ILoadedAnomalyModel Runtime;
        internal readonly int Bytes;
        internal int Users;
        internal DateTime LastUsed;
    }
    internal sealed class Lease : IDisposable
    {
        private AnomalyModelCache? _owner; private readonly Slot _slot;
        internal Lease(AnomalyModelCache owner, Slot slot) { _owner = owner; _slot = slot; }
        internal ILoadedAnomalyModel Runtime => _slot.Runtime;
        public void Dispose() { var owner = System.Threading.Interlocked.Exchange(ref _owner, null); owner?.Return(_slot); }
    }
}

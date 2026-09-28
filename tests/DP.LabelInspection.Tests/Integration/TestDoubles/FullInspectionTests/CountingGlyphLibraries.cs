using DP.LabelInspection.Contracts;

namespace DP.LabelInspection.Tests;

public sealed partial class FullInspectionTests
{
    /// <summary>记录固定版本字库的载入次数，其余委托给真实存储。</summary>
    private sealed class CountingGlyphLibraries : IGlyphLibraryRepository
    {
        private readonly IGlyphLibraryRepository _inner;

        public CountingGlyphLibraries(IGlyphLibraryRepository inner)
        {
            _inner = inner;
        }

        public int Loads { get; private set; }

        public GlyphLibrarySnapshot Load(string id, int revision)
        {
            Loads++;
            return _inner.Load(id, revision);
        }
    }
}

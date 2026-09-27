using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DP.LabelInspection.AnomalyBenchmark;

/// <summary>写UTF-8（带BOM，便于Excel打开）CSV。</summary>
internal sealed class Csv : System.IDisposable
{
    private readonly StreamWriter _writer;

    internal Csv(string path, params string[] header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, false, new UTF8Encoding(true));
        Row(header);
    }

    internal void Row(IEnumerable<object> fields)
    {
        _writer.WriteLine(
            string.Join(
                ",",
                fields.Select(f =>
                {
                    string s = f is double d
                        ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        : System.Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                        ? "\"" + s.Replace("\"", "\"\"") + "\""
                        : s;
                })
            )
        );
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

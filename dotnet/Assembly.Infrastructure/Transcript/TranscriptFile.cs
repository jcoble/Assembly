using System.Text;
using Assembly.Application.Common.Acp;

namespace Assembly.Infrastructure.Transcript;

/// <summary>A <see cref="TranscriptLog"/> written to a file, which it creates (and its folder) or replaces.</summary>
public static class TranscriptFile
{
    public static TranscriptLog Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        return new TranscriptLog(stream);
    }
}

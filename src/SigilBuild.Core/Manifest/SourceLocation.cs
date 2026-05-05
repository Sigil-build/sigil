namespace SigilBuild.Core.Manifest;

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public static readonly SourceLocation Unknown = new(string.Empty, 0, 0);

    public override string ToString() =>
        File.Length == 0 ? "<unknown>" : $"{File}:{Line}:{Column}";
}

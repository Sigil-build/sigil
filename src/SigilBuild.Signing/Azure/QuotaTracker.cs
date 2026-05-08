using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SigilBuild.Signing.Azure;

public sealed class QuotaTracker
{
    private readonly string _path;

    public QuotaTracker(string path) { _path = path; }

    public void RecordSign(DateTimeOffset when)
    {
        var data = LoadAll();
        data.Add(when);
        Save(data);
    }

    public int CountForMonth(int year, int month) =>
        LoadAll().Count(d => d.Year == year && d.Month == month);

    private List<DateTimeOffset> LoadAll()
    {
        if (!File.Exists(_path)) return new();
        return JsonSerializer.Deserialize(File.ReadAllText(_path), QuotaJsonContext.Default.ListDateTimeOffset)
            ?? new List<DateTimeOffset>();
    }

    private void Save(List<DateTimeOffset> data) =>
        File.WriteAllText(_path,
            JsonSerializer.Serialize(data, QuotaJsonContext.Default.ListDateTimeOffset));
}

[JsonSerializable(typeof(List<DateTimeOffset>))]
internal sealed partial class QuotaJsonContext : JsonSerializerContext { }

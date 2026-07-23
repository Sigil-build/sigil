using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// T11.1 (P11): <see cref="RollbackRecord.DeleteScheduledTask"/> — the inverse
/// journaled by <c>scheduled_task_create</c> BEFORE the create (mirrors
/// <see cref="RollbackRecord.RemoveService"/>) — survives the flat
/// <see cref="SerializableRollbackRecord"/> converter and the full
/// source-generated (AOT-safe) <see cref="WrapperBlobJsonContext"/> in both
/// directions. The journal records the task NAME only — no secrets, no
/// resolved program path.
/// </summary>
public class RollbackRecordRoundtripTests
{
    [Fact]
    public void DeleteScheduledTask_roundtrips_through_the_converter()
    {
        var record = new RollbackRecord.DeleteScheduledTask("AcmeUpdaterTask");

        var wire = record.ToSerializable();
        wire.Type.Should().Be("delete_scheduled_task");

        var back = wire.ToRollbackRecord();
        back.Should().BeOfType<RollbackRecord.DeleteScheduledTask>()
            .Which.TaskName.Should().Be("AcmeUpdaterTask");
    }

    [Fact]
    public void DeleteScheduledTask_survives_the_source_generated_json_context()
    {
        var wire = new RollbackRecord.DeleteScheduledTask("AcmeUpdaterTask").ToSerializable();

        var json = JsonSerializer.Serialize(wire, WrapperBlobJsonContext.Default.SerializableRollbackRecord);
        var back = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)),
            WrapperBlobJsonContext.Default.SerializableRollbackRecord);

        back.Should().NotBeNull();
        var record = back!.ToRollbackRecord();
        record.Should().BeOfType<RollbackRecord.DeleteScheduledTask>()
            .Which.TaskName.Should().Be("AcmeUpdaterTask");
    }

    [Fact]
    public void DeleteScheduledTask_survives_the_full_journal_roundtrip()
    {
        var journal = new SerializableRollbackJournal
        {
            Records = new[] { new RollbackRecord.DeleteScheduledTask("AcmeUpdaterTask").ToSerializable() },
        };

        var json = JsonSerializer.Serialize(journal, WrapperBlobJsonContext.Default.SerializableRollbackJournal);
        var back = JsonSerializer.Deserialize(json, WrapperBlobJsonContext.Default.SerializableRollbackJournal);

        back.Should().NotBeNull();
        back!.Records.Should().ContainSingle()
            .Which.Type.Should().Be("delete_scheduled_task");
    }
}

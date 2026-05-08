using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Win32 round-trip tests for the SIGIL_BLOB_V1 / SIGIL_PAYLOAD_V1 embed
/// path. The "embed" half (<see cref="WrapperResourceWriter.WriteAsync"/>)
/// requires a real PE file to update — there is no in-memory equivalent of
/// <c>BeginUpdateResource</c>. Tests therefore depend on the AOT-published
/// wrapper runtime being copied into <c>runtimes/win-x64/</c>; that wiring
/// is the responsibility of the build pipeline (see
/// <see cref="WrapperRuntimeLocator"/>) and is tracked as a follow-up.
/// </summary>
public class WrapperResourceWriterTests
{
    [Fact(Skip = "Requires the AOT-published Wrapper.exe in runtimes/win-x64/. Tracked as a build-pipeline follow-up to Task 14.")]
    public async Task Roundtrip_blob_via_resource_apis()
    {
        var stubExe = WrapperRuntimeLocator.Locate();
        var tmp = Path.Combine(Path.GetTempPath(), $"sigil-rw-{Guid.NewGuid():N}.exe");
        File.Copy(stubExe, tmp, overwrite: true);
        try
        {
            var blob = Encoding.UTF8.GetBytes("hello-blob");
            var payload = new byte[] { 1, 2, 3 };
            await WrapperResourceWriter.WriteAsync(tmp, blob, payload, CancellationToken.None);

            // Read the resource back via raw Win32 APIs without launching the
            // wrapper exe (which would also exercise Half B).
            var roundtrippedBlob = ResourceReader.Read(tmp, "SIGIL_BLOB_V1");
            roundtrippedBlob.Should().Equal(blob);

            var roundtrippedPayload = ResourceReader.Read(tmp, "SIGIL_PAYLOAD_V1");
            roundtrippedPayload.Should().Equal(payload);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [Fact]
    public void Roundtrip_empty_blob_via_json_context()
    {
        var bytes = SerializeBlob(WrapperBlob.Empty);
        var deserialized = DeserializeBlob(bytes);

        deserialized.AppId.Should().Be("<unset>");
        deserialized.Parameters.Should().BeEmpty();
        deserialized.InstallSteps.Should().BeEmpty();
        deserialized.PreInstall.Should().BeEmpty();
        deserialized.PostInstall.Should().BeEmpty();
        deserialized.UpdateSteps.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_parameters_only_blob_via_json_context()
    {
        var blob = new WrapperBlob(
            AppId: "com.example.app",
            Parameters: new[]
            {
                new ParameterDefinition(
                    Name: "InstallDir",
                    Type: ParameterType.Path,
                    Default: @"C:\Program Files\Example",
                    EnumValues: null,
                    InstallTime: true,
                    Description: "Where to install",
                    Pattern: null,
                    Min: null,
                    Max: null),
                new ParameterDefinition(
                    Name: "Verbose",
                    Type: ParameterType.Bool,
                    Default: true,
                    EnumValues: null,
                    InstallTime: false,
                    Description: null,
                    Pattern: null,
                    Min: null,
                    Max: null),
            },
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var deserialized = DeserializeBlob(SerializeBlob(blob));

        deserialized.AppId.Should().Be("com.example.app");
        deserialized.Parameters.Should().HaveCount(2);
        deserialized.Parameters[0].Name.Should().Be("InstallDir");
        deserialized.Parameters[0].Type.Should().Be(ParameterType.Path);
        deserialized.Parameters[0].Default.Should().Be(@"C:\Program Files\Example");
        deserialized.Parameters[0].InstallTime.Should().BeTrue();
        deserialized.Parameters[1].Name.Should().Be("Verbose");
        deserialized.Parameters[1].Type.Should().Be(ParameterType.Bool);
        deserialized.Parameters[1].Default.Should().Be(true);
    }

    [Fact]
    public void Roundtrip_one_step_blob_via_json_context()
    {
        var blob = new WrapperBlob(
            AppId: "com.example.app",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                new InstallStep.FileCopy(
                    Id: "copy-readme",
                    From: "payload://README.txt",
                    To: "${parameters.InstallDir}/README.txt",
                    Overwrite: true,
                    When: null,
                    OnFailure: OnFailure.Fail),
                new InstallStep.DirectoryCreate(
                    Id: "make-data",
                    Path: "${parameters.InstallDir}/data",
                    When: "system.arch == \"x64\"",
                    OnFailure: OnFailure.Continue),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var deserialized = DeserializeBlob(SerializeBlob(blob));

        deserialized.InstallSteps.Should().HaveCount(2);

        deserialized.InstallSteps[0].Should().BeOfType<InstallStep.FileCopy>();
        var fc = (InstallStep.FileCopy)deserialized.InstallSteps[0];
        fc.Id.Should().Be("copy-readme");
        fc.From.Should().Be("payload://README.txt");
        fc.To.Should().Be("${parameters.InstallDir}/README.txt");
        fc.Overwrite.Should().BeTrue();
        fc.OnFailure.Should().Be(OnFailure.Fail);

        deserialized.InstallSteps[1].Should().BeOfType<InstallStep.DirectoryCreate>();
        var dc = (InstallStep.DirectoryCreate)deserialized.InstallSteps[1];
        dc.Id.Should().Be("make-data");
        dc.Path.Should().Be("${parameters.InstallDir}/data");
        dc.When.Should().Be("system.arch == \"x64\"");
        dc.OnFailure.Should().Be(OnFailure.Continue);
    }

    private static byte[] SerializeBlob(WrapperBlob blob)
    {
        var ser = SerializableWrapperBlob.FromWrapperBlob(blob);
        var json = System.Text.Json.JsonSerializer.Serialize(
            ser, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return Encoding.UTF8.GetBytes(json);
    }

    private static WrapperBlob DeserializeBlob(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        var ser = System.Text.Json.JsonSerializer.Deserialize(
            json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        ser.Should().NotBeNull();
        return SerializableWrapperBlob.ToWrapperBlob(ser!);
    }
}

/// <summary>
/// Reads a single <c>RT_RCDATA</c> resource by string name from a PE file
/// using <c>LoadLibraryEx(LOAD_LIBRARY_AS_DATAFILE)</c> and the
/// <c>FindResource</c> / <c>LoadResource</c> / <c>LockResource</c> /
/// <c>SizeofResource</c> family. Used by the round-trip test as an
/// independent reader so we exercise the writer without launching the
/// wrapper exe.
/// </summary>
internal static partial class ResourceReader
{
    private static readonly IntPtr RtRcData = (IntPtr)10;
    private const uint LoadLibraryAsDataFile = 0x00000002;

    public static byte[] Read(string filePath, string resourceName)
    {
        var hModule = LoadLibraryExW(filePath, IntPtr.Zero, LoadLibraryAsDataFile);
        if (hModule == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"LoadLibraryEx failed for '{filePath}'");
        }

        var namePtr = Marshal.StringToHGlobalUni(resourceName);
        try
        {
            var hRes = FindResourceW(hModule, namePtr, RtRcData);
            if (hRes == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"FindResource failed for '{resourceName}'");
            }

            var size = SizeofResource(hModule, hRes);
            var hData = LoadResource(hModule, hRes);
            if (hData == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadResource failed");
            }

            var ptr = LockResource(hData);
            var managed = new byte[size];
            Marshal.Copy(ptr, managed, 0, (int)size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            FreeLibrary(hModule);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
    private static partial IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadResource", SetLastError = true)]
    private static partial IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "LockResource", SetLastError = true)]
    private static partial IntPtr LockResource(IntPtr hResData);

    [LibraryImport("kernel32.dll", EntryPoint = "SizeofResource", SetLastError = true)]
    private static partial uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hModule);
}

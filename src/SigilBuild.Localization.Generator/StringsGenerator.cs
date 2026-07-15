using Microsoft.CodeAnalysis;

namespace SigilBuild.Localization.Generator;

/// <summary>
/// Incremental source generator that will turn <c>Strings.&lt;lang&gt;.txt</c> catalog
/// files into a compiled-in string table (P9). This is a skeleton registered with the
/// compiler from Task 1 onward; the pipeline that reads AdditionalFiles, calls
/// <see cref="CatalogParser"/>, and emits source is wired up in a later P9 task.
/// </summary>
[Generator]
public sealed class StringsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Emitter + diagnostics land in a later P9 task. Intentionally empty for now.
    }
}

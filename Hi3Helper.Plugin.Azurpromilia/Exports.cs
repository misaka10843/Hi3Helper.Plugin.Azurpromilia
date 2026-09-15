using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia;

/// <summary>
/// Native export surface. The launcher resolves every plugin entry point through
/// <c>TryGetApiExport</c>, which forwards to the shared export table populated by
/// <see cref="SharedStaticV1Ext{T}"/>.
/// </summary>
public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports()
    {
        // The intercept version tells the launcher which API standard this plugin was compiled
        // against when the runtime cannot inspect the assembly (Native AOT).
        Load<AzurPromiliaPlugin>(!RuntimeFeature.IsDynamicCodeCompiled ? new GameVersion(1, 0, 0, 0) : default);
    }

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* exportName, void** delegateP)
        => TryGetApiExportPointer(exportName, delegateP);
}

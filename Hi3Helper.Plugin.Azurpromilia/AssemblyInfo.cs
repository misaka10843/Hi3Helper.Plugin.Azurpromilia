using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Azur Promilia is a Windows-only title: the launcher protocol needs the Windows registry (device id,
// uninstall entries, channel detection), Windows path semantics and the official launcher's
// per-machine credential store. Declaring that at the assembly level is both accurate and what lets
// the CA1416 platform-compatibility analyser see that the Windows-only helpers in Utils/ are only
// ever called from equally Windows-only code.
[assembly: SupportedOSPlatform("windows")]

// The signing rules are the single most fragile part of the wire protocol (see the POST/GET asymmetry
// in AzurPromiliaApiClient.BuildSignedHeaders), so the smoke-test harness is allowed to assert on them
// directly rather than through a mocked HTTP layer.
[assembly: InternalsVisibleTo("AccountResolutionTests")]

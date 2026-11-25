using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal partial class SelfUpdate : PluginSelfUpdateBase
{
    private static readonly string[] CdnUrls =
    [
        "https://cl-plugins.sakurakoi.top/AzurPromilia/",
        "https://github.com/misaka10843/CollapsePlugin-ReleaseRepo/raw/main/AzurPromilia/"
    ];

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => CdnUrls;
    protected override HttpClient UpdateHttpClient { get; } = new PluginHttpClientBuilder()
        .AllowRedirections()
        .Create();
}

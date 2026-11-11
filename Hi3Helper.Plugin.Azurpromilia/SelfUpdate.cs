using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
// ReSharper disable once InconsistentNaming
internal partial class SelfUpdate : PluginSelfUpdateBase
{
    private const string CdnFileSuffix = "AzurPromilia/";
    private const string PrimaryCdnUrl = "https://github.com/Hi3Helper-PLUGINS/Hi3Helper.Plugin.Azurpromilia/releases/latest/download/" +
                                         CdnFileSuffix;
    private const string FallbackCdnUrl = "https://github.com/Hi3Helper-PLUGINS/Hi3Helper.Plugin.Azurpromilia/raw/main/dist/" +
                                          CdnFileSuffix;

    protected readonly string[] BaseCdnUrl = [PrimaryCdnUrl, FallbackCdnUrl];

    internal SelfUpdate()
    {
        UpdateHttpClient = new PluginHttpClientBuilder()
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .Create();
    }

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => BaseCdnUrl;

    protected override HttpClient UpdateHttpClient { get; }
}

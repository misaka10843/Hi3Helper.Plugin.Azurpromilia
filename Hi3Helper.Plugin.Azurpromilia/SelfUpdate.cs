using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

/// <summary>
/// Self-update source for the plugin.
/// <para>
/// The launcher looks for a <c>manifest.json</c> plus a versioned archive under one of the CDN roots
/// below. <b>These URLs are placeholders:</b> point them at wherever you publish plugin builds before
/// shipping, or remove the <c>GetPluginSelfUpdater</c> override in <see cref="AzurPromiliaPlugin"/> to
/// disable self-update entirely.
/// </para>
/// </summary>
[GeneratedComClass]
// ReSharper disable once InconsistentNaming
internal partial class SelfUpdate : PluginSelfUpdateBase
{
    private const string CdnFileSuffix = "AzurPromilia/";

    /// <summary>Primary CDN root. Replace with your own hosting.</summary>
    private const string PrimaryCdnUrl = "https://github.com/Hi3Helper-PLUGINS/Hi3Helper.Plugin.Azurpromilia/releases/latest/download/" +
                                         CdnFileSuffix;

    /// <summary>Fallback CDN root.</summary>
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

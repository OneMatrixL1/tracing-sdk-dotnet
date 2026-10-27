using System.Text;

namespace Tracing.Sdk.Events;

/// <summary>
/// Where verification links point: the integrity portal, which looks a
/// record up by the fields in the link and checks it against the chain in
/// the browser. The opaque ticketId travels after '#'.
/// </summary>
public sealed class PortalConfig
{
    public const string DefaultBaseUrl = "https://integrity.vmatrix.com/v1/";

    /// <summary>The portal page; links append their query to it.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>Names of the parameters every link carries.</summary>
    public PortalParamNames ParamNames { get; init; } = new();

    /// <exception cref="ConfigException">the base URL is not an absolute http(s) URL without a fragment, or a parameter name is empty</exception>
    internal void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ConfigException($"portal baseUrl must be an absolute http(s) URL, got \"{BaseUrl}\"");
        }

        if (BaseUrl.Contains('#'))
        {
            throw new ConfigException("portal baseUrl must not have a fragment: the link puts the ticketId there");
        }

        if (ParamNames is null
            || string.IsNullOrEmpty(ParamNames.EventType) || string.IsNullOrEmpty(ParamNames.TicketId))
        {
            throw new ConfigException("portal paramNames must all be non-empty");
        }
    }
}

/// <summary>Names of the parameters every portal link carries.</summary>
/// <param name="EventType">the event type (query)</param>
/// <param name="TicketId">the opaque record id (fragment, never sent to a server)</param>
public sealed record PortalParamNames(string EventType = "type", string TicketId = "ticketId");

/// <summary>Formats one portal link.</summary>
internal static class PortalLink
{
    /// <exception cref="ConfigException">two parameters share a name</exception>
    public static string Build(PortalConfig portal, string eventType, IEnumerable<KeyValuePair<string, string>> fields, string? ticketId)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new(portal.ParamNames.EventType, eventType),
        };
        parameters.AddRange(fields);

        var duplicate = parameters.GroupBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new ConfigException($"Portal link parameter \"{duplicate.Key}\" appears twice; rename it in the portal paramNames or the event type's link");
        }

        var link = new StringBuilder(portal.BaseUrl);
        link.Append(portal.BaseUrl.Contains('?') ? (portal.BaseUrl.EndsWith('?') || portal.BaseUrl.EndsWith('&') ? "" : "&") : "?");
        link.AppendJoin('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        if (ticketId is not null)
        {
            // After '#': the browser never sends it, so it stays out of server logs.
            link.Append('#').Append(Uri.EscapeDataString(portal.ParamNames.TicketId)).Append('=').Append(Base64Url(TracingSdk.EncodeRawData(ticketId)));
        }

        return link.ToString();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

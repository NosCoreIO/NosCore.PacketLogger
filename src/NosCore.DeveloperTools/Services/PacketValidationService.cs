using System.Reflection;
using NosCore.DeveloperTools.Models;
using NosCore.Packets;
using NosCore.Packets.Attributes;

namespace NosCore.DeveloperTools.Services;

/// <summary>
/// Runs every captured packet through NosCore.Packets's deserializer
/// against the direction it was tagged with, and reports anomalies:
///   <list type="bullet">
///     <item><c>Missing</c> — header isn't defined anywhere in NosCore.Packets.</item>
///     <item><c>WrongStructure</c> — header is defined for this direction but the wire format doesn't match the schema (deserializer throws).</item>
///     <item><c>WrongTag</c> — header is defined but only in the opposite-direction namespace (a client header appeared tagged as [Server], or vice versa).</item>
///   </list>
/// Two separate <see cref="Deserializer"/> instances are built so the
/// existing "server loses to client on duplicate header" logic inside
/// <see cref="Deserializer.Initialize{T}"/> doesn't mask wrong-tag cases.
/// </summary>
public sealed class PacketValidationService
{
    private readonly Deserializer _clientDeserializer;
    private readonly Deserializer _serverDeserializer;
    private readonly HashSet<string> _clientHeaders;
    private readonly HashSet<string> _serverHeaders;

    public PacketValidationService()
    {
        var all = typeof(PacketBase).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true })
            .Where(t => typeof(PacketBase).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<PacketHeaderAttribute>() != null)
            .ToList();

        var clientTypes = all.Where(t => t.Namespace?.Contains("ClientPackets") == true).ToList();
        var serverTypes = all.Where(t => t.Namespace?.Contains("ServerPackets") == true).ToList();

        _clientDeserializer = new Deserializer(clientTypes);
        _serverDeserializer = new Deserializer(serverTypes);

        _clientHeaders = new HashSet<string>(clientTypes.SelectMany(HeadersOf), StringComparer.Ordinal);
        _serverHeaders = new HashSet<string>(serverTypes.SelectMany(HeadersOf), StringComparer.Ordinal);
    }

    public PacketValidationIssue? Validate(LoggedPacket packet)
    {
        var header = ExtractHeader(packet.Raw);
        if (string.IsNullOrEmpty(header)) return null;

        var fromClient = packet.Direction == PacketDirection.Send;
        var expected = fromClient ? _clientHeaders : _serverHeaders;
        var opposite = fromClient ? _serverHeaders : _clientHeaders;
        var deserializer = fromClient ? _clientDeserializer : _serverDeserializer;

        if (expected.Contains(header))
        {
            try
            {
                _ = deserializer.Deserialize(packet.Raw);
                return null;
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException?.Message ?? ex.Message;
                return new PacketValidationIssue(DateTime.Now, ValidationCategory.WrongStructure, packet, detail);
            }
        }

        if (opposite.Contains(header))
        {
            var expectedTag = fromClient ? "[Client]" : "[Server]";
            var actualSide = fromClient ? "server" : "client";
            return new PacketValidationIssue(DateTime.Now, ValidationCategory.WrongTag, packet,
                $"Header '{header}' is a known {actualSide} packet but was captured as {expectedTag}.");
        }

        return new PacketValidationIssue(DateTime.Now, ValidationCategory.Missing, packet,
            $"Header '{header}' is not defined in NosCore.Packets.");
    }

    private static IEnumerable<string> HeadersOf(Type t)
    {
        var primary = t.GetCustomAttribute<PacketHeaderAttribute>()?.Identification;
        if (primary != null) yield return primary;
        foreach (var alias in t.GetCustomAttributes<PacketHeaderAliasAttribute>())
        {
            yield return alias.Identification;
        }
    }

    // Mirror of Deserializer's own header-extraction logic: the leading token
    // is the keepalive id when it parses as a ushort (client→server packets),
    // otherwise it's the header directly (server→client packets).
    private static string ExtractHeader(string raw)
    {
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;
        if (ushort.TryParse(tokens[0], out _) && tokens.Length >= 2) return tokens[1];
        return tokens[0];
    }
}

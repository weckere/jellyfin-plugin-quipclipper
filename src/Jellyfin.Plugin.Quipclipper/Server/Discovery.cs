using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Quipclipper.Server;

/// <summary>
/// Finds quipclipper servers that advertise themselves on the LAN (<c>_quipclipper._tcp</c>
/// over mDNS/Bonjour, which quipclipper does only when told to: <c>QC_ADVERTISE=1</c>), so
/// the settings page can offer their URLs instead of having them typed. Only when an
/// administrator asks (the page's "Find on this network"); typing the URL stays the way in.
/// One-shot queries from an ephemeral port (RFC 6762 §5.1): responders answer by unicast,
/// so nothing binds port 5353 and nothing is announced. IPv4 only. Multicast doesn't cross
/// a container's bridge network, so a Jellyfin in Docker finds nothing unless it runs with
/// the host's network. Anyone on the LAN can answer, so what is read is bounded (packet,
/// name, record and server counts) and only answers from the mDNS port count; a server
/// listed is a suggestion an administrator picks, then pairs with by comparing a code.
/// </summary>
public sealed class Discovery
{
    /// <summary>The service type quipclipper advertises.</summary>
    public const string ServiceType = "_quipclipper._tcp.local";

    /// <summary>How long the page's search listens for answers.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    /// <summary>The mDNS group and port.</summary>
    public static readonly IPEndPoint MulticastGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const int MaxServers = 50;
    private const int MaxPackets = 200;
    private const int MaxPacketBytes = 9000;   // RFC 6762 §17
    private const int MaxRecordsPerPacket = 64;
    private const int MaxNames = 256;          // owner names kept across every answer
    private const int MaxNameBytes = 255;      // RFC 1035 §2.3.4
    private const int MaxAsked = 32;           // names asked about in follow-up queries
    private const int MaxQuestions = 16;

    private readonly IPEndPoint _target;
    private readonly Func<IReadOnlyList<IPAddress>> _interfaces;

    /// <summary>Initializes a new instance of the <see cref="Discovery"/> class: the LAN, on every interface that can multicast.</summary>
    public Discovery()
        : this(MulticastGroup, LanInterfaces)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Discovery"/> class (the tests ask a fake advertiser on loopback).</summary>
    /// <param name="target">Where the queries go.</param>
    /// <param name="interfaces">The local addresses to send a multicast query from, one each; none: the system's choice.</param>
    internal Discovery(IPEndPoint target, Func<IReadOnlyList<IPAddress>> interfaces)
    {
        _target = target;
        _interfaces = interfaces;
    }

    /// <summary>Asks the LAN for quipclipper servers and listens for answers.</summary>
    /// <param name="window">How long to listen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="enough">Stops listening early once it says what was found is enough (the tests).</param>
    /// <returns>The servers that answered, by name.</returns>
    /// <exception cref="SocketException">No socket could be opened.</exception>
    public async Task<IReadOnlyList<FoundServer>> FindAsync(TimeSpan window, CancellationToken cancellationToken, Func<IReadOnlyList<FoundServer>, bool>? enough = null)
    {
        var records = new Records();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listen.CancelAfter(window);
        await SendAsync(udp, Query((ServiceType, TypePtr)), listen.Token).ConfigureAwait(false);
        for (var packets = 0; packets < MaxPackets; packets++)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(listen.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // the window is over
            }
            catch (SocketException)
            {
                continue; // an ICMP "port unreachable" from a host that doesn't listen
            }

            // Responders answer a one-shot query from the mDNS port (RFC 6762 §6.7).
            if (received.RemoteEndPoint.Port != _target.Port || !records.Add(received.Buffer))
            {
                continue;
            }

            // A responder that left out an instance's SRV/TXT (or its host's address): ask for them.
            var missing = records.Missing().Where(n => asked.Count < MaxAsked && asked.Add(n)).Take(MaxQuestions / 2).ToList();
            if (missing.Count > 0)
            {
                await SendAsync(udp, Query(missing.SelectMany(Follow).ToArray()), listen.Token).ConfigureAwait(false);
            }

            if (enough is not null && enough(records.Servers()))
            {
                break;
            }
        }

        return records.Servers();

        static IEnumerable<(string, ushort)> Follow(string name) =>
            name.EndsWith(ServiceType, StringComparison.OrdinalIgnoreCase) ? [(name, TypeSrv), (name, TypeTxt)] : [(name, TypeA)];
    }

    /// <summary>A DNS query (id 0, no recursion) for each name and type.</summary>
    /// <param name="questions">The names and record types.</param>
    /// <returns>The packet.</returns>
    internal static byte[] Query(params (string Name, ushort Type)[] questions)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(questions.Length, MaxQuestions);
        var packet = new List<byte>(512) { 0, 0, 0, 0, (byte)(questions.Length >> 8), (byte)questions.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                packet.Add((byte)Math.Min(bytes.Length, 63));
                packet.AddRange(bytes.Take(63));
            }

            packet.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 }); // end of name, type, class IN
        }

        return packet.ToArray();
    }

    /// <summary>The servers in a set of mDNS answers (the tests read packets with it).</summary>
    /// <param name="packets">The answer packets.</param>
    /// <returns>The servers.</returns>
    internal static IReadOnlyList<FoundServer> Read(IEnumerable<byte[]> packets)
    {
        var records = new Records();
        foreach (var packet in packets)
        {
            records.Add(packet);
        }

        return records.Servers();
    }

    private static IReadOnlyList<IPAddress> LanInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                            && !(a.GetAddressBytes() is [169, 254, ..]))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private async Task SendAsync(UdpClient udp, byte[] packet, CancellationToken cancellationToken)
    {
        var from = _target.Address.GetAddressBytes() is [>= 224 and <= 239, ..] ? _interfaces() : [];
        if (from.Count == 0)
        {
            await udp.SendAsync(packet, _target, cancellationToken).ConfigureAwait(false);
            return;
        }

        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        foreach (var address in from)
        {
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                await udp.SendAsync(packet, _target, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // an interface that can't send: the others still can
            }
        }
    }

    /// <summary>The records collected from every answer so far.</summary>
    private sealed class Records
    {
        private readonly Dictionary<string, HashSet<string>> _ptr = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Host, int Port)> _srv = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _txt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<IPAddress>> _a = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads one answer packet; false when it isn't one (or can't be read).</summary>
        public bool Add(byte[] packet)
        {
            if (packet.Length > MaxPacketBytes)
            {
                return false;
            }

            try
            {
                return Parse(packet);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException or DecoderFallbackException)
            {
                return false; // a malformed packet: whatever else was read stays
            }
        }

        /// <summary>Names an answer pointed at without saying more: instances with no SRV or TXT, hosts with no address.</summary>
        public IEnumerable<string> Missing()
        {
            foreach (var instance in Instances())
            {
                if (!_srv.TryGetValue(instance, out var srv) || !_txt.ContainsKey(instance))
                {
                    yield return instance;
                }
                else if (!_a.ContainsKey(srv.Host))
                {
                    yield return srv.Host;
                }
            }
        }

        public List<FoundServer> Servers()
        {
            var found = new List<FoundServer>();
            foreach (var instance in Instances())
            {
                if (found.Count == MaxServers)
                {
                    break;
                }

                var txt = _txt.GetValueOrDefault(instance) ?? [];
                var urls = new List<string>();
                var advertised = txt.TryGetValue("url", out var u) && Uri.TryCreate(u, UriKind.Absolute, out var parsed)
                                 && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                    ? parsed
                    : null;
                // A .local name needs mDNS to resolve, which a server (above all in a
                // container) usually hasn't got: the addresses come first then.
                if (advertised is not null && !advertised.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(advertised.AbsoluteUri.TrimEnd('/'));
                }

                if (_srv.TryGetValue(instance, out var srv) && _a.TryGetValue(srv.Host, out var addresses))
                {
                    var scheme = advertised?.Scheme ?? Uri.UriSchemeHttp;
                    urls.AddRange(addresses.OrderBy(a => a.ToString(), StringComparer.Ordinal)
                        .Select(a => $"{scheme}://{a}" + ((scheme == "http" && srv.Port == 80) || (scheme == "https" && srv.Port == 443) ? string.Empty : ":" + srv.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                }

                if (advertised is not null && advertised.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(advertised.AbsoluteUri.TrimEnd('/'));
                }

                if (urls.Count > 0)
                {
                    found.Add(new FoundServer(InstanceLabel(instance), txt.GetValueOrDefault("id"), urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
                }
            }

            return found.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IEnumerable<string> Instances()
            => _ptr.TryGetValue(ServiceType, out var instances) ? instances.Order(StringComparer.OrdinalIgnoreCase) : [];

        private static string InstanceLabel(string instance)
            => instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) ? instance[..^(ServiceType.Length + 1)] : instance;

        private bool Parse(byte[] packet)
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0)
            {
                return false; // not a response
            }

            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
            var records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6))
                          + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8))
                          + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
            var at = 12;
            for (var i = 0; i < questions; i++)
            {
                ReadName(packet, ref at);
                at += 4;
            }

            for (var i = 0; i < Math.Min(records, MaxRecordsPerPacket); i++)
            {
                var name = ReadName(packet, ref at);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at));
                var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at + 8));
                var data = at + 10;
                at = data + length;
                if (at > packet.Length)
                {
                    throw new ArgumentException("A record runs past the packet.");
                }

                // Enough kept: nothing more that grows what is held (a known name's SRV or TXT
                // may still be replaced), so a stranger flooding answers can't fill memory.
                var full = Names >= MaxNames;

                switch (type)
                {
                    case TypePtr when !full:
                        var cursor = data;
                        Set(_ptr, name).Add(ReadName(packet, ref cursor));
                        break;
                    case TypeSrv when !full || _srv.ContainsKey(name):
                        var host = data + 6;
                        _srv[name] = (ReadName(packet, ref host), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(data + 4)));
                        break;
                    case TypeTxt when !full || _txt.ContainsKey(name):
                        _txt[name] = ReadTxt(packet.AsSpan(data, length));
                        break;
                    case TypeA when length == 4 && !full:
                        Set(_a, name).Add(new IPAddress(packet.AsSpan(data, 4)));
                        break;
                }
            }

            return true;
        }

        private int Names => _ptr.Count + _srv.Count + _txt.Count + _a.Count + _ptr.Values.Sum(v => v.Count);

        private static HashSet<T> Set<T>(Dictionary<string, HashSet<T>> map, string key)
        {
            if (!map.TryGetValue(key, out var set))
            {
                map[key] = set = [];
            }

            return set;
        }

        private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> data)
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var at = 0;
            while (at < data.Length)
            {
                var length = data[at];
                var entry = Encoding.UTF8.GetString(data.Slice(at + 1, length));
                at += 1 + length;
                var eq = entry.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    pairs.TryAdd(entry[..eq], entry[(eq + 1)..]);
                }
            }

            return pairs;
        }

        // A name, following compression pointers (at most a few, so a loop can't hang it).
        private static string ReadName(byte[] packet, ref int at)
        {
            var labels = new List<string>();
            var cursor = at;
            var jumped = false;
            var bytes = 1;
            for (var hops = 0; hops < 20;)
            {
                var length = packet[cursor];
                if ((length & 0xC0) is 0x40 or 0x80)
                {
                    throw new FormatException("A reserved label type.");
                }

                if ((length & 0xC0) == 0 && (bytes += length + 1) > MaxNameBytes)
                {
                    throw new FormatException("A name longer than DNS allows.");
                }

                if (length == 0)
                {
                    if (!jumped)
                    {
                        at = cursor + 1;
                    }

                    return string.Join('.', labels);
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (!jumped)
                    {
                        at = cursor + 2;
                    }

                    cursor = ((length & 0x3F) << 8) | packet[cursor + 1];
                    jumped = true;
                    hops++;
                    continue;
                }

                labels.Add(Encoding.UTF8.GetString(packet, cursor + 1, length));
                cursor += 1 + length;
            }

            throw new FormatException("A name's pointers loop.");
        }
    }
}

/// <summary>A quipclipper server found on the LAN.</summary>
/// <param name="Name">What it calls itself (its server name).</param>
/// <param name="Id">Its server id, when it said.</param>
/// <param name="Urls">URLs to reach it by, best first.</param>
public sealed record FoundServer(string Name, string? Id, IReadOnlyList<string> Urls);

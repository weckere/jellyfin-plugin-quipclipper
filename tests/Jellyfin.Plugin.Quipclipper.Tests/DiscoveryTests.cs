using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Quipclipper.Server;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>
/// Finding quipclipper on the LAN (D-051). The answers are python-zeroconf's own (the library
/// quipclipper's advertiser uses; Fixtures/zeroconf-answer.bin is what it sends for the record
/// quipclipper registers) or built here; the advertiser is a UDP socket on loopback. Each
/// search ends as soon as what it waits for has come, so nothing depends on timing.
/// </summary>
public sealed class DiscoveryTests
{
    private static readonly byte[] Zeroconf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zeroconf-answer.bin"));

    [Fact]
    public void It_reads_python_zeroconfs_answer_addresses_before_a_dot_local_name()
    {
        var server = Assert.Single(Discovery.Read([Zeroconf]));
        Assert.Equal("QuipClipper on nas", server.Name);
        Assert.Equal("qcsrv_abc123", server.Id);
        // A .local name needs mDNS to resolve, which Jellyfin (in a container above all) usually lacks.
        Assert.Equal(["http://192.168.1.20:8896", "http://nas.local:8896"], server.Urls);
    }

    [Fact]
    public void An_advertised_url_that_isnt_dot_local_comes_first()
    {
        var answer = Packet.Answer(
            Packet.Ptr(Discovery.ServiceType, "nas." + Discovery.ServiceType),
            Packet.Srv("nas." + Discovery.ServiceType, "nas.local", 443),
            Packet.Txt("nas." + Discovery.ServiceType, "v=1", "url=https://quipclipper.example.com/", "id=x"),
            Packet.A("nas.local", "10.0.0.5"));
        var server = Assert.Single(Discovery.Read([answer]));
        Assert.Equal(["https://quipclipper.example.com", "https://10.0.0.5"], server.Urls);
    }

    [Fact]
    public void Broken_packets_are_skipped_and_the_rest_still_read()
    {
        byte[] loop = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 2, 0xC0, 12];
        byte[] truncated = Zeroconf[..60];
        var query = Discovery.Query((Discovery.ServiceType, 12));
        var servers = Discovery.Read([loop, truncated, query, [], Zeroconf]);
        Assert.Equal("QuipClipper on nas", Assert.Single(servers).Name);
    }

    [Fact]
    public void A_name_longer_than_DNS_allows_is_refused()
    {
        var label = new string('a', 63);
        var longName = string.Join('.', Enumerable.Repeat(label, 5)) + ".local"; // > 255 bytes
        var answer = Packet.Answer(Packet.Ptr(Discovery.ServiceType, "x." + Discovery.ServiceType), Packet.A(longName, "10.0.0.1"));
        Assert.Empty(Discovery.Read([answer]));
    }

    [Fact]
    public void A_flood_of_records_keeps_only_a_bounded_number()
    {
        // 64 records a packet are read; 256 names kept across packets: what a stranger
        // on the LAN can make Jellyfin hold stays small, and the real answer still counts.
        var flood = Enumerable.Range(0, 20).Select(p => Packet.Answer(Enumerable.Range(0, 100)
            .Select(i => Packet.Ptr(Discovery.ServiceType, $"junk{p}-{i}." + Discovery.ServiceType)).ToArray())).ToList();
        var servers = Discovery.Read([Zeroconf, .. flood]);
        Assert.Equal("QuipClipper on nas", Assert.Single(servers).Name); // junk has no SRV/A: never listed
    }

    [Fact]
    public async Task An_answer_from_another_port_doesnt_count()
    {
        using var impostor = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var advertiser = new FakeAdvertiser(question =>
        {
            return question == (Discovery.ServiceType, 12) ? [Zeroconf] : [];
        });
        advertiser.Before = to => impostor.Send(Packet.Answer(
            Packet.Ptr(Discovery.ServiceType, "evil." + Discovery.ServiceType),
            Packet.Srv("evil." + Discovery.ServiceType, "evil.local", 80),
            Packet.A("evil.local", "10.6.6.6")), to);
        var found = await new Discovery(advertiser.EndPoint, () => [])
            .FindAsync(TimeSpan.FromMinutes(1), default, servers => servers.Count > 0);
        Assert.Equal("QuipClipper on nas", Assert.Single(found).Name);
    }

    [Fact]
    public void Other_services_answering_are_left_out()
    {
        var answer = Packet.Answer(
            Packet.Ptr("_http._tcp.local", "printer._http._tcp.local"),
            Packet.Srv("printer._http._tcp.local", "printer.local", 80),
            Packet.A("printer.local", "10.0.0.9"));
        Assert.Empty(Discovery.Read([answer]));
    }

    [Fact]
    public void The_query_asks_for_the_service_once()
    {
        byte[] want =
        [
            0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            12, .. "_quipclipper"u8, 4, .. "_tcp"u8, 5, .. "local"u8, 0,
            0, 12, 0, 1,
        ];
        Assert.Equal(want, Discovery.Query((Discovery.ServiceType, 12)));
    }

    [Fact]
    public async Task A_LAN_advertiser_is_found_over_the_network()
    {
        using var advertiser = new FakeAdvertiser(question =>
            question == (Discovery.ServiceType, 12) ? [Zeroconf] : []);
        var found = await new Discovery(advertiser.EndPoint, () => [])
            .FindAsync(TimeSpan.FromMinutes(1), default, servers => servers.Count > 0);
        var server = Assert.Single(found);
        Assert.Equal(("QuipClipper on nas", "http://192.168.1.20:8896"), (server.Name, server.Urls[0]));
        Assert.Equal([(Discovery.ServiceType, (ushort)12)], advertiser.Asked);
    }

    [Fact]
    public async Task An_advertiser_that_answers_the_pointer_alone_is_asked_for_the_rest()
    {
        var instance = "den." + Discovery.ServiceType;
        using var advertiser = new FakeAdvertiser(question => question switch
        {
            (Discovery.ServiceType, 12) => [Packet.Answer(Packet.Ptr(Discovery.ServiceType, instance))],
            ({ } n, 33) when n == instance => [Packet.Answer(Packet.Srv(instance, "den.local", 8080))],
            ({ } n, 16) when n == instance => [Packet.Answer(Packet.Txt(instance, "v=1", "url=http://den.local:8080", "id=qcsrv_den"))],
            ("den.local", 1) => [Packet.Answer(Packet.A("den.local", "192.168.1.30"))],
            _ => [],
        });
        var found = await new Discovery(advertiser.EndPoint, () => [])
            .FindAsync(TimeSpan.FromMinutes(1), default, servers => servers.Count > 0 && servers[0].Urls.Count == 2);
        var server = Assert.Single(found);
        Assert.Equal(["http://192.168.1.30:8080", "http://den.local:8080"], server.Urls);
        Assert.Equal("qcsrv_den", server.Id);
    }

    [Fact]
    public async Task Nobody_answering_finds_nothing()
    {
        using var silent = new FakeAdvertiser(_ => []);
        Assert.Empty(await new Discovery(silent.EndPoint, () => []).FindAsync(TimeSpan.FromMilliseconds(200), default));
    }

    /// <summary>A quipclipper advertiser on loopback: answers each question it is asked by unicast, as RFC 6762 §6.7 has a responder answer a one-shot query.</summary>
    private sealed class FakeAdvertiser : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<(string Name, ushort Type), byte[][]> _answers;

        public FakeAdvertiser(Func<(string Name, ushort Type), byte[][]> answers)
        {
            _answers = answers;
            _ = Task.Run(ServeAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

        public List<(string Name, ushort Type)> Asked { get; } = [];

        /// <summary>Runs before each answer is sent, with where it goes (an impostor answers first).</summary>
        public Action<IPEndPoint>? Before { get; set; }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    var query = await _udp.ReceiveAsync(_stop.Token);
                    foreach (var question in Packet.Questions(query.Buffer))
                    {
                        lock (Asked)
                        {
                            Asked.Add(question);
                        }

                        foreach (var answer in _answers(question))
                        {
                            Before?.Invoke(query.RemoteEndPoint);
                            await _udp.SendAsync(answer, query.RemoteEndPoint, _stop.Token);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    /// <summary>mDNS answers built by hand (no name compression).</summary>
    private static class Packet
    {
        public static byte[] Answer(params byte[][] records)
        {
            var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
            foreach (var r in records)
            {
                packet.AddRange(r);
            }

            return packet.ToArray();
        }

        public static byte[] Ptr(string name, string target) => Record(name, 12, Name(target));

        public static byte[] Srv(string name, string host, int port) =>
            Record(name, 33, [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. Name(host)]);

        public static byte[] Txt(string name, params string[] entries) =>
            Record(name, 16, entries.SelectMany(e => new[] { (byte)Encoding.UTF8.GetByteCount(e) }.Concat(Encoding.UTF8.GetBytes(e))).ToArray());

        public static byte[] A(string name, string address) => Record(name, 1, IPAddress.Parse(address).GetAddressBytes());

        public static IEnumerable<(string Name, ushort Type)> Questions(byte[] query)
        {
            var count = (query[4] << 8) | query[5];
            var at = 12;
            for (var i = 0; i < count; i++)
            {
                var labels = new List<string>();
                while (query[at] != 0)
                {
                    labels.Add(Encoding.UTF8.GetString(query, at + 1, query[at]));
                    at += 1 + query[at];
                }

                yield return (string.Join('.', labels), (ushort)((query[at + 1] << 8) | query[at + 2]));
                at += 5;
            }
        }

        private static byte[] Name(string name) =>
            name.Split('.').SelectMany(l => new[] { (byte)Encoding.UTF8.GetByteCount(l) }.Concat(Encoding.UTF8.GetBytes(l))).Append((byte)0).ToArray();

        private static byte[] Record(string name, ushort type, byte[] data) =>
            [.. Name(name), (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length, .. data];
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Server;
using VagabondK.nF.MQTT;

/// <summary>루프백 MQTTnet 브로커(평문 + 자체 서명 TLS 1.2, 영구 세션)를 상대로 반복 교환하는 장시간 검사입니다.</summary>
/// <remarks><c>--soak</c> 인수로만 실행되며 반복 횟수·시간 등은 <c>SoakRunner</c>가 인수에서 해석합니다. 외부 네트워크는 사용하지 않습니다.</remarks>
internal static class MqttSoakChecks
{
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); return port;
    }
    internal static async Task Run(string[] args)
    {
        int plainPort = Port(), tlsPort = Port();
        while (tlsPort == plainPort) tlsPort = Port();
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using (var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2)))
            using (var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null))
            using (var broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
                .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(plainPort)
                .WithEncryptedEndpoint().WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback).WithEncryptedEndpointPort(tlsPort)
                .WithEncryptionSslProtocol(SslProtocols.Tls12).WithEncryptionCertificate(certificate)
                .WithPersistentSessions(true).Build()))
            {
                await broker.StartAsync();
                try
                {
                    await Task.Run(() => {
                        using (var plain = new Peer(plainPort, 4, "soak-plain", null))
                        using (var secure = new Peer(tlsPort, 5, "soak-tls", certificate))
                        {
                            SoakRunner.TryRun(args, "MQTT",
                                () => { plain.Exchange(); secure.Exchange(); },
                                () => { Mqtt5Checks.Run(); },
                                () => Lifecycle(plainPort));
                        }
                    });
                }
                finally { await broker.StopAsync(); }
            }
        }
    }
    private static void Lifecycle(int port)
    {
        var state = new MqttSessionState();
        for (int iteration = 0; iteration < 2; iteration++)
        {
            using (var peer = new Peer(port, 5, "soak-session", null, state, iteration != 0))
            {
                using (var completed = new ManualResetEventSlim())
                using (var worker = new MqttWorker(peer.Client, 2))
                {
                    Exception failure = null;
                    worker.Completed += (id, error) => { failure = error; completed.Set(); };
                    worker.Enqueue(connection => peer.Exchange());
                    if (!completed.Wait(10000)) throw new TimeoutException("Soak worker completion.");
                    if (failure != null) throw new IOException("Soak worker failed.", failure);
                }
                peer.Client.Unsubscribe("soak-session");
                if (!peer.Client.Subscribe("soak-session", MqttQos.ExactlyOnce)) throw new IOException("Soak resubscribe refused.");
                peer.Client.Disconnect();
            }
        }
    }
    private sealed class Peer : IDisposable
    {
        private readonly Socket socket;
        private readonly SslStream tls;
        private readonly byte protocol;
        private readonly string topic;
        private readonly byte[] payload = new byte[512];
        private int sequence, delivered;
        private byte[] expected;
        internal MqttConnection Client { get; private set; }
        internal Peer(int port, byte protocol, string id, X509Certificate2 certificate,
            MqttSessionState state = null, bool present = false)
        {
            this.protocol = protocol; topic = id;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            bool success = false;
            try
            {
                socket.Connect(IPAddress.Loopback, port);
                IMqttChannel channel;
                if (certificate != null)
                {
                    tls = new SslStream(new NetworkStream(socket, false), false);
                    var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck };
                    policy.CustomTrustStore.Add(certificate);
                    tls.AuthenticateAsClient(new SslClientAuthenticationOptions {
                        TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12, CertificateChainPolicy = policy });
                    // 이 픽스처는 응답/발행을 기다릴 때만 읽고 유휴 Poll은 하지 않으므로 준비 검사를 항상 참으로 둡니다.
                    channel = new StreamMqttChannel(tls, timeout => true);
                }
                else channel = new SocketMqttChannel(socket);
                Client = new MqttConnection(channel, state ?? new MqttSessionState());
                Client.MessageReceived = message => {
                    if (expected == null || message.Topic != topic || !message.Payload.SequenceEqual(expected))
                        throw new IOException("Soak echo byte/sequence mismatch.");
                    if (protocol == 5 && (!message.Properties.Any(p => p.Id == MqttPropertyId.ContentType && p.Text == "binary") ||
                        !message.Properties.Any(p => p.Id == MqttPropertyId.SubscriptionIdentifier && p.Number == 42)))
                        throw new IOException("Soak MQTT 5 metadata lost.");
                    delivered++;
                };
                Client.Connect(new MqttConnectionOptions { ClientId = id, ProtocolLevel = protocol,
                    CleanSession = !present, KeepAliveSeconds = 1,
                    Properties = protocol == 5 ? new[] { new MqttProperty(MqttPropertyId.SessionExpiryInterval, state == null ? 0u : 60u) } : null });
                if (Client.SessionPresent != present) throw new IOException("Soak persistent session presence mismatch.");
                byte[] grants = Client.Subscribe(new[] { topic }, new byte[] { 2 },
                    protocol == 5 ? new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 42u) } : null);
                if (grants[0] != 2) throw new IOException("Soak subscription refused.");
                success = true;
            }
            finally { if (!success) Dispose(); }
        }
        internal void Exchange()
        {
            for (int i = 0; i < 12; i++)
            {
                int number = ++sequence;
                for (int j = 0; j < payload.Length; j++) payload[j] = (byte)(j ^ number);
                payload[0] = (byte)(number >> 24); payload[1] = (byte)(number >> 16);
                payload[2] = (byte)(number >> 8); payload[3] = (byte)number;
                expected = payload; delivered = 0;
                var properties = protocol == 5 ? new[] { new MqttProperty(MqttPropertyId.ContentType, "binary"), new MqttProperty("cycle", number.ToString()) } : null;
                long started = SoakRunner.StartExchange();
                int count;
                if (i % 4 == 3)
                {
                    count = 4;
                    var requests = new MqttPublishRequest[count];
                    for (int j = 0; j < count; j++)
                        requests[j] = new MqttPublishRequest { Topic = topic, Payload = payload, Qos = (MqttQos)(j % 3), Properties = properties };
                    if (Client.PublishBatch(requests).Any(reason => reason >= 128)) throw new IOException("Soak batch refusal.");
                }
                else { count = 1; Client.Publish(topic, payload, (MqttQos)(i % 3), false, properties); }
                long deadline = Environment.TickCount64 + 5000;
                while (delivered < count && Environment.TickCount64 < deadline) Client.Poll();
                if (delivered != count) throw new IOException("Soak echo missing/duplicated.");
                SoakRunner.EndExchange(started);
            }
            Client.Ping();
        }
        public void Dispose()
        {
            if (Client != null) Client.Abort();
            if (tls != null) tls.Dispose();
            socket.Dispose();
        }
    }
}

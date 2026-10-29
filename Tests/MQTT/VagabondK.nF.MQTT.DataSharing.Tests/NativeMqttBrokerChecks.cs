using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;
using MqttPropertyId = VagabondK.nF.MQTT.MqttPropertyId;
using MQTTnet.Server;
using VagabondK.nF.MQTT;

/// <summary>라이브러리 <see cref="MqttConnection"/>과 실제 MQTTnet 브로커(루프백, 임의 포트)의 상호 운용을 검사합니다.</summary>
/// <remarks>MQTT 3.1.1·MQTT 5 연결/인증/구독/발행, <see cref="MqttWorker"/> 동작, 자체 서명 인증서를 쓴 TLS를 확인합니다. 외부 장비는 필요 없습니다.</remarks>
internal static class NativeMqttBrokerChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static int Port()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop(); return port;
    }
    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(IPAddress.Loopback, port); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static MqttConnectionOptions Options(string id, bool clean = true)
    {
        return new MqttConnectionOptions { ClientId = id, UserName = "test",
            Password = new byte[] { 0, 255, 128 }, KeepAliveSeconds = 2, CleanSession = clean };
    }
    internal static async Task Run()
    {
        var factory = new MqttFactory();
        int port = Port();
        using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).WithPersistentSessions(true).Build()))
        {
            broker.ValidatingConnectionAsync += args => {
                if (string.IsNullOrEmpty(args.ClientId)) args.AssignedClientIdentifier = "assigned-mqtt5";
                if (args.UserName != "test" || !args.RawPassword.SequenceEqual(new byte[] { 0, 255, 128 }))
                    args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
            await broker.StartAsync();
            try
            {
                await Task.Run(() => Interoperate(port));
                await Task.Run(() => Interoperate5(port));
                await Task.Run(() => Worker(port));
            }
            finally { await broker.StopAsync(); }
        }
        await Tls(factory);
        Console.WriteLine("NATIVE MQTT CONTRACT PASS brokerChecks=" + checks);
    }
    private static void Interoperate(int port)
    {
        const string topic = "test/\uC7A5\uCE58/\ud83d\ude80";
        byte[] payload = { 0, 128, 255, 0 };
        var state = new MqttSessionState();
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket), state);
            client.Connect(Options("persistent", false));
            Check(!client.SessionPresent, "New MQTTnet session absent.");
            Check(client.Subscribe(new[] { topic, "unused/+" }, new byte[] { 2, 1 }).SequenceEqual(new byte[] { 2, 1 }), "MQTTnet batch grants.");
            int deliveries = 0;
            client.Received = (t, p, r, d) => { Check(t == topic && p.SequenceEqual(payload), "MQTTnet QoS2 UTF-8/binary echo."); deliveries++; };
            client.Publish(topic, payload, MqttQos.ExactlyOnce, true);
            long deadline = Environment.TickCount64 + 3000;
            while (deliveries == 0 && Environment.TickCount64 < deadline) client.Poll(100);
            Check(deliveries == 1, "MQTTnet QoS2 delivered exactly once within session.");
            client.Disconnect();
        }
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket), state);
            client.Connect(Options("persistent", false));
            Check(client.SessionPresent, "MQTTnet persistent broker session resumed.");
            client.Unsubscribe(topic, "unused/+");
            client.Publish(topic, payload, MqttQos.ExactlyOnce);
            Check(!client.Poll(100), "MQTTnet unsubscribe removes delivery.");
            bool retained = false;
            client.Received = (t, p, r, d) => { Check(r && t == topic && p.SequenceEqual(payload), "MQTTnet retained replay."); retained = true; };
            Check(client.Subscribe(topic, MqttQos.ExactlyOnce), "MQTTnet re-subscribe.");
            long deadline = Environment.TickCount64 + 3000;
            while (!retained && Environment.TickCount64 < deadline) client.Poll(100);
            Check(retained, "Retained publication survived session reconnect.");
            client.Disconnect();
        }
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket));
            var bad = Options("refused"); bad.Password = new byte[] { 1 };
            try { client.Connect(bad); throw new InvalidOperationException("Bad credentials accepted."); }
            catch (IOException) { Check(client.Faulted, "MQTTnet credential refusal faults explicitly."); }
        }
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket));
            var options = Options("mqtt31"); options.ProtocolLevel = 3;
            client.Connect(options); client.Publish("mqtt31", payload, MqttQos.ExactlyOnce); client.Disconnect();
            Check(!client.Faulted, "MQTT 3.1 QoS2 interoperates with MQTTnet.");
        }
    }
    private static void Worker(int port)
    {
        using (var socket = Connect(port))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var client = new MqttConnection(new SocketMqttChannel(socket)); client.Connect(Options("worker"));
            client.Subscribe("worker/data", MqttQos.AtMostOnce);
            bool copied = false;
            client.Received = (topic, body, retained, duplicate) => copied = body.SequenceEqual(new byte[] { 1, 2, 3 });
            int completions = 0, errors = 0;
            using (var worker = new MqttWorker(client, 1))
            {
                worker.Completed += (id, error) => { if (error != null) Interlocked.Increment(ref errors); Interlocked.Increment(ref completions); };
                try
                {
                    worker.Enqueue(c => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException("Test release."); });
                    Check(entered.Wait(3000), "Worker executes independently.");
                    byte[] mutable = { 1, 2, 3 };
                    worker.Publish("worker/data", mutable, MqttQos.ExactlyOnce); mutable[0] = 9;
                    try { worker.Enqueue(c => { }); throw new InvalidOperationException("Queue overflow accepted."); }
                    catch (InvalidOperationException error) { Check(error.Message == "MQTT work queue full.", "Worker queue bound explicit."); }
                }
                finally { release.Set(); }
            }
            Check(completions == 2 && errors == 0 && copied && client.Connected, "Worker drains admitted QoS2 operations and copies queued payload.");
            using (var worker = new MqttWorker(client))
            {
                entered.Reset(); release.Reset();
                int executed = 0;
                worker.Completed += (id, error) => { if (error != null) Interlocked.Increment(ref errors); };
                try
                {
                    worker.Enqueue(c => {
                        entered.Set();
                        if (!release.Wait(3000)) throw new TimeoutException("Test failure release.");
                        throw new IOException("Deliberate queued failure.");
                    });
                    Check(entered.Wait(3000), "Failure test action entered.");
                    worker.Enqueue(c => executed++);
                    worker.Enqueue(c => executed++);
                }
                finally { release.Set(); }
                worker.Dispose();
                Check(worker.Failure is IOException && errors == 3 && executed == 0,
                    "Worker failure retained and all queued work explicitly failed without execution.");
            }
            client.Disconnect();
        }
    }
    private static void Interoperate5(int port)
    {
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket));
            var options = Options(""); options.ProtocolLevel = 5;
            client.Connect(options);
            Check(!string.IsNullOrEmpty(client.ClientId), "MQTTnet server-assigned MQTT 5 identifier.");
            client.Disconnect();
        }
        using (var socket = Connect(port))
        {
            var client = new MqttConnection(new SocketMqttChannel(socket));
            var options = Options("mqtt5"); options.ProtocolLevel = 5;
            options.Properties = new[] { new MqttProperty(MqttPropertyId.SessionExpiryInterval, 30u), new MqttProperty(MqttPropertyId.TopicAliasMaximum, 4u),
                new MqttProperty(MqttPropertyId.RequestResponseInformation, 1u), new MqttProperty("client", "native") };
            options.WillTopic = "mqtt5/death"; options.WillPayload = new byte[] { 255, 0 };
            options.WillQos = MqttQos.ExactlyOnce; options.WillProperties = new[] { new MqttProperty(MqttPropertyId.WillDelayInterval, 1u), new MqttProperty(MqttPropertyId.ContentType, "application/octet-stream") };
            client.Connect(options);
            Check(client.Connected, "MQTT 5 CONNECT properties and binary Will accepted by MQTTnet.");
            byte[] grants = client.Subscribe(new[] { "mqtt5/data" }, new byte[] { 2 },
                new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 123u), new MqttProperty("sub", "v") });
            Check(grants.SequenceEqual(new byte[] { 2 }), "MQTT 5 SUBACK properties/reasons.");
            int deliveries = 0;
            client.MessageReceived = message => {
                Check(message.Topic == "mqtt5/data" && message.Qos == MqttQos.ExactlyOnce && message.Payload.SequenceEqual(new byte[] { 0, 255 }) &&
                    message.Properties.Any(p => p.Id == MqttPropertyId.ContentType && p.Text == "application/octet-stream") &&
                    message.Properties.Any(p => p.Id == MqttPropertyId.ResponseTopic && p.Text == "mqtt5/response") &&
                    message.Properties.Any(p => p.Id == MqttPropertyId.CorrelationData && p.Bytes.SequenceEqual(new byte[] { 128, 0 })) &&
                    message.Properties.Any(p => p.Id == MqttPropertyId.SubscriptionIdentifier && p.Number == 123) &&
                    message.Properties.Count(p => p.Id == MqttPropertyId.UserProperty) == 2, "MQTTnet independent MQTT 5 metadata round-trip.");
                deliveries++;
            };
            client.Publish("mqtt5/data", new byte[] { 0, 255 }, MqttQos.ExactlyOnce, false, new[] {
                new MqttProperty(MqttPropertyId.ContentType, "application/octet-stream"), new MqttProperty(MqttPropertyId.ResponseTopic, "mqtt5/response"),
                new MqttProperty(MqttPropertyId.CorrelationData, new byte[] { 128, 0 }), new MqttProperty(MqttPropertyId.MessageExpiryInterval, 60u),
                new MqttProperty("a", "b"), new MqttProperty("a", "c") });
            long deadline = Environment.TickCount64 + 3000;
            while (deliveries == 0 && Environment.TickCount64 < deadline) client.Poll();
            Check(deliveries == 1, "MQTT 5 QoS2 echo delivered.");
            var metadata = new[] { new MqttProperty(MqttPropertyId.ContentType, "application/octet-stream"), new MqttProperty(MqttPropertyId.ResponseTopic, "mqtt5/response"),
                new MqttProperty(MqttPropertyId.CorrelationData, new byte[] { 128, 0 }), new MqttProperty("a", "b"), new MqttProperty("a", "c") };
            byte[] batch = client.PublishBatch(new[] {
                new MqttPublishRequest { Topic = "mqtt5/data", Payload = new byte[] { 0, 255 }, Qos = MqttQos.ExactlyOnce, Properties = metadata },
                new MqttPublishRequest { Topic = "mqtt5/data", Payload = new byte[] { 0, 255 }, Qos = MqttQos.ExactlyOnce, Properties = metadata } });
            deadline = Environment.TickCount64 + 3000;
            while (deliveries < 3 && Environment.TickCount64 < deadline) client.Poll();
            Check(deliveries == 3 && batch.All(reason => reason < 128), "MQTTnet multiple outgoing in-flight QoS2 metadata echo.");
            Check(client.Unsubscribe(new[] { "mqtt5/data" }, new[] { new MqttProperty("u", "v") }).SequenceEqual(new byte[] { 0 }),
                "MQTT 5 UNSUBACK reason consumed.");
            client.Disconnect(0, new[] { new MqttProperty(MqttPropertyId.SessionExpiryInterval, 0u), new MqttProperty("done", "yes") });
            Check(!client.Faulted, "MQTT 5 DISCONNECT properties accepted.");
        }
    }
    private static async Task Tls(MqttFactory factory)
    {
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using (var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)))
            using (var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null))
            {
                int port = Port();
                using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithoutDefaultEndpoint()
                    .WithEncryptedEndpoint().WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback).WithEncryptedEndpointPort(port)
                    .WithEncryptionSslProtocol(SslProtocols.Tls12).WithEncryptionCertificate(certificate).Build()))
                {
                    await broker.StartAsync();
                    try
                    {
                        await Task.Run(() => {
                            using (var socket = Connect(port))
                            using (var tls = new SslStream(new NetworkStream(socket, false)))
                            {
                                tls.AuthenticateAsClient(TlsOptions("localhost", certificate));
                                // 이 픽스처는 응답을 기다리는 작업에서만 읽고 유휴 Poll은 하지 않으므로 준비 검사를 항상 참으로 둡니다.
                                // nanoFramework 호출자는 대신 SslStream.DataAvailable과 socket.Poll을 함께 사용합니다.
                                var client = new MqttConnection(new StreamMqttChannel(tls, ms => true));
                                client.Connect("verified-tls"); client.Publish("tls/data", new byte[] { 0, 255 }, MqttQos.ExactlyOnce); client.Disconnect();
                                Check(!client.Faulted, "Authenticated TLS stream carries MQTTnet QoS2.");
                            }
                            using (var socket = Connect(port))
                            using (var tls = new SslStream(new NetworkStream(socket, false)))
                            {
                                try
                                {
                                    tls.AuthenticateAsClient(TlsOptions("wrong-name.invalid", certificate));
                                    throw new InvalidOperationException("TLS hostname mismatch accepted.");
                                }
                                catch (AuthenticationException) { Check(true, "TLS hostname mismatch rejected before MQTT."); }
                            }
                            using (var socket = Connect(port))
                            using (var tls = new SslStream(new NetworkStream(socket, false)))
                            {
                                try
                                {
                                    tls.AuthenticateAsClient(new SslClientAuthenticationOptions {
                                        TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12,
                                        CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
                                            RevocationMode = X509RevocationMode.NoCheck }
                                    });
                                    throw new InvalidOperationException("Untrusted TLS certificate accepted.");
                                }
                                catch (AuthenticationException) { Check(true, "TLS untrusted issuer rejected before MQTT."); }
                            }
                        });
                    }
                    finally { await broker.StopAsync(); }
                }
            }
        }
    }
    private static SslClientAuthenticationOptions TlsOptions(string host, X509Certificate2 certificate)
    {
        return new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12,
            CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { certificate }, RevocationMode = X509RevocationMode.NoCheck } };
    }
}

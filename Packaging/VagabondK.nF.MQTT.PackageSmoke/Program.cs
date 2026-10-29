using System;
using System.Net.Sockets;
using VagabondK.nF.MQTT;

internal static class Program
{
    public static void Main()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            var state = new MqttSessionState(32, 32);
            var client = new MqttConnection(new SocketMqttChannel(socket), state, 4096, 3000, 1024, 32);
            var options = new MqttConnectionOptions { ClientId = "package-smoke", ProtocolLevel = 5,
                CleanSession = false, AcknowledgmentRetries = 1, WillQos = MqttQos.ExactlyOnce, WillRetain = true,
                Properties = new[] { new MqttProperty(MqttPropertyId.SessionExpiryInterval, 30u), new MqttProperty(MqttPropertyId.AuthenticationMethod, "method") },
                Authenticate = (reason, properties) => new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "method") } };
            if (client.Connected || client.Faulted || state.HasPendingPublication || options.ProtocolLevel != 5)
                throw new InvalidOperationException("Initial state.");
        }
    }
    private static void ConnectedApi(MqttConnection client)
    {
        client.Subscribe(new[] { "a/+", "b/#" }, new byte[] { 1, 2 });
        client.Unsubscribe("a/+", "b/#");
        client.Publish("binary", new byte[] { 0, 255 }, MqttQos.ExactlyOnce, true);
        client.MessageReceived = message => { if (message.Properties == null) throw new InvalidOperationException("Metadata"); };
        client.Subscribe(new[] { "v5" }, new byte[] { 2 }, new[] { new MqttProperty(MqttPropertyId.SubscriptionIdentifier, 42u) });
        client.Publish("v5", new byte[] { 0, 255 }, MqttQos.ExactlyOnce, false, new[] { new MqttProperty(MqttPropertyId.ContentType, "binary"), new MqttProperty("k", "v") });
        client.PublishBatch(new[] { new MqttPublishRequest { Topic = "v5", Payload = new byte[0], Qos = MqttQos.ExactlyOnce } });
        client.Reauthenticate(new[] { new MqttProperty(MqttPropertyId.AuthenticationMethod, "method") });
        client.Ping();
        using (var worker = new MqttWorker(client, 2))
        {
            worker.Completed += (id, error) => { if (error != null) throw error; };
            worker.Publish("worker", new byte[] { 0, 255 }, MqttQos.ExactlyOnce);
            worker.Enqueue(connection => connection.Unsubscribe("b/#"));
        }
        client.Abort();
    }
    private static void TlsApi(Socket socket, System.Security.Cryptography.X509Certificates.X509Certificate root)
    { using (var channel = new NanoTlsMqttChannel(socket, "broker.example", root)) { } }
}

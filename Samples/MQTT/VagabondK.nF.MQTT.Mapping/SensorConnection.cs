using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using VagabondK.nF.MQTT;

namespace VagabondK.nF.MQTT.Mapping
{
    // 연결·수신·발행은 같은 스레드에서 실행합니다. QoS 1 발행은 PUBACK까지 기다립니다.
    internal sealed class SensorConnection : IDisposable
    {
        private readonly string address, incomingTopic, outgoingTopic;
        private readonly int port;
        private Socket socket;
        private MqttConnection mqtt;

        internal SensorConnection(string address, int port, string incomingTopic, string outgoingTopic)
        {
            this.address = address; this.port = port;
            this.incomingTopic = incomingTopic; this.outgoingTopic = outgoingTopic;
            Mapping = new RemoteSensorMapping(incomingTopic, 3000);
        }

        internal RemoteSensorMapping Mapping { get; private set; }
        internal int Published { get; private set; }
        internal int Applied { get; private set; }
        internal int Rejected { get; private set; }
        internal int Subscribed { get; private set; }
        internal bool Connected { get { return mqtt != null && mqtt.Connected; } }

        internal void Connect()
        {
            if (socket != null) throw new InvalidOperationException("Disconnect before reconnecting.");
            bool success = false;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Connect(new IPEndPoint(IPAddress.Parse(address), port));
                mqtt = new MqttConnection(new SocketMqttChannel(socket));
                mqtt.Received = (topic, bytes, retained, duplicate) => {
                    ApplyResult result = Mapping.Apply(topic, bytes, retained, Environment.TickCount64);
                    if (result == ApplyResult.Applied) Applied++; else Rejected++;
                };
                mqtt.Connect(new MqttConnectionOptions {
                    ClientId = "vknf-sharing-device-b", ProtocolLevel = 4, CleanSession = true,
                    OperationTimeoutMilliseconds = 10000, AcknowledgmentRetries = 0
                });
                Mapping.SetConnected(true);
                byte[] granted = mqtt.Subscribe(new[] { incomingTopic }, new[] { (byte)MqttQos.AtLeastOnce });
                if (granted.Length != 1 || granted[0] != (byte)MqttQos.AtLeastOnce)
                    throw new IOException("Subscription requires QoS 1.");
                Subscribed++;
                success = true;
            }
            finally { if (!success) Dispose(); }
        }

        internal void Publish(SensorSnapshot snapshot)
        {
            try
            {
                RequireConnection();
                mqtt.Publish(outgoingTopic, SnapshotJson.Encode(snapshot), MqttQos.AtLeastOnce, false);
                Published++;
            }
            finally { UpdateConnectionState(); }
        }

        internal void Poll(int waitMilliseconds)
        {
            try { RequireConnection(); mqtt.Poll(waitMilliseconds); }
            finally { UpdateConnectionState(); }
        }

        private void RequireConnection()
        {
            if (mqtt == null || !mqtt.Connected) throw new IOException("MQTT disconnected; no automatic replay.");
        }

        private void UpdateConnectionState()
        {
            if (mqtt == null || !mqtt.Connected) Mapping.SetConnected(false);
        }

        public void Dispose()
        {
            Mapping.SetConnected(false);
            try { if (mqtt != null && mqtt.Connected) mqtt.Disconnect(); }
            finally
            {
                try { if (socket != null) socket.Close(); }
                finally { socket = null; mqtt = null; }
            }
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using VagabondK.nF.MQTT.Mapping;

internal static class SharingSampleChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Until(SensorConnection sample, Func<bool> ready)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (!ready())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Sample receive.");
            sample.Poll(20);
        }
    }

    internal static async Task Run()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        const string incoming = "vknf/sample/device-a/sensor", outgoing = "vknf/sample/device-b/sensor";
        var factory = new MqttFactory();
        using (var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build()))
        using (var peer = factory.CreateMqttClient())
        {
            int received = 0;
            SensorSnapshot last = null;
            peer.ApplicationMessageReceivedAsync += args => {
                Check(args.ApplicationMessage.Topic == outgoing && !args.ApplicationMessage.Retain, "Sample outgoing topic/retain.");
                SensorSnapshot value;
                Check(SnapshotJson.TryDecode(args.ApplicationMessage.PayloadSegment.ToArray(), out value), "Sample outgoing JSON.");
                last = value;
                Interlocked.Increment(ref received);
                return Task.CompletedTask;
            };
            await broker.StartAsync();
            try
            {
                await peer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId("sample-peer").Build());
                await peer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(outgoing, MqttQualityOfServiceLevel.AtLeastOnce).Build());
                Func<int, bool, Task> send = async (sequence, retained) => {
                    await peer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(incoming)
                        .WithPayload(SnapshotJson.Encode(new SensorSnapshot(sequence, 235, true)))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retained).Build());
                };
                await send(1, true);
                await Task.Run(() => {
                    using (var sample = new SensorConnection("127.0.0.1", port, incoming, outgoing))
                    {
                        sample.Connect();
                        Until(sample, () => sample.Rejected == 1);
                        Check(sample.Subscribed == 1 && sample.Mapping.Read() == null, "Retained replay is not fresh.");
                        send(2, false).GetAwaiter().GetResult();
                        Until(sample, () => sample.Applied == 1);
                        Check(sample.Mapping.Read().SourceSequence == 2 && sample.Mapping.IsAvailable(Environment.TickCount64),
                            "Sample incoming model and freshness.");
                        send(2, false).GetAwaiter().GetResult();
                        Until(sample, () => sample.Rejected == 2);
                        sample.Publish(new SensorSnapshot(1, 251, true));
                        Until(sample, () => Volatile.Read(ref received) == 1);
                        Check(sample.Published == 1 && last.Sequence == 1 && last.TemperatureDeciC == 251,
                            "Sample QoS1 outgoing model.");
                        for (int sequence = 2; sequence <= 30; sequence++)
                            sample.Publish(new SensorSnapshot(sequence, 250 + sequence, sequence % 2 != 0));
                        Until(sample, () => Volatile.Read(ref received) == 30);
                        Check(sample.Published == 30 && last.Sequence == 30 && last.TemperatureDeciC == 280 && !last.Running,
                            "Normal sample completes thirty publications without changing its contract.");
                        sample.Dispose();
                        Check(!sample.Connected && !sample.Mapping.IsAvailable(Environment.TickCount64), "Disconnect invalidates freshness.");
                        sample.Connect();
                        Until(sample, () => sample.Rejected == 3);
                        Check(sample.Subscribed == 2 && !sample.Mapping.IsAvailable(Environment.TickCount64), "Reconnect does not refresh old data.");
                        send(2, false).GetAwaiter().GetResult();
                        Until(sample, () => sample.Rejected == 4);
                        send(3, false).GetAwaiter().GetResult();
                        Until(sample, () => sample.Applied == 2);
                        Check(sample.Mapping.Read().SourceSequence == 3 && sample.Mapping.IsAvailable(Environment.TickCount64),
                            "New sequence restores availability.");
                        broker.StopAsync().GetAwaiter().GetResult();
                        try
                        {
                            Until(sample, () => false);
                            throw new InvalidOperationException("Broker shutdown was not reported.");
                        }
                        catch (IOException)
                        {
                            Check(!sample.Connected && !sample.Mapping.IsAvailable(Environment.TickCount64),
                                "Transport failure invalidates freshness.");
                        }
                        sample.Dispose();
                        try
                        {
                            sample.Connect();
                            throw new InvalidOperationException("Stopped broker connection accepted.");
                        }
                        catch (SocketException)
                        {
                            Check(!sample.Connected && !sample.Mapping.IsAvailable(Environment.TickCount64),
                                "Failed connect releases socket and leaves mapping unavailable.");
                        }
                        broker.StartAsync().GetAwaiter().GetResult();
                        sample.Connect();
                        Check(sample.Connected && sample.Subscribed == 3 && !sample.Mapping.IsAvailable(Environment.TickCount64),
                            "Explicit reconnect works after failed connect without reusing the failed socket.");
                    }
                });
            }
            finally
            {
                if (peer.IsConnected) await peer.DisconnectAsync();
                await broker.StopAsync();
            }
        }
        Console.WriteLine("MQTT SAMPLE PASS: native connection, QoS1, JSON exchange, retained/duplicate rejection, reconnect and transport failure.");
    }
}

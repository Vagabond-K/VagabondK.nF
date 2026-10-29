using System;
using System.Threading;

namespace VagabondK.nF.MQTT
{
    /// <summary>대기열 작업 하나가 끝났음을 알리는 콜백입니다. 워커 스레드에서 실행됩니다.</summary>
    /// <param name="workId"><see cref="MqttWorker.Enqueue(MqttWork)"/>가 반환한 작업 ID입니다. 작업 밖(폴링 등)에서 난 오류면 0입니다.</param>
    /// <param name="error">성공이면 <see langword="null"/>, 실패했거나 워커 중지로 실행되지 못했으면 원인 예외입니다.</param>
    public delegate void MqttWorkCompleted(long workId, Exception error);
    /// <summary>MQTT 워커 스레드에서 배타적으로 실행되는 작업 하나입니다.</summary>
    /// <param name="connection">워커가 감싼 연결입니다. 이 콜백 안에서만 직접 호출할 수 있습니다.</param>
    public delegate void MqttWork(MqttConnection connection);

    /// <summary>연결된 <see cref="MqttConnection"/>을 전용 스레드 하나에서 다루는 선택적 제한 크기 작업 스케줄러입니다.</summary>
    /// <remarks>
    /// <para>워커 스레드는 대기열 작업을 차례로 실행하고, 대기열이 비어 있으면 <see cref="MqttConnection.Poll(int)"/>(10ms)로 수신과 keepalive를 처리합니다.
    /// 따라서 수신 콜백도 워커 스레드에서 실행됩니다.</para>
    /// <para>채널과 연결의 수명은 소유하지 않습니다. 워커가 실행 중일 때는 연결을 다른 스레드에서 직접 호출하면 안 됩니다.
    /// 작업이나 <see cref="Completed"/> 콜백이 예외를 던지면 워커는 중지되고, 남은 작업은 같은 예외로 완료 통지됩니다.</para>
    /// </remarks>
    public sealed class MqttWorker : IDisposable
    {
        private readonly MqttConnection connection;
        private readonly MqttWork[] queue;
        private readonly long[] ids;
        private readonly object gate = new object();
        private readonly Thread thread;
        private int head, count;
        private long nextId;
        private bool stopping, disposed;
        private Exception failure;
        /// <summary>대기열 작업이 실행되었거나 실패할 때마다 워커 스레드에서 발생합니다.</summary>
        /// <remarks>성공 통지 중 처리기가 예외를 던지면 워커가 중지되고 <see cref="Failure"/>가 설정됩니다.</remarks>
        public event MqttWorkCompleted Completed;
        /// <summary>워커를 중지시킨 마지막 연결 오류 또는 콜백 예외입니다. 실패 전에는 <see langword="null"/>입니다.</summary>
        public Exception Failure { get { lock (gate) return failure; } }
        /// <summary>워커를 만들고 즉시 전용 스레드를 시작합니다.</summary>
        /// <param name="connection">이미 <see cref="MqttConnection.Connect(MqttConnectionOptions)"/>로 연결된 연결입니다.</param>
        /// <param name="capacity">대기열에 동시에 넣을 수 있는 작업 수(1..256)입니다.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/>이 <see langword="null"/>인 경우.</exception>
        /// <exception cref="InvalidOperationException">연결되지 않은 연결을 넘긴 경우.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/>가 범위를 벗어난 경우.</exception>
        public MqttWorker(MqttConnection connection, int capacity = 16)
        {
            this.connection = connection ?? throw new ArgumentNullException("connection");
            if (!connection.Connected) throw new InvalidOperationException("Connect before starting the worker.");
            if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException("capacity");
            queue = new MqttWork[capacity]; ids = new long[capacity];
            thread = new Thread(Run); thread.Start();
        }
        /// <summary>MQTT 통신을 기다리지 않고 작업을 대기열에 넣습니다. 어느 스레드에서나 호출할 수 있습니다.</summary>
        /// <remarks>작업이 캡처한 가변 데이터는 실행될 때까지 바뀌면 안 됩니다. 페이로드 복사가 필요하면 <see cref="Publish"/>를 사용하십시오.</remarks>
        /// <param name="operation">워커 스레드에서 실행할 작업입니다.</param>
        /// <returns><see cref="Completed"/>에서 결과를 구분하는 데 쓰는 1부터 증가하는 작업 ID입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="operation"/>이 <see langword="null"/>인 경우.</exception>
        /// <exception cref="InvalidOperationException">대기열이 가득 찼거나 워커가 중지·해제·실패 상태인 경우.</exception>
        public long Enqueue(MqttWork operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            lock (gate)
            {
                if (stopping || disposed || failure != null) throw new InvalidOperationException("MQTT worker stopped or faulted.");
                if (count == queue.Length) throw new InvalidOperationException("MQTT work queue full.");
                long id = ++nextId;
                int tail = (head + count) % queue.Length;
                queue[tail] = operation; ids[tail] = id; count++;
                return id;
            }
        }
        /// <summary>페이로드를 복사한 뒤 <see cref="MqttConnection.Publish(string, byte[], MqttQos, bool)"/> 작업을 대기열에 넣습니다.</summary>
        /// <remarks>완료 통지는 대기열 진입이 아니라 QoS 핸드셰이크가 끝난 시점에 발생합니다. 토픽 검증은 실행 시점에 이루어집니다.</remarks>
        /// <param name="topic">발행할 토픽 이름입니다.</param>
        /// <param name="payload">보낼 페이로드입니다. 호출 즉시 복사되므로 이후 수정해도 됩니다.</param>
        /// <param name="qos">전달 보장 수준입니다.</param>
        /// <param name="retain">브로커에 보관 메시지로 남길지 여부입니다.</param>
        /// <returns>대기열 작업 ID입니다.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/>가 <see langword="null"/>인 경우.</exception>
        /// <exception cref="InvalidOperationException">대기열이 가득 찼거나 워커가 중지·실패 상태인 경우.</exception>
        public long Publish(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            var copy = new byte[payload.Length]; Array.Copy(payload, copy, copy.Length);
            return Enqueue(client => client.Publish(topic, copy, qos, retain));
        }
        private void Run()
        {
            long executing = 0;
            try
            {
                while (true)
                {
                    MqttWork action = null;
                    lock (gate)
                    {
                        if (count != 0)
                        {
                            action = queue[head]; executing = ids[head]; queue[head] = null;
                            head = (head + 1) % queue.Length; count--;
                        }
                        else if (stopping) break;
                    }
                    if (action != null)
                    {
                        action(connection);
                        var completed = Completed;
                        if (completed != null) completed(executing, null);
                        executing = 0;
                    }
                    if (connection.Connected) connection.Poll(10);
                    else
                    {
                        lock (gate)
                        {
                            if (count != 0) throw new InvalidOperationException("Connection closed with queued MQTT work.");
                            stopping = true;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                lock (gate) { failure = error; stopping = true; }
                NotifyFailure(executing, error);
                while (true)
                {
                    long id;
                    lock (gate)
                    {
                        if (count == 0) break;
                        id = ids[head]; queue[head] = null;
                        head = (head + 1) % queue.Length; count--;
                    }
                    NotifyFailure(id, error);
                }
            }
        }
        private void NotifyFailure(long id, Exception error)
        {
            var completed = Completed;
            if (completed == null) return;
            try { completed(id, error); }
            catch (Exception callbackError) { lock (gate) failure = callbackError; }
        }
        /// <summary>새 작업 접수를 멈추고, 이미 받은 작업을 모두 실행한 뒤 워커 스레드 종료를 최대 15초 기다립니다.</summary>
        /// <remarks>연결을 끊거나 채널을 닫지 않습니다. 시간 초과로 예외가 나면 호출자가 전송 계층을 닫아 블로킹을 풀고 다시 호출해야 합니다.</remarks>
        /// <exception cref="InvalidOperationException">워커 스레드(작업·콜백) 안에서 호출한 경우.</exception>
        /// <exception cref="TimeoutException">15초 안에 워커 스레드가 끝나지 않은 경우.</exception>
        public void Dispose()
        {
            if (Thread.CurrentThread == thread) throw new InvalidOperationException("Cannot join MQTT worker from its callback.");
            lock (gate) { if (disposed) return; stopping = true; }
            if (!thread.Join(15000)) throw new TimeoutException("MQTT worker join deadline.");
            lock (gate) disposed = true;
        }
    }
}

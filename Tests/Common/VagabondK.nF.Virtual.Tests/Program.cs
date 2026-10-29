using System;
using System.Diagnostics;

namespace VagabondK.nF.Virtual.Tests
{
    internal delegate void Act();

    /// <summary>PC .NET만으로 확인할 수 없는 정수 승격·실수 비트 표현·스레드 동작을 nanoCLR 가상 장치에서 검증합니다.</summary>
    /// <remarks>메모리 채널을 사용하므로 실제 UART·네트워크 드라이버나 보드의 타이밍 검증을 대신하지 않습니다.</remarks>
    internal static class Program
    {
        private static int checks;
        private static int failures;

        private static void Main()
        {
            // 한 프로토콜의 실패로 나머지 시험을 건너뛰지 않고 최종 VIRTUAL PASS/FAIL에 합산합니다.
            Run("Modbus", ModbusVirtual.Run);
            Run("DNP3", Dnp3Virtual.Run);
            Run("BACnet", DeviceVirtual.Bacnet);
            Run("SNMP", DeviceVirtual.Snmp);
            Run("MMS", MmsVirtual.Run);
            Run("MQTT", MqttVirtual.Run);
            if (failures == 0)
                Debug.WriteLine("VIRTUAL PASS: " + checks + " checks");
            else
                Debug.WriteLine("VIRTUAL FAIL: " + failures + " of " + checks + " checks");
        }

        private static void Run(string name, Act body)
        {
            int before = failures;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failures++;
                Debug.WriteLine("FAIL " + name + ": unexpected " + ex.GetType().Name + " " + ex.Message);
            }
            Debug.WriteLine((failures == before ? "ok   " : "FAIL ") + name);
        }

        internal static void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
            {
                failures++;
                Debug.WriteLine("FAIL: " + message);
            }
        }

        internal static void Throws(Act body, string message)
        {
            bool thrown = false;
            try
            {
                body();
            }
            catch (Exception)
            {
                thrown = true;
            }
            Check(thrown, message);
        }

        internal static void Rejects(Act body, Type expected, string message)
        {
            bool rejected = false;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                if (ex.GetType() != expected)
                    throw;
                rejected = true;
            }
            Check(rejected, message);
        }

        internal static byte[] Copy(byte[] bytes, int length)
        {
            var result = new byte[length];
            Array.Copy(bytes, result, bytes.Length < length ? bytes.Length : length);
            return result;
        }
    }
}

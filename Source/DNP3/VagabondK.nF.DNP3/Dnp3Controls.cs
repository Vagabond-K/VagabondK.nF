using System;
using VagabondK.nF.Internal;

namespace VagabondK.nF.DNP3
{
    /// <summary>DNP3 제어 명령의 검증 또는 실행 결과입니다.</summary>
    public enum Dnp3CommandStatus : byte
    {
        /// <summary>명령이 성공했습니다.</summary>
        Success = 0,

        /// <summary>명령 처리 시간이 만료되었습니다.</summary>
        Timeout = 1,

        /// <summary>일치하는 유효한 Select가 없습니다.</summary>
        NoSelect = 2,

        /// <summary>명령 형식이 잘못되었습니다.</summary>
        FormatError = 3,

        /// <summary>지원하지 않는 명령입니다.</summary>
        NotSupported = 4,

        /// <summary>요청한 제어가 이미 활성 상태입니다.</summary>
        AlreadyActive = 5,

        /// <summary>하드웨어 오류가 발생했습니다.</summary>
        HardwareError = 6,

        /// <summary>로컬 제어 모드로 인해 실행할 수 없습니다.</summary>
        Local = 7,

        /// <summary>허용된 작업 수를 초과했습니다.</summary>
        TooManyOperations = 8,

        /// <summary>실행 권한이 없습니다.</summary>
        NotAuthorized = 9,

        /// <summary>자동화 억제 상태로 인해 실행할 수 없습니다.</summary>
        AutomationInhibit = 10,

        /// <summary>처리 자원 제한으로 실행할 수 없습니다.</summary>
        ProcessingLimited = 11,

        /// <summary>명령 값이 허용 범위를 벗어납니다.</summary>
        OutOfRange = 12,

        /// <summary>하위 장치가 로컬 제어 모드입니다.</summary>
        DownstreamLocal = 13,

        /// <summary>요청한 작업이 이미 완료되었습니다.</summary>
        AlreadyComplete = 14,

        /// <summary>제어가 차단되었습니다.</summary>
        Blocked = 15,

        /// <summary>명령이 취소되었습니다.</summary>
        Cancelled = 16,

        /// <summary>다른 Master의 제어로 인해 차단되었습니다.</summary>
        BlockedOtherMaster = 17,

        /// <summary>하위 장치에서 오류가 발생했습니다.</summary>
        DownstreamFailure = 18,

        /// <summary>해당 제어 작업에 참여하지 않습니다.</summary>
        NonParticipating = 126,

        /// <summary>정의되지 않은 실패입니다.</summary>
        Undefined = 127
    }

    /// <summary>CROB 또는 Analog Output 명령의 값과 대상 인덱스를 나타냅니다.</summary>
    /// <remarks><see cref="Relay"/> 또는 <see cref="Analog"/>로 생성합니다. 기본값 구조체는 전송 가능한 명령이 아닙니다.</remarks>
    public struct Dnp3Command
    {
        /// <summary>대상 Point의 16비트 인덱스입니다.</summary>
        public ushort Index { get; private set; }

        /// <summary>Object Group입니다. CROB는 12, Analog Output은 41입니다.</summary>
        public byte Group { get; private set; }

        /// <summary>명령 값의 Object Variation입니다.</summary>
        public byte Variation { get; private set; }

        /// <summary>CROB의 동작 및 Trip/Close 비트가 포함된 Control Code입니다.</summary>
        public byte ControlCode { get; private set; }

        /// <summary>CROB 동작 반복 횟수입니다.</summary>
        public byte Count { get; private set; }

        /// <summary>CROB의 On 지속 시간입니다. 단위는 밀리초입니다.</summary>
        public uint OnMilliseconds { get; private set; }

        /// <summary>CROB의 Off 지속 시간입니다. 단위는 밀리초입니다.</summary>
        public uint OffMilliseconds { get; private set; }

        /// <summary>Analog Output 명령 값입니다.</summary>
        public double AnalogValue { get; private set; }

        /// <summary>Group 12 Variation 1 CROB 명령을 생성합니다.</summary>
        /// <param name="index">대상 Point 인덱스입니다.</param>
        /// <param name="code">하위 4비트가 Pulse On/Off 또는 Latch On/Off인 1~4이고,
        /// Queue/Clear 비트가 없으며 Trip/Close 두 비트가 동시에 설정되지 않은 Control Code입니다.</param>
        /// <param name="count">1~255 범위의 동작 반복 횟수입니다.</param>
        /// <param name="onMilliseconds">On 지속 시간입니다. 단위는 밀리초입니다.</param>
        /// <param name="offMilliseconds">Off 지속 시간입니다. 단위는 밀리초입니다.</param>
        /// <returns>지정된 CROB 명령입니다.</returns>
        /// <exception cref="ArgumentException">Control Code 또는 반복 횟수가 지원 범위를 벗어납니다.</exception>
        public static Dnp3Command Relay(ushort index, byte code, byte count = 1, uint onMilliseconds = 0, uint offMilliseconds = 0)
        {
            if ((code & 15) < 1 || (code & 15) > 4 || (code & 0x30) != 0 || (code & 0xc0) == 0xc0 || count == 0)
                throw new ArgumentException("CROB supports pulse-on/off or latch-on/off; queue/clear are unsupported.");
            return new Dnp3Command
            {
                Index = index,
                Group = 12,
                Variation = 1,
                ControlCode = code,
                Count = count,
                OnMilliseconds = onMilliseconds,
                OffMilliseconds = offMilliseconds
            };
        }

        /// <summary>Group 41 Analog Output 명령을 생성합니다.</summary>
        /// <param name="index">대상 Point 인덱스입니다.</param>
        /// <param name="value">유한한 출력 값입니다. Variation 1/2에서는 각각 Int32/Int16 정수 범위여야 합니다.</param>
        /// <param name="variation">1은 Int32, 2는 Int16, 3은 Single, 4는 Double입니다.</param>
        /// <returns>지정된 Analog Output 명령입니다. Variation 3으로 전송할 때는 값이 Single 정밀도로 변환됩니다.</returns>
        /// <exception cref="ArgumentException">Variation이 1~4가 아니거나 값이 해당 형식의 범위를 벗어납니다.</exception>
        public static Dnp3Command Analog(ushort index, double value, byte variation = 3)
        {
            if (variation < 1 || variation > 4 || double.IsNaN(value) || double.IsInfinity(value) ||
                variation == 1 && (value < int.MinValue || value > int.MaxValue || value != (int)value) ||
                variation == 2 && (value < short.MinValue || value > short.MaxValue || value != (short)value) ||
                variation == 3 && (value < -float.MaxValue || value > float.MaxValue))
                throw new ArgumentException("Analog command range/type.");
            return new Dnp3Command { Index = index, Group = 41, Variation = variation, AnalogValue = value };
        }
    }

    /// <summary>Outstation의 명령 검증 및 실제 제어 실행을 담당합니다.</summary>
    /// <remarks>Outstation 처리 호출 중 동기적으로 실행됩니다. Select 검증은 실제 출력을 변경하지 않아야 합니다.</remarks>
    public interface IDnp3ControlHandler
    {
        /// <summary>Select 또는 Operate 전에 명령을 검증합니다.</summary>
        /// <param name="command">검증할 명령입니다.</param>
        /// <returns>명령을 허용하면 Success, 그렇지 않으면 거부 사유입니다.</returns>
        Dnp3CommandStatus Validate(Dnp3Command command);

        /// <summary>검증된 명령을 실행합니다.</summary>
        /// <param name="command">실행할 명령입니다.</param>
        /// <param name="selected">일치하는 Select가 제한 시간 내 확인된 Operate이면 true이고, Direct Operate이면 false입니다.</param>
        /// <returns>실제 제어 실행 결과입니다.</returns>
        Dnp3CommandStatus Operate(Dnp3Command command, bool selected);
    }

    /// <summary>Outstation이 수신한 UTC 시각을 적용하는 처리기입니다.</summary>
    /// <remarks>Outstation의 Process 호출 안에서 동기적으로 실행됩니다. 처리기에서 발생한 예외는 호출자에게 전파될 수 있습니다.</remarks>
    public interface IDnp3TimeHandler
    {
        /// <summary>수신한 DNP3 UTC 시각을 적용합니다.</summary>
        /// <param name="milliseconds">1970-01-01 UTC부터의 경과 밀리초입니다. 0~0xFFFFFFFFFFFF 범위입니다.</param>
        void SetUtcMilliseconds(long milliseconds);
    }

    internal static class Dnp3Controls
    {
        internal static byte[] Encode(Dnp3Command command)
        {
            if (command.Group != 12 && command.Group != 41)
                throw new ArgumentException("Uninitialized DNP3 command.");
            var b = new byte[command.Group == 12 ? 18 : 8 + (command.Variation == 2 ? 2 : command.Variation == 4 ? 8 : 4)];
            b[0] = command.Group;
            b[1] = command.Variation;
            b[2] = 0x28;
            b[3] = 1;
            Dnp3Channel.PutWord(b, 5, command.Index);
            int p = 7;
            if (command.Group == 12)
            {
                b[p++] = command.ControlCode;
                b[p++] = command.Count;
                Dnp3Objects.Put(b, ref p, command.OnMilliseconds, 4);
                Dnp3Objects.Put(b, ref p, command.OffMilliseconds, 4);
            }
            else
            {
                int width = command.Variation == 2 ? 2 : command.Variation == 4 ? 8 : 4;
                ulong raw = command.Variation <= 2 ? unchecked((uint)(int)command.AnalogValue) :
                    command.Variation == 3 ? RealBits.Encode((float)command.AnalogValue) :
                    unchecked((ulong)BitConverter.DoubleToInt64Bits(command.AnalogValue));
                Dnp3Objects.Put(b, ref p, raw, width);
            }
            return b;
        }

        internal static Dnp3Command Parse(byte[] bytes, ref int p, ushort index, int group, int variation)
        {
            if (group == 12 && variation == 1)
            {
                Dnp3Channel.Require(bytes.Length - p >= 11 && bytes[p + 10] == 0);
                byte code = bytes[p++], count = bytes[p++];
                uint on = (uint)Dnp3Objects.Read(bytes, p, 4);
                p += 4;
                uint off = (uint)Dnp3Objects.Read(bytes, p, 4);
                p += 5;
                try
                {
                    return Dnp3Command.Relay(index, code, count, on, off);
                }
                catch (ArgumentException)
                {
                    throw new Dnp3ProtocolException();
                }
            }
            Dnp3Channel.Require(group == 41 && variation >= 1 && variation <= 4);
            int width = variation == 2 ? 2 : variation == 4 ? 8 : 4;
            Dnp3Channel.Require(bytes.Length - p >= width + 1 && bytes[p + width] == 0);
            ulong raw = Dnp3Objects.Read(bytes, p, width);
            p += width + 1;
            double value = variation == 1 ? Dnp3Objects.Signed32(raw) : variation == 2 ? unchecked((short)raw) :
                variation == 3 ? RealBits.Decode((uint)raw) :
                BitConverter.Int64BitsToDouble(unchecked((long)raw));
            try
            {
                return Dnp3Command.Analog(index, value, (byte)variation);
            }
            catch (ArgumentException)
            {
                throw new Dnp3ProtocolException();
            }
        }
    }
}

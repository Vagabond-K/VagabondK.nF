"""Independent, pinned pymodbus peer for the isolated mapping evaluation MCU."""
import argparse
import asyncio
import json
import logging
from pathlib import Path
import socket
import struct
import time

from pymodbus.client import ModbusTcpClient
from pymodbus.constants import ExcCodes
from pymodbus.datastore import ModbusDeviceContext, ModbusSequentialDataBlock, ModbusServerContext
from pymodbus.exceptions import ModbusException
from pymodbus.server import ModbusTcpServer


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def words(fmt, value, low_word=False, low_byte=False):
    raw = struct.pack(">" + fmt, value)
    chunks = [raw[i:i + 2] for i in range(0, len(raw), 2)]
    if low_word:
        chunks.reverse()
    if low_byte:
        chunks = [chunk[::-1] for chunk in chunks]
    return [int.from_bytes(chunk, "big") for chunk in chunks]


SCALARS = [
    (200, words("h", -12345)),
    (201, words("H", 0xFEDC, low_byte=True)),
    (202, words("i", -(2**31))),
    (204, words("I", 0x89ABCDEF, low_word=True)),
    (206, words("f", -12.5, low_byte=True)),
    (208, words("q", -(2**63) + 123, low_word=True)),
    (212, words("Q", 0xFEDCBA9876543210, low_byte=True)),
    (216, words("d", -12345.125, low_word=True, low_byte=True)),
]
EXPECTED = [word for _, data in SCALARS for word in data]


def ok(response):
    require(not response.isError(), "Unexpected Modbus error: " + str(response))
    return response


def rejected(response):
    require(response.isError() and response.exception_code == 2, "Expected IllegalDataAddress: " + str(response))


def read_holding(client, address, count):
    return ok(client.read_holding_registers(address, count=count, device_id=1)).registers


def run_slave(args):
    deadline = time.monotonic() + 240
    report = None
    client = None
    while time.monotonic() < deadline:
        candidate = ModbusTcpClient(args.device, port=args.port, timeout=args.timeout, retries=0,
                                   trace_packet=trace_packet(args.output.with_suffix(".wire.jsonl")))
        try:
            if candidate.connect():
                response = candidate.read_input_registers(1000, count=32, device_id=1)
                if not response.isError():
                    report = response.registers
                    client = candidate
                    break
        except (OSError, ModbusException) as error:
            print("Waiting for MCU:", error, flush=True)
        candidate.close()
        time.sleep(2)
    require(report is not None, "MCU did not publish benchmark readiness.")
    try:
        require(report[:4] == [0x4D50, 1, args.points, 1000] and report[15] == 0x600D, "Invalid MCU benchmark report.")
        u32 = lambda offset: (report[offset] << 16) | report[offset + 1]
        metrics = {
            "points": args.points, "iterations": report[3],
            "batches": report[28],
            "free_before_model": u32(4), "free_after_model_store": u32(6),
            "free_after_warmup": u32(8), "free_after_iterations": u32(10),
            "elapsed_ms": u32(12),
            "free_after_batches": [u32(16), u32(18), u32(20)],
            "debugger_attached": bool(report[31]),
        }
        metrics["retained_growth_bytes"] = metrics["free_after_warmup"] - metrics["free_after_iterations"]
        metrics["tail_growth_bytes"] = u32(18) - u32(20)
        args.output.write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        print("SLAVE METRICS:", json.dumps(metrics), flush=True)
        require(not args.require_no_debugger or not metrics["debugger_attached"], "Unexpected attached debugger.")
        require(metrics["batches"] == 3, "Incomplete benchmark batches.")
        require(metrics["retained_growth_bytes"] <= 256 and metrics["tail_growth_bytes"] <= 256,
                "Unexpected retained heap growth >256 bytes; inspect startup/network/GC and recorded samples.")
        for address, data in SCALARS:
            ok(client.write_registers(address, data, device_id=1))
            require(read_holding(client, address, len(data)) == data, "Scalar wire word order " + str(address))
        ok(client.write_coil(10, True, device_id=1))
        require(ok(client.read_coils(10, count=1, device_id=1)).bits[0], "Coil mapping.")
        require(ok(client.read_discrete_inputs(20, count=1, device_id=1)).bits[0], "Discrete Input mapping.")
        require(ok(client.read_input_registers(100, count=1, device_id=1)).registers == [0xFF85], "Signed Input Register.")
        # MCU는 변환 왕복뿐 아니라 실제 형식별 멤버 값도 검사합니다.
        ok(client.write_register(1100, 0xCAFE, device_id=1))
        require(ok(client.read_input_registers(1014, count=1, device_id=1)).registers == [0x600D], "MCU typed member oracle.")
        require(read_holding(client, 200, 20) == EXPECTED, "Combined scalar request.")
        require(read_holding(client, 203, 1) == EXPECTED[3:4], "Partial scalar read.")
        rejected(client.write_register(202, 0, device_id=1))
        rejected(client.write_registers(200, [0] * 3, device_id=1))
        rejected(client.write_registers(200, [0] * 21, device_id=1))
        rejected(client.write_registers(219, [0, 0], device_id=1))
        rejected(client.read_holding_registers(199, count=2, device_id=1))
        rejected(client.read_holding_registers(65535, count=2, device_id=1))
        rejected(client.write_coils(10, [False, False], device_id=1))
        require(read_holding(client, 200, 20) == EXPECTED, "Rejected writes must preserve all typed values.")
        require(read_holding(client, 220, 1) == [0x55AA], "Read-only mapping unchanged.")
        require(ok(client.read_coils(10, count=1, device_id=1)).bits[0], "Rejected coil range must preserve first value.")
        changed = [
            words("h", 1234), words("H", 0x1234, low_byte=True),
            words("i", 0x12345678), words("I", 0x76543210, low_word=True),
            words("f", 123.25, low_byte=True), words("q", 0x1234567890ABCDEF, low_word=True),
            words("Q", 0x0123456789ABCDEF, low_byte=True),
            words("d", 1234567.25, low_word=True, low_byte=True),
        ]
        for slot in range(9):
            before = read_holding(client, 200, 21)
            ok(client.write_register(1102, slot, device_id=1))
            expected = before[:]
            if slot < 8:
                address, original = SCALARS[slot]
                expected[address - 200:address - 200 + len(original)] = changed[slot]
            require(read_holding(client, 200, 21) == expected, "Local model change/isolation slot " + str(slot))
            require(ok(client.read_coils(10, count=1, device_id=1)).bits[0] == (slot != 8),
                    "Local bit change isolated from registers.")
            if slot < 8:
                address, original = SCALARS[slot]
                ok(client.write_registers(address, original, device_id=1))
            else:
                ok(client.write_coil(10, True, device_id=1))
            require(read_holding(client, 200, 20) == EXPECTED, "External write restores actual local model.")
        print("MODBUS ROBUST PASS local-model/isolation/all-scalars/word-byte-orders/failed-write-preservation", flush=True)
        boundaries = [
            (200, "h", [-32768, -1, 0, 32767], False, False),
            (201, "H", [0, 1, 65535], False, True),
            (202, "i", [-2147483648, -1, 0, 2147483647], False, False),
            (204, "I", [0, 1, 0xFFFFFFFF], True, False),
            (206, "I", [0, 0x80000000, 1, 0x80000001, 0x7F7FFFFF, 0x7F800000, 0xFF800000, 0x7FC00000], False, True),
            (208, "q", [-9223372036854775808, -1, 0, 9223372036854775807], True, False),
            (212, "Q", [0, 1, 0xFFFFFFFFFFFFFFFF], False, True),
            (216, "Q", [0, 0x8000000000000000, 1, 0x8000000000000001,
                         0x7FEFFFFFFFFFFFFF, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000], True, True),
        ]
        boundary_count = 0
        for address, fmt, cases, low_word, low_byte in boundaries:
            baseline = read_holding(client, 200, 21)
            for value in cases:
                data = words(fmt, value, low_word=low_word, low_byte=low_byte)
                ok(client.write_registers(address, data, device_id=1))
                expected = baseline[:]
                expected[address - 200:address - 200 + len(data)] = data
                require(read_holding(client, 200, 21) == expected,
                        "Exact scalar boundary bits and neighbor preservation at " + str(address))
                boundary_count += 1
            original = next(data for scalar_address, data in SCALARS if scalar_address == address)
            ok(client.write_registers(address, original, device_id=1))
            require(read_holding(client, 200, 21) == baseline, "Boundary restore preserves complete model.")
        print("MODBUS BOUNDARY PASS cases=" + str(boundary_count) +
              " signed/unsigned/negative-zero/subnormal/infinity/quiet-NaN exact-bits", flush=True)
        for start in range(0, args.points, 123):
            count = min(123, args.points - start)
            data = [((start + i) * 257) & 0xFFFF for i in range(count)]
            ok(client.write_registers(start, data, device_id=1))
        for start in range(0, args.points, 125):
            count = min(125, args.points - start)
            require(read_holding(client, start, count) == [((start + i) * 257) & 0xFFFF for i in range(count)], "Scale wire mapping.")
        rejected(client.write_register(args.points, 0, device_id=1))
        for _ in range(args.iterations):
            require(read_holding(client, 200, 20) == EXPECTED, "Repeated scalar read.")
        if args.stability_seconds:
            require(not metrics["debugger_attached"], "Stability run must not have an attached debugger.")
            metrics["stability"] = run_stability(args, client)
        args.output.write_text(json.dumps(metrics, indent=2), encoding="utf-8")
        print("SLAVE PASS:", json.dumps(metrics), flush=True)
    finally:
        client.close()


def run_stability(args, initial):
    started = time.monotonic()
    latencies = []
    samples = []
    cycles = 0
    wire = trace_packet(args.output.with_suffix(".wire.jsonl"))

    def check_cycle(client):
        for _ in range(10):
            start = time.monotonic()
            require(read_holding(client, 200, 20) == EXPECTED, "Stability scalar read.")
            rejected(client.write_register(202, 0, device_id=1))
            require(read_holding(client, 200, 20) == EXPECTED, "Stability rejected write changed model.")
            latencies.append(time.monotonic() - start)
        ok(client.write_register(1101, 0xCAFE, device_id=1))
        report = ok(client.read_input_registers(1000, count=32, device_id=1)).registers
        require(report[31] == 0 and report[15] == 0x600D, "Debugger attached or benchmark marker lost.")
        samples.append({"elapsed_seconds": time.monotonic() - started, "connections": report[22],
                        "responses": report[23], "exception_responses": report[24],
                        "timeouts": report[25], "closures": report[26], "socket_errors": report[27],
                        "free_heap": (report[29] << 16) | report[30]})
        args.output.with_suffix(".stability.json").write_text(
            json.dumps({"cycles": cycles + 1, "samples": samples,
                        "max_read_reject_read_ms": max(latencies) * 1000}, indent=2), encoding="utf-8")

    check_cycle(initial)
    cycles += 1
    initial.close()
    time.sleep(4)
    with socket.create_connection((args.device, args.port), timeout=20) as broken:
        broken.sendall(bytes.fromhex("00010001000201"))
        require(broken.recv(1) == b"", "Invalid MBAP header connection was not closed.")
    time.sleep(4)
    with socket.create_connection((args.device, args.port), timeout=20) as partial:
        partial.sendall(b"\x00\x02\x00")
        require(partial.recv(1) == b"", "Incomplete header connection was not closed after server timeout.")
    # GC 후 잔류 힙의 기준을 측정하기 전에 연결 종료와 두 오류 경로를 실행합니다.
    for _ in range(2):
        time.sleep(4)
        with ModbusTcpClient(args.device, port=args.port, timeout=args.timeout, retries=0, trace_packet=wire) as client:
            check_cycle(client)
        cycles += 1
    baseline = len(samples) - 1
    measured_start = time.monotonic()
    deadline = measured_start + args.stability_seconds
    while time.monotonic() < deadline:
        # 정상 EOF 시 nanoCLR의 예외 진단 시간은 요청 지연 측정에서 제외합니다.
        time.sleep(4)
        with ModbusTcpClient(args.device, port=args.port, timeout=args.timeout, retries=0, trace_packet=wire) as client:
            check_cycle(client)
        cycles += 1
    require(len(samples) - baseline - 1 >= 2, "Stability measurement must cover at least two reconnects after warm-up.")
    require(samples[-1]["timeouts"] > samples[0]["timeouts"], "Missing incomplete-frame timeout recovery.")
    growth = samples[baseline]["free_heap"] - samples[-1]["free_heap"]
    maximum_growth = samples[baseline]["free_heap"] - min(s["free_heap"] for s in samples[baseline:])
    result = {"cycles": cycles, "elapsed_seconds": time.monotonic() - started,
              "measured_seconds": time.monotonic() - measured_start, "baseline_sample": baseline,
              "startup_growth_bytes": samples[0]["free_heap"] - samples[baseline]["free_heap"],
              "retained_growth_bytes": growth, "max_retained_growth_bytes": maximum_growth,
              "max_read_reject_read_ms": max(latencies) * 1000, "last_counters": samples[-1]}
    args.output.with_suffix(".stability.json").write_text(
        json.dumps({"result": result, "samples": samples}, indent=2), encoding="utf-8")
    require(maximum_growth <= 256, "Stability retained heap growth exceeds 256 bytes: " + str(maximum_growth))
    return result


def trace_packet(path):
    def record(sending, data):
        with path.open("a", encoding="utf-8") as output:
            output.write(json.dumps({"utc_ns": time.time_ns(), "monotonic_ns": time.monotonic_ns(),
                                     "sending": sending, "hex": data.hex()}) + "\n")
        return data
    return record


class PeerContext(ModbusDeviceContext):
    def __init__(self):
        super().__init__(
            di=ModbusSequentialDataBlock(0, [False] * 1102),
            co=ModbusSequentialDataBlock(0, [False] * 1102),
            ir=ModbusSequentialDataBlock(0, [0] * 1102),
            hr=ModbusSequentialDataBlock(0, [0] * 1102))
        self.setValues(3, 200, EXPECTED)
        self.setValues(1, 10, [True])
        self.setValues(2, 20, [True])
        self.setValues(4, 100, [0xFF85])
        self.stage = 0
        self.wire_fault = 0
        self.faults = []
        self.reads = []
        self.writes = []
        self.complete = asyncio.Event()
        self.server = None

    async def async_getValues(self, func_code, address, count=1):
        # 쓰기 함수 구현도 응답을 만들기 위해 값을 다시 읽습니다.
        if func_code in (1, 2, 3, 4):
            if self.stage in (1, 2, 3, 4, 5):
                stage = self.stage
                self.stage = 0
                self.faults.append(stage)
                if stage == 1:
                    return ExcCodes.ILLEGAL_ADDRESS
                if stage == 2:
                    await asyncio.sleep(0.75)
                if stage == 3:
                    self.server.handlers[-1].close()
                if stage in (4, 5):
                    self.wire_fault = stage
            else:
                self.reads.append((func_code, address, count))
                require(count <= (2 if func_code in (1, 2) else 4), "MCU exceeded configured read cap.")
        return self.getValues(func_code, address, count)

    async def async_setValues(self, func_code, address, values):
        if address == 900:
            require(len(values) == 1, "Control marker width.")
            if values[0] == 0x600D:
                require(self.faults == [1, 2, 3, 4, 5], "Missing error/recovery phases.")
                require(self.getValues(3, 200, 20) == EXPECTED, "MCU generated writes changed raw scalar values.")
                require(self.getValues(1, 10, 1) == [True], "MCU generated coil write.")
                expected_writes = [(6, 200, 1), (6, 201, 1), (16, 202, 2), (16, 204, 2),
                                   (16, 206, 2), (16, 208, 4), (16, 212, 4), (16, 216, 4), (5, 10, 1)]
                require(self.writes == expected_writes, "Unexpected generated write requests: " + str(self.writes))
                expected_reads = [(1, 10, 1), (2, 20, 1), (3, 200, 4), (3, 204, 4),
                                  (3, 208, 4), (3, 212, 4), (3, 216, 4), (4, 100, 1)]
                require(self.reads == expected_reads * 101 + [(1, 10, 1), (3, 200, 1)] + expected_reads,
                        "Unexpected normal/group read grouping/recovery.")
                self.complete.set()
            else:
                require(values[0] == len(self.faults) + 1, "Out-of-order fault marker.")
                self.stage = values[0]
        else:
            self.writes.append((func_code, address, len(values)))
        return self.setValues(func_code, address, values)


class PeerServer(ModbusTcpServer):
    def __init__(self, *args, **kwargs):
        self.handlers = []
        super().__init__(*args, **kwargs)

    def callback_new_connection(self):
        handler = super().callback_new_connection()
        self.handlers.append(handler)
        return handler


async def run_master(args):
    context = PeerContext()
    def trace_pdu(sending, pdu):
        if sending and context.wire_fault and pdu.function_code in (1, 2, 3, 4):
            if context.wire_fault == 4:
                pdu.transaction_id = (pdu.transaction_id + 7) & 0xFFFF
            else:
                pdu.dev_id = 2
            context.wire_fault = 0
        return pdu
    server = PeerServer(ModbusServerContext(devices={1: context}, single=False), address=(args.bind, args.port),
                        trace_pdu=trace_pdu)
    context.server = server
    try:
        await server.serve_forever(background=True)
        args.ready.write_text("READY", encoding="ascii")
        print("MASTER PEER READY:", args.bind, args.port, flush=True)
        if args.mode == "selftest":
            await asyncio.to_thread(selftest_client, args)
        await asyncio.wait_for(context.complete.wait(), 240)
        # 종료 전에 완료 표식에 대한 응답이 전달될 시간을 줍니다.
        await asyncio.sleep(0.2)
        result = {"normal_reads": len(context.reads), "writes": len(context.writes), "faults": context.faults}
        args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
        print("MASTER PASS:", json.dumps(result), flush=True)
    finally:
        await server.shutdown()


def selftest_client(args):
    def read_model(client):
        ok(client.read_coils(10, count=1, device_id=1))
        ok(client.read_discrete_inputs(20, count=1, device_id=1))
        for address in (200, 204, 208, 212, 216):
            require(read_holding(client, address, 4) == EXPECTED[address - 200:address - 196], "Peer raw data.")
        ok(client.read_input_registers(100, count=1, device_id=1))

    with ModbusTcpClient(args.bind, port=args.port, timeout=1, retries=0) as client:
        read_model(client)
        for address, data in SCALARS:
            if len(data) == 1:
                ok(client.write_register(address, data[0], device_id=1))
            else:
                ok(client.write_registers(address, data, device_id=1))
        ok(client.write_coil(10, True, device_id=1))
        for _ in range(100):
            read_model(client)
        ok(client.read_coils(10, count=1, device_id=1))
        require(read_holding(client, 200, 1) == EXPECTED[:1], "Peer settings group.")
        ok(client.write_register(900, 1, device_id=1))
        rejected(client.read_coils(10, count=1, device_id=1))
    for stage in (2, 3, 4, 5):
        with ModbusTcpClient(args.bind, port=args.port, timeout=0.25, retries=0) as client:
            ok(client.write_register(900, stage, device_id=1))
            failed = False
            try:
                response = client.read_coils(10, count=1, device_id=1)
                failed = response.isError()
            except ModbusException:
                failed = True
            require(failed, "Peer did not inject expected transport fault.")
    with ModbusTcpClient(args.bind, port=args.port, timeout=1, retries=0) as client:
        read_model(client)
        ok(client.write_register(900, 0x600D, device_id=1))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["slave", "master", "selftest"])
    parser.add_argument("--device", default="192.168.0.187")
    parser.add_argument("--bind", default="192.168.0.210")
    parser.add_argument("--port", type=int, default=15020)
    parser.add_argument("--points", type=int, choices=[8, 32, 128], default=8)
    parser.add_argument("--iterations", type=int, default=64)
    parser.add_argument("--timeout", type=float, default=2)
    parser.add_argument("--stability-seconds", type=int, default=0)
    parser.add_argument("--require-no-debugger", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ready", type=Path)
    args = parser.parse_args()
    require(args.timeout > 0 and args.iterations > 0 and args.stability_seconds >= 0, "Invalid test limits.")
    require(args.stability_seconds == 0 or args.stability_seconds >= 60, "Stability run requires at least 60 seconds.")
    logging.basicConfig(level=logging.ERROR)
    if args.mode == "slave":
        run_slave(args)
    else:
        require(args.ready is not None, "Master peer needs a readiness path.")
        asyncio.run(run_master(args))


if __name__ == "__main__":
    main()

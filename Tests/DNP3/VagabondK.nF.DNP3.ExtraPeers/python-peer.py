"""Independent dnp3py master; no VagabondK.nF protocol code is imported."""
import asyncio
import struct
import sys
from datetime import UTC, datetime, timedelta

sys.path.insert(0, sys.argv[1])
from dnp3.master import DefaultSOEHandler, Master, MasterConfig, MasterTcpRunner
from dnp3.master.polling import RangePollTask

BOUNDARIES = (
    0, 0x8000000000000000, 1, 0x8000000000000001,
    0x000FFFFFFFFFFFFF, 0x800FFFFFFFFFFFFF, 0x0010000000000000, 0x8010000000000000,
    0x36A0000000000000, 0xB6A0000000000000, 0x380FFFFFC0000000, 0xB80FFFFFC0000000,
    0x3810000000000000, 0xB810000000000000, 0x3FF4000000000000, 0xBFF4000000000000,
    0x47EFFFFFE0000000, 0xC7EFFFFFE0000000, 0x3FF0000000000001, 0xBFF0000000000001,
)
TIMES = (0, 1, 65535, 65536, 4294967295, 4294967296, 281474976710654, 281474976710655)
COUNT = 84


def number(i):
    if i < len(BOUNDARIES):
        return struct.unpack("<d", struct.pack("<Q", BOUNDARIES[i]))[0]
    return struct.unpack("<f", struct.pack("<I", ((i - 20) * 2654435761) & 0xFEFFFFFF))[0]


class Handler(DefaultSOEHandler):
    def __init__(self):
        super().__init__()
        self.received = []

    def on_analog_input(self, values, info):
        self.received.extend(values)

    def on_analog_output(self, values, info):
        self.received.extend(values)


async def main():
    role, port, group, variation = sys.argv[2:]
    if role != "master":
        raise ValueError("Python peer supports master only")
    port, group, variation = int(port), int(group), int(variation)
    handler = Handler()
    master = Master(handler=handler, config=MasterConfig(address=2, outstation_address=1))
    async with MasterTcpRunner(master=master, port=port) as runner:
        responses = await runner.poll(RangePollTask(group=group, variation=variation, start=0, stop=COUNT - 1))
        if any(r.truncation is not None or int(r.iin) & 0xFF00 for r in responses):
            raise AssertionError("truncated/error response")
    if len(handler.received) != COUNT:
        raise AssertionError(f"measurement count {len(handler.received)}")
    seen = set()
    time_limits = 0
    narrow = (group == 30 and variation == 5 or group == 40 and variation == 3
              or group in (32, 42) and variation in (5, 7))
    for p in handler.received:
        i = p.index
        if not 0 <= i < COUNT or i in seen:
            raise AssertionError("index/duplicate")
        seen.add(i)
        expected = number(i)
        if narrow:
            expected = struct.unpack("<f", struct.pack("<f", expected))[0]
        if struct.pack("<d", p.value) != struct.pack("<d", expected):
            raise AssertionError(f"g{group}v{variation} index{i} bits got {struct.pack('<d', p.value).hex()}")
        if p.quality != 1 | ((i * 37) & 254):
            raise AssertionError("quality")
        timed = group in (32, 42) and variation >= 7
        if timed and TIMES[i & 7] > 253402300799999:
            if p.timestamp is not None:
                raise AssertionError("unexpected out-of-datetime-range result")
            time_limits += 1
            continue
        timestamp = datetime(1970, 1, 1, tzinfo=UTC) + timedelta(milliseconds=TIMES[i & 7]) if timed else None
        if p.timestamp != timestamp:
            raise AssertionError("timestamp")
    print(f"Python master <- VagabondK.nF station g{group}v{variation}: NUMERIC/QUALITY PASS {COUNT}; timestamps exact={COUNT - time_limits}.")
    if time_limits:
        print(f"TIME LIMIT {time_limits}: dnp3py master callback loses timestamps beyond datetime year 9999; NOT passed.")


asyncio.run(main())

package main

import (
	"bufio"
	"context"
	"fmt"
	"math"
	"net"
	"os"
	"strconv"
	"time"

	"github.com/otfabric/go-dnp3/app"
	"github.com/otfabric/go-dnp3/client"
	"github.com/otfabric/go-dnp3/server"
)

const count = 84

var boundaries = []uint64{
	0, 0x8000000000000000, 1, 0x8000000000000001,
	0x000fffffffffffff, 0x800fffffffffffff, 0x0010000000000000, 0x8010000000000000,
	0x36a0000000000000, 0xb6a0000000000000, 0x380fffffc0000000, 0xb80fffffc0000000,
	0x3810000000000000, 0xb810000000000000, 0x3ff4000000000000, 0xbff4000000000000,
	0x47efffffe0000000, 0xc7efffffe0000000, 0x3ff0000000000001, 0xbff0000000000001,
}
var times = []uint64{0, 1, 65535, 65536, 4294967295, 4294967296, 281474976710654, 281474976710655}

func number(i int) float64 {
	if i < len(boundaries) {
		return math.Float64frombits(boundaries[i])
	}
	return float64(math.Float32frombits((uint32(i-20) * 2654435761) & 0xfeffffff))
}
func flags(i int) app.Flags { return app.Flags(1 | ((i * 37) & 254)) }
func check(ok bool, reason string) {
	if !ok {
		panic(reason)
	}
}
func must(err error) {
	if err != nil {
		panic(err)
	}
}
func main() {
	if len(os.Args) == 4 && os.Args[1] == "plc-tcp-observe" {
		seconds, err := strconv.Atoi(os.Args[3])
		must(err)
		runPlcObserver(os.Args[2], seconds)
		return
	}
	if len(os.Args) == 3 && os.Args[1] == "plc-tcp" {
		runPlc(os.Args[2])
		return
	}
	if len(os.Args) == 3 && (os.Args[1] == "serial-master" || os.Args[1] == "serial-station") {
		runSerial(os.Args[1], os.Args[2])
		return
	}
	if len(os.Args) == 4 && os.Args[1] == "serial-master-soak" {
		seconds, err := strconv.Atoi(os.Args[3])
		must(err)
		check(seconds > 0, "serial soak duration must be positive")
		runSerialSoak(os.Args[2], time.Duration(seconds)*time.Second)
		return
	}
	check(len(os.Args) == 5, "usage: peer master|station port group variation")
	port, err := strconv.Atoi(os.Args[2])
	must(err)
	group, err := strconv.Atoi(os.Args[3])
	must(err)
	variation, err := strconv.Atoi(os.Args[4])
	must(err)
	gv := app.GV(uint8(group), uint8(variation))
	if os.Args[1] == "master" {
		ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
		defer cancel()
		c, err := client.Dial(ctx, fmt.Sprintf("127.0.0.1:%d", port),
			client.WithAddresses(2, 1), client.WithAutoClearRestart(false), client.WithAutoIntegrity(false))
		must(err)
		defer c.Close()
		r, err := c.Read(ctx, app.Header{GV: gv, Qualifier: app.QualAll})
		must(err)
		points := r.Measurements()
		check(len(points) == count, fmt.Sprintf("measurement count %d", len(points)))
		seen := make([]bool, count)
		for _, p := range points {
			i := int(p.Index)
			check(i < count && !seen[i] && p.GV == gv, "type/index uniqueness")
			seen[i] = true
			value, ok := p.Value.(app.Analog)
			check(ok, "analog type")
			n := number(i)
			if group == 30 && variation == 5 || group == 40 && variation == 3 || group == 32 && (variation == 5 || variation == 7) || group == 42 && (variation == 5 || variation == 7) {
				n = float64(float32(n))
			}
			check(math.Float64bits(value.Value) == math.Float64bits(n),
				fmt.Sprintf("g%dv%d index%d bits got %016x want %016x", group, variation, i, math.Float64bits(value.Value), math.Float64bits(n)))
			check(value.Flags == flags(i), "quality")
			var timestamp uint64
			if (group == 32 || group == 42) && variation >= 7 {
				timestamp = times[i&7]
			}
			check(uint64(value.Time) == timestamp, "timestamp")
		}
		fmt.Printf("Go master <- VagabondK.nF station g%dv%d: NUMERIC/QUALITY PASS %d; timestamps exact.\n", group, variation, count)
	} else {
		check(os.Args[1] == "station", "unknown role")
		s, err := server.New(server.WithAddress(1), server.WithMasters(2), server.WithMaxFragment(256, 2048))
		must(err)
		defer s.Close()
		typ := server.AnalogInput
		static := uint8(6)
		if group == 40 || group == 42 {
			typ = server.AnalogOutput
			static = 4
		}
		for i := 0; i < count; i++ {
			must(s.Database().Add(typ, uint32(i), server.PointConfig{Class: 1, StaticVariation: static, EventVariation: 8}, app.Analog{}))
			timestamp := times[i&7]
			// 이 서버의 공개 API에서 시각 0은 현재 시각을 자동 지정한다는 뜻입니다.
			if timestamp == 0 {
				timestamp = 1
			}
			must(s.Database().Update(typ, uint32(i), app.Analog{Value: number(i), Flags: flags(i), Time: app.Timestamp(timestamp)}, server.EventForce))
		}
		listener, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
		must(err)
		done := make(chan error, 1)
		go func() { done <- s.Serve(listener) }()
		fmt.Printf("READY %d\n", listener.Addr().(*net.TCPAddr).Port)
		scanner := bufio.NewScanner(os.Stdin)
		check(scanner.Scan() && scanner.Text() == "quit", "expected quit")
		must(s.Close())
		must(<-done)
	}
}

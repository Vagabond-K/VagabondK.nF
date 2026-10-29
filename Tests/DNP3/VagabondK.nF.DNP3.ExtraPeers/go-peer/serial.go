package main

import (
	"context"
	"fmt"
	"io"
	"math"
	"net"
	"sync/atomic"
	"time"

	"github.com/otfabric/go-dnp3/app"
	"github.com/otfabric/go-dnp3/client"
	"github.com/otfabric/go-dnp3/server"
	"go.bug.st/serial"
)

var serialMatrix = [][2]uint8{{30, 5}, {30, 6}, {40, 3}, {40, 4},
	{32, 5}, {32, 6}, {32, 7}, {32, 8}, {42, 5}, {42, 6}, {42, 7}, {42, 8}}

func serialFlags(i, operation int) app.Flags {
	f := flags(i)
	if operation&1 == 0 {
		f ^= 128
	}
	return f
}

func openSerial(name string) serial.Port {
	check(name == "COM6", "isolated hardware fixture requires COM6")
	port, err := serial.Open(name, &serial.Mode{
		BaudRate: 9600, DataBits: 8, Parity: serial.EvenParity, StopBits: serial.OneStopBit,
		InitialStatusBits: &serial.ModemOutputBits{RTS: true, DTR: true},
	})
	must(err)
	must(port.ResetInputBuffer())
	must(port.ResetOutputBuffer())
	return port
}

type numericControls struct {
	server.NopHandler
	srv        *server.Server
	operations atomic.Int32
	completed  atomic.Bool
}

func (h *numericControls) Select(ctx context.Context, c server.Command) app.CommandStatus {
	v, ok := c.Value.(app.CROB)
	if !ok || c.GV != app.G12V1 || v.Op != app.OpLatchOn || v.Count != 1 || v.OffTime != 0 {
		return app.StatusNotSupported
	}
	n := h.operations.Load()
	if c.Index == 65501 && n == 12 && v.OnTime == 0 && !h.completed.Load() {
		return app.StatusSuccess
	}
	if c.Index == 65500 && n < 12 && v.OnTime == uint32(n+1) && !h.completed.Load() {
		return app.StatusSuccess
	}
	return app.StatusOutOfRange
}

func (h *numericControls) Operate(ctx context.Context, c server.Command, op server.OperateType) app.CommandStatus {
	if status := h.Select(ctx, c); status != app.StatusSuccess {
		return status
	}
	if c.Index == 65501 {
		h.completed.Store(true)
		return app.StatusSuccess
	}
	n := int(h.operations.Add(1))
	must(h.srv.Database().Batch(func(b *server.Batch) error {
		for _, typ := range []server.PointType{server.AnalogInput, server.AnalogOutput} {
			for i := 0; i < count; i++ {
				timestamp := times[i&7]
				if timestamp == 0 {
					timestamp = 1
				}
				if err := b.Update(typ, uint32(i), app.Analog{
					Value: number(i), Flags: serialFlags(i, n), Time: app.Timestamp(timestamp),
				}, server.EventForce); err != nil {
					return err
				}
			}
		}
		return nil
	}))
	fmt.Printf("OPERATIONS %d\n", n)
	return app.StatusSuccess
}

func runSerial(role, portName string) {
	port := openSerial(portName)
	defer port.Close()
	if role == "serial-master" {
		c, err := client.New(portName, client.WithAddresses(2, 1),
			client.WithAutoClearRestart(false), client.WithAutoIntegrity(false),
			client.WithResponseTimeout(30*time.Second),
			client.WithLink(client.Link{Confirmed: true, Timeout: 3 * time.Second, Retries: 2}),
			client.WithStream(func(context.Context) (io.ReadWriteCloser, net.Addr, error) { return port, nil, nil }))
		must(err)
		defer c.Close()
		ctx, cancel := context.WithTimeout(context.Background(), 180*time.Second)
		defer cancel()
		must(c.Connect(ctx))
		fmt.Printf("READY Go numeric Master %s 9600 8E1 RTS/DTR=true; direct COM, no TCP bridge\n", portName)
		time.Sleep(5 * time.Second)
		must(c.ClearRestart(ctx))
		for n, pair := range serialMatrix {
			_, err = c.Operate(ctx, client.DirectOperate, client.CROB(65500, app.OpLatchOn, uint32(n+1), 0))
			must(err)
			group, variation := pair[0], pair[1]
			gv := app.GV(group, variation)
			r, err := c.Read(ctx, app.Header{GV: gv, Qualifier: app.QualAll})
			must(err)
			check(len(r.Measurements()) == count, "serial measurement count")
			seen := make([]bool, count)
			for _, p := range r.Measurements() {
				i := int(p.Index)
				check(i < count && !seen[i] && p.GV == gv && p.Event == (group == 32 || group == 42), "serial index/type/event")
				seen[i] = true
				v, ok := p.Value.(app.Analog)
				check(ok, "serial analog type")
				expected := number(i)
				if group == 30 && variation == 5 || group == 40 && variation == 3 ||
					(group == 32 || group == 42) && (variation == 5 || variation == 7) {
					expected = float64(float32(expected))
				}
				check(math.Float64bits(v.Value) == math.Float64bits(expected),
					fmt.Sprintf("serial g%dv%d index%d bits got %016x want %016x",
						group, variation, i, math.Float64bits(v.Value), math.Float64bits(expected)))
				check(v.Flags == serialFlags(i, n+1), "serial flags")
				var timestamp uint64
				if (group == 32 || group == 42) && variation >= 7 {
					timestamp = times[i&7]
				}
				check(uint64(v.Time) == timestamp, "serial timestamp")
			}
			drained, err := c.EventPoll(ctx, 1, 2, 3)
			must(err)
			remaining := count * 2
			if group == 32 || group == 42 {
				remaining = count
			}
			check(len(drained.Measurements()) == remaining, "remaining serial event count")
			fmt.Printf("Go RS485 master <- MCU g%dv%d: NUMERIC/QUALITY/TIME PASS %d\n", group, variation, count)
		}
		_, err = c.Operate(ctx, client.DirectOperate, client.CROB(65501, app.OpLatchOn, 0, 0))
		must(err)
		time.Sleep(time.Second)
		fmt.Println("GO RS485 NUMERIC MASTER PASS: 1008 exact IEEE754/quality/time measurements; 12 variations; confirmed links.")
		return
	}
	h := &numericControls{}
	s, err := server.New(server.WithAddress(1), server.WithMasters(2),
		server.WithMaxFragment(256, 2048), server.WithConfirmTimeout(30*time.Second),
		server.WithLink(server.Link{Confirmed: true, Timeout: 3 * time.Second, Retries: 2}),
		server.WithHandler(h))
	must(err)
	h.srv = s
	defer s.Close()
	for _, typ := range []server.PointType{server.AnalogInput, server.AnalogOutput} {
		static := uint8(6)
		if typ == server.AnalogOutput {
			static = 4
		}
		for i := 0; i < count; i++ {
			must(s.Database().Add(typ, uint32(i), server.PointConfig{
				Class: 1, StaticVariation: static, EventVariation: 8,
			}, app.Analog{}))
		}
	}
	done := make(chan error, 1)
	go func() { done <- s.ServeConn(port, nil) }()
	fmt.Printf("READY Go numeric Outstation %s 9600 8E1 RTS/DTR=true; direct COM, no TCP bridge\n", portName)
	deadline := time.NewTimer(180 * time.Second)
	defer deadline.Stop()
	for !h.completed.Load() {
		select {
		case err := <-done:
			panic(fmt.Sprintf("serial station stopped before completion: %v", err))
		case <-deadline.C:
			panic("serial station completion timeout")
		case <-time.After(20 * time.Millisecond):
		}
	}
	time.Sleep(5 * time.Second)
	check(h.operations.Load() == 12, "serial operation total")
	must(s.Close())
	must(<-done)
	fmt.Println("GO RS485 NUMERIC OUTSTATION PASS: 12 arm controls and completion; MCU console must confirm 1008 exact comparisons.")
}

func runSerialSoak(portName string, duration time.Duration) {
	port := openSerial(portName)
	defer port.Close()
	c, err := client.New(portName, client.WithAddresses(2, 1),
		client.WithAutoClearRestart(false), client.WithAutoIntegrity(false),
		client.WithResponseTimeout(30*time.Second),
		client.WithLink(client.Link{Confirmed: true, Timeout: 3 * time.Second, Retries: 2}),
		client.WithStream(func(context.Context) (io.ReadWriteCloser, net.Addr, error) { return port, nil, nil }))
	must(err)
	defer c.Close()
	ctx, cancel := context.WithTimeout(context.Background(), duration+5*time.Minute)
	defer cancel()
	must(c.Connect(ctx))
	fmt.Printf("READY Go numeric Master soak %s 9600 8E1 RTS/DTR=true; duration=%s\n", portName, duration)
	time.Sleep(5 * time.Second)
	must(c.ClearRestart(ctx))

	start := time.Now()
	var operations, measurements, cycles uint64
	for time.Since(start) < duration {
		for _, pair := range serialMatrix {
			operations++
			_, err = c.Operate(ctx, client.DirectOperate,
				client.CROB(65500, app.OpLatchOn, uint32(operations), 0))
			must(err)

			group, variation := pair[0], pair[1]
			gv := app.GV(group, variation)
			r, readErr := c.Read(ctx, app.Header{GV: gv, Qualifier: app.QualAll})
			must(readErr)
			points := r.Measurements()
			check(len(points) == count, fmt.Sprintf("soak measurement count %d", len(points)))
			seen := make([]bool, count)
			for _, p := range points {
				i := int(p.Index)
				check(i < count && !seen[i] && p.GV == gv &&
					p.Event == (group == 32 || group == 42), "soak index/type/event")
				seen[i] = true
				v, ok := p.Value.(app.Analog)
				check(ok, "soak analog type")
				expected := number(i)
				if group == 30 && variation == 5 || group == 40 && variation == 3 ||
					(group == 32 || group == 42) && (variation == 5 || variation == 7) {
					expected = float64(float32(expected))
				}
				check(math.Float64bits(v.Value) == math.Float64bits(expected),
					fmt.Sprintf("soak g%dv%d index%d value", group, variation, i))
				check(v.Flags == serialFlags(i, int(operations)), "soak quality")
				var timestamp uint64
				if (group == 32 || group == 42) && variation >= 7 {
					timestamp = times[i&7]
					if timestamp == 0 {
						timestamp = 1
					}
				}
				check(uint64(v.Time) == timestamp, fmt.Sprintf(
					"soak timestamp g%dv%d index%d got=%d want=%d", group, variation, i, v.Time, timestamp))
			}
			measurements += uint64(len(points))

			drained, pollErr := c.EventPoll(ctx, 1, 2, 3)
			must(pollErr)
			remaining := count * 2
			if group == 32 || group == 42 {
				remaining = count
			}
			check(len(drained.Measurements()) == remaining, "soak remaining event count")
		}
		cycles++
		fmt.Printf("SOAK cycle=%d operations=%d measurements=%d elapsed=%s\n",
			cycles, operations, measurements, time.Since(start).Round(time.Second))
	}

	_, err = c.Operate(ctx, client.DirectOperate, client.CROB(65501, app.OpLatchOn, 0, 0))
	must(err)
	fmt.Printf("GO RS485 NUMERIC MASTER SOAK PASS: duration=%s cycles=%d operations=%d measurements=%d\n",
		time.Since(start).Round(time.Second), cycles, operations, measurements)
}

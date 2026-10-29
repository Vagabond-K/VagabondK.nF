package main

import (
	"context"
	"fmt"
	"math"
	"net"
	"time"

	"github.com/otfabric/go-dnp3/app"
	"github.com/otfabric/go-dnp3/client"
)

func runPlc(host string) {
	check(net.ParseIP(host) != nil, "PLC peer requires an explicit isolated IP address")
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()
	c, err := client.Dial(ctx, net.JoinHostPort(host, "20000"),
		client.WithAddresses(2, 1), client.WithAutoClearRestart(false), client.WithAutoIntegrity(false))
	must(err)
	defer c.Close()
	must(c.ClearRestart(ctx))
	must(c.DisableUnsolicited(ctx))
	readSetting := func() app.Analog {
		r, err := c.Read(ctx, app.AllObjects(app.G40V4))
		must(err)
		p := r.Measurements()
		check(len(p) == 1 && p[0].Index == 1 && !p[0].Event, "PLC setting read shape")
		value, ok := p[0].Value.(app.Analog)
		check(ok && value.Flags == 1 && value.Time == 0, "PLC setting quality/unknown time")
		return value
	}

	original := readSetting().Value
	statuses, err := c.Operate(ctx, client.DirectOperate, client.SetPoint(65535, 1))
	check(err != nil && len(statuses) == 1 && statuses[0] == app.StatusNotSupported, "unmapped PLC command must be rejected")
	check(math.Float64bits(readSetting().Value) == math.Float64bits(original), "rejected PLC command changed the setting")
	set := func(value float64, mode client.CommandMode) {
		cmd := client.SetPoint(1, value)
		cmd.GV = app.G41V4
		statuses, err := c.Operate(ctx, mode, cmd)
		must(err)
		check(len(statuses) == 1 && statuses[0] == app.StatusSuccess, "PLC command status")
		deadline := time.Now().Add(3 * time.Second)
		for {
			got := readSetting().Value
			if math.Float64bits(got) == math.Float64bits(value) {
				break
			}
			check(time.Now().Before(deadline), fmt.Sprintf("PLC next-scan commit: got %016x want %016x",
				math.Float64bits(got), math.Float64bits(value)))
			time.Sleep(20 * time.Millisecond)
		}
		_, err = c.EventPoll(ctx, 1, 2, 3)
		must(err)
	}
	for i := 0; i < count; i++ {
		mode := client.DirectOperate
		if i&1 == 0 {
			mode = client.SelectBeforeOperate
		}
		set(float64(float32(number(i))), mode)
	}
	set(original, client.SelectBeforeOperate)
	r, err := c.Read(ctx, app.AllObjects(app.G30V6))
	must(err)
	p := r.Measurements()
	check(len(p) == 1 && p[0].Index == 0, "PLC input shape")
	value, ok := p[0].Value.(app.Analog)
	check(ok && value.Flags == 2 && value.Time == 0, "PLC NoData input must not be advertised ONLINE")
	fmt.Println("GO PLC TCP PASS: 84 exact float32 boundary Setting controls; direct and SELECT/OPERATE; next-scan readback; original setting restored; NoData input; event drains.")
}

func runPlcObserver(host string, seconds int) {
	check(net.ParseIP(host) != nil && seconds >= 60 && seconds <= 86400, "PLC observation requires explicit IP and 60..86400 seconds")
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(seconds+30)*time.Second)
	defer cancel()
	c, err := client.Dial(ctx, net.JoinHostPort(host, "20000"),
		client.WithAddresses(2, 1), client.WithAutoClearRestart(false), client.WithAutoIntegrity(false))
	must(err)
	defer c.Close()
	must(c.ClearRestart(ctx))
	must(c.DisableUnsolicited(ctx))
	deadline := time.Now().Add(time.Duration(seconds) * time.Second)
	rounds := 0
	for time.Now().Before(deadline) {
		r, err := c.Read(ctx, app.AllObjects(app.G40V4))
		must(err)
		p := r.Measurements()
		check(len(p) == 1 && p[0].Index == 1 && !p[0].Event, "observed PLC setting shape")
		v, ok := p[0].Value.(app.Analog)
		check(ok && v.Value == 321 && v.Flags == 1 && v.Time == 0, "observed PLC setting changed")
		_, err = c.EventPoll(ctx, 1, 2, 3)
		must(err)
		rounds++
		time.Sleep(100 * time.Millisecond)
	}
	fmt.Printf("GO PLC OBSERVER PASS: rounds=%d; seconds=%d; stable 321/ONLINE, unknown UTC; concurrent scans/Modbus/NEUP.\n", rounds, seconds)
}

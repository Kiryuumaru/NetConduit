package main

import (
	"encoding/json"
	"fmt"
	"math/rand"
	"net"
	"os"
	"runtime"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"github.com/hashicorp/yamux"
	"github.com/xtaci/smux"
)

// Result matches the JSON schema used by the .NET benchmark and report.py
type Result struct {
	Implementation string  `json:"implementation"`
	Scenario       string  `json:"scenario"`
	Channels       int     `json:"channels"`
	DataSizeBytes  int     `json:"dataSizeBytes"`
	TotalBytes     int64   `json:"totalBytes"`
	DurationMs     float64 `json:"durationMs"`
	ThroughputMBps float64 `json:"throughputMBps"`
	MessagesPerSec float64 `json:"messagesPerSec"`
	AllocMB        float64 `json:"allocMB"`
}

func main() {
	filter := ""
	if len(os.Args) > 1 {
		filter = os.Args[1]
	}

	var results []Result

	if filter == "" || filter == "throughput" {
	// Throughput scenarios
	channelCounts := []int{1, 10, 100}
	dataSizes := []int{1024, 102400, 1048576}

	for _, ch := range channelCounts {
		for _, ds := range dataSizes {
			for _, impl := range []string{"Raw TCP (Go)", "FRP/Yamux (Go)", "Smux (Go)"} {
				mbps, allocMB := runThroughput(impl, ch, ds, 5)
				totalBytes := int64(ch) * int64(ds)
				durationMs := float64(totalBytes) / mbps / 1048576.0 * 1000.0
				results = append(results, Result{
					Implementation: impl,
					Scenario:       "throughput",
					Channels:       ch,
					DataSizeBytes:  ds,
					TotalBytes:     totalBytes,
					DurationMs:     durationMs,
					ThroughputMBps: mbps,
					AllocMB:        allocMB,
				})
				fmt.Fprintf(os.Stderr, "  throughput %-18s ch=%4d data=%8d  %8.1f MB/s\n", impl, ch, ds, mbps)
			}
		}
	}
	}

	if filter == "" || filter == "game-tick" {
	// Game-tick scenarios
	gtChannels := []int{1, 10, 50, 1000}
	msgSizes := []int{64, 256}

	for _, ch := range gtChannels {
		for _, ms := range msgSizes {
			for _, impl := range []string{"Raw TCP (Go)", "FRP/Yamux (Go)", "Smux (Go)"} {
				mps, allocMB := runGameTick(impl, ch, ms, 2*time.Second, 5)
				results = append(results, Result{
					Implementation: impl,
					Scenario:       "game-tick",
					Channels:       ch,
					DataSizeBytes:  ms,
					TotalBytes:     0,
					MessagesPerSec: mps,
					AllocMB:        allocMB,
				})
				fmt.Fprintf(os.Stderr, "  game-tick  %-18s ch=%4d msg=%4d  %8.0f msg/s\n", impl, ch, ms, mps)
			}
		}
	}
	}

	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(results); err != nil {
		fmt.Fprintf(os.Stderr, "JSON encode error: %v\n", err)
		os.Exit(1)
	}
}

// ---- Throughput benchmark ----

func runThroughput(impl string, channelCount, dataSize, runs int) (mbps float64, allocMB float64) {
	measurements := make([]float64, 0, runs)
	var totalAlloc float64

	for run := 0; run < runs; run++ {
		var memBefore runtime.MemStats
		runtime.ReadMemStats(&memBefore)

		var m float64
		switch impl {
		case "Raw TCP (Go)":
			m = runThroughputRawTCP(channelCount, dataSize)
		case "FRP/Yamux (Go)":
			m = runThroughputYamux(channelCount, dataSize)
		case "Smux (Go)":
			m = runThroughputSmux(channelCount, dataSize)
		}

		var memAfter runtime.MemStats
		runtime.ReadMemStats(&memAfter)
		totalAlloc += float64(memAfter.TotalAlloc-memBefore.TotalAlloc) / 1048576.0

		measurements = append(measurements, m)
	}

	sort.Float64s(measurements)
	return measurements[len(measurements)/2], totalAlloc / float64(runs)
}

func runThroughputRawTCP(channelCount, dataSize int) float64 {
	listeners := make([]net.Listener, channelCount)
	for i := 0; i < channelCount; i++ {
		l, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			panic(err)
		}
		listeners[i] = l
	}

	sendBuf := makeRandBuf(min(dataSize, 65536))

	var wg sync.WaitGroup
	start := time.Now()

	// Readers
	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func(l net.Listener) {
			defer wg.Done()
			conn, err := l.Accept()
			if err != nil {
				return
			}
			defer conn.Close()
			buf := make([]byte, 65536)
			total := 0
			for total < dataSize {
				n, err := conn.Read(buf)
				if err != nil {
					break
				}
				total += n
			}
		}(listeners[i])
	}

	// Writers
	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func(l net.Listener) {
			defer wg.Done()
			conn, err := net.Dial("tcp", l.Addr().String())
			if err != nil {
				return
			}
			defer conn.Close()
			total := 0
			for total < dataSize {
				toSend := min(len(sendBuf), dataSize-total)
				n, err := conn.Write(sendBuf[:toSend])
				if err != nil {
					break
				}
				total += n
			}
		}(listeners[i])
	}

	wg.Wait()
	elapsed := time.Since(start)

	for _, l := range listeners {
		l.Close()
	}

	totalBytes := int64(channelCount) * int64(dataSize)
	return float64(totalBytes) / elapsed.Seconds() / 1048576.0
}

func runThroughputYamux(channelCount, dataSize int) float64 {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	defer l.Close()

	sendBuf := makeRandBuf(min(dataSize, 65536))

	serverReady := make(chan *yamux.Session, 1)
	go func() {
		conn, err := l.Accept()
		if err != nil {
			return
		}
		sess, err := yamux.Server(conn, yamux.DefaultConfig())
		if err != nil {
			conn.Close()
			return
		}
		serverReady <- sess
	}()

	clientConn, err := net.Dial("tcp", l.Addr().String())
	if err != nil {
		panic(err)
	}
	clientSess, err := yamux.Client(clientConn, yamux.DefaultConfig())
	if err != nil {
		panic(err)
	}
	serverSess := <-serverReady

	defer clientSess.Close()
	defer serverSess.Close()

	var wg sync.WaitGroup
	start := time.Now()

	for i := 0; i < channelCount; i++ {
		// Reader (server accepts stream)
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := serverSess.AcceptStream()
			if err != nil {
				return
			}
			defer stream.Close()
			buf := make([]byte, 65536)
			total := 0
			for total < dataSize {
				n, err := stream.Read(buf)
				if err != nil {
					break
				}
				total += n
			}
		}()

		// Writer (client opens stream)
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := clientSess.OpenStream()
			if err != nil {
				return
			}
			defer stream.Close()
			total := 0
			for total < dataSize {
				toSend := min(len(sendBuf), dataSize-total)
				n, err := stream.Write(sendBuf[:toSend])
				if err != nil {
					break
				}
				total += n
			}
		}()
	}

	wg.Wait()
	elapsed := time.Since(start)

	totalBytes := int64(channelCount) * int64(dataSize)
	return float64(totalBytes) / elapsed.Seconds() / 1048576.0
}

func runThroughputSmux(channelCount, dataSize int) float64 {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	defer l.Close()

	sendBuf := makeRandBuf(min(dataSize, 65536))

	serverReady := make(chan *smux.Session, 1)
	go func() {
		conn, err := l.Accept()
		if err != nil {
			return
		}
		sess, err := smux.Server(conn, smux.DefaultConfig())
		if err != nil {
			conn.Close()
			return
		}
		serverReady <- sess
	}()

	clientConn, err := net.Dial("tcp", l.Addr().String())
	if err != nil {
		panic(err)
	}
	clientSess, err := smux.Client(clientConn, smux.DefaultConfig())
	if err != nil {
		panic(err)
	}
	serverSess := <-serverReady

	defer clientSess.Close()
	defer serverSess.Close()

	var wg sync.WaitGroup
	start := time.Now()

	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := serverSess.AcceptStream()
			if err != nil {
				return
			}
			defer stream.Close()
			buf := make([]byte, 65536)
			total := 0
			for total < dataSize {
				n, err := stream.Read(buf)
				if err != nil {
					break
				}
				total += n
			}
		}()

		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := clientSess.OpenStream()
			if err != nil {
				return
			}
			defer stream.Close()
			total := 0
			for total < dataSize {
				toSend := min(len(sendBuf), dataSize-total)
				n, err := stream.Write(sendBuf[:toSend])
				if err != nil {
					break
				}
				total += n
			}
		}()
	}

	wg.Wait()
	elapsed := time.Since(start)

	totalBytes := int64(channelCount) * int64(dataSize)
	return float64(totalBytes) / elapsed.Seconds() / 1048576.0
}

// ---- Game-tick benchmark ----

func runGameTick(impl string, channelCount, msgSize int, duration time.Duration, runs int) (mps float64, allocMB float64) {
	measurements := make([]float64, 0, runs)
	var totalAlloc float64

	for run := 0; run < runs; run++ {
		var memBefore runtime.MemStats
		runtime.ReadMemStats(&memBefore)

		var m float64
		switch impl {
		case "Raw TCP (Go)":
			m = runGameTickRawTCP(channelCount, msgSize, duration)
		case "FRP/Yamux (Go)":
			m = runGameTickYamux(channelCount, msgSize, duration)
		case "Smux (Go)":
			m = runGameTickSmux(channelCount, msgSize, duration)
		}

		var memAfter runtime.MemStats
		runtime.ReadMemStats(&memAfter)
		totalAlloc += float64(memAfter.TotalAlloc-memBefore.TotalAlloc) / 1048576.0

		measurements = append(measurements, m)
	}

	sort.Float64s(measurements)
	return measurements[len(measurements)/2], totalAlloc / float64(runs)
}

func runGameTickRawTCP(channelCount, msgSize int, duration time.Duration) float64 {
	listeners := make([]net.Listener, channelCount)
	for i := 0; i < channelCount; i++ {
		l, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			panic(err)
		}
		listeners[i] = l
	}

	sendBuf := makeRandBuf(msgSize)
	var totalMsgs int64
	done := make(chan struct{})

	var wg sync.WaitGroup

	// Readers
	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func(l net.Listener) {
			defer wg.Done()
			conn, err := l.Accept()
			if err != nil {
				return
			}
			defer conn.Close()
			buf := make([]byte, msgSize*4)
			for {
				select {
				case <-done:
					return
				default:
				}
				conn.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
				_, err := conn.Read(buf)
				if err != nil {
					if ne, ok := err.(net.Error); ok && ne.Timeout() {
						continue
					}
					return
				}
			}
		}(listeners[i])
	}

	// Writers
	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func(l net.Listener) {
			defer wg.Done()
			conn, err := net.Dial("tcp", l.Addr().String())
			if err != nil {
				return
			}
			defer conn.Close()
			for {
				select {
				case <-done:
					return
				default:
				}
				_, err := conn.Write(sendBuf)
				if err != nil {
					return
				}
				atomic.AddInt64(&totalMsgs, 1)
			}
		}(listeners[i])
	}

	time.Sleep(duration)
	close(done)
	wg.Wait()

	for _, l := range listeners {
		l.Close()
	}

	return float64(totalMsgs) / duration.Seconds()
}

func runGameTickYamux(channelCount, msgSize int, duration time.Duration) float64 {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	defer l.Close()

	sendBuf := makeRandBuf(msgSize)
	var totalMsgs int64
	done := make(chan struct{})

	serverReady := make(chan *yamux.Session, 1)
	go func() {
		conn, err := l.Accept()
		if err != nil {
			return
		}
		sess, err := yamux.Server(conn, yamux.DefaultConfig())
		if err != nil {
			conn.Close()
			return
		}
		serverReady <- sess
	}()

	clientConn, err := net.Dial("tcp", l.Addr().String())
	if err != nil {
		panic(err)
	}
	clientSess, err := yamux.Client(clientConn, yamux.DefaultConfig())
	if err != nil {
		panic(err)
	}
	serverSess := <-serverReady

	defer clientSess.Close()
	defer serverSess.Close()

	var wg sync.WaitGroup

	for i := 0; i < channelCount; i++ {
		// Reader
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := serverSess.AcceptStream()
			if err != nil {
				return
			}
			defer stream.Close()
			buf := make([]byte, msgSize*4)
			for {
				select {
				case <-done:
					return
				default:
				}
				_, err := stream.Read(buf)
				if err != nil {
					return
				}
			}
		}()

		// Writer
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := clientSess.OpenStream()
			if err != nil {
				return
			}
			defer stream.Close()
			for {
				select {
				case <-done:
					return
				default:
				}
				_, err := stream.Write(sendBuf)
				if err != nil {
					return
				}
				atomic.AddInt64(&totalMsgs, 1)
			}
		}()
	}

	time.Sleep(duration)
	close(done)
	wg.Wait()

	return float64(totalMsgs) / duration.Seconds()
}

func runGameTickSmux(channelCount, msgSize int, duration time.Duration) float64 {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	defer l.Close()

	sendBuf := makeRandBuf(msgSize)
	var totalMsgs int64
	done := make(chan struct{})

	serverReady := make(chan *smux.Session, 1)
	go func() {
		conn, err := l.Accept()
		if err != nil {
			return
		}
		sess, err := smux.Server(conn, smux.DefaultConfig())
		if err != nil {
			conn.Close()
			return
		}
		serverReady <- sess
	}()

	clientConn, err := net.Dial("tcp", l.Addr().String())
	if err != nil {
		panic(err)
	}
	clientSess, err := smux.Client(clientConn, smux.DefaultConfig())
	if err != nil {
		panic(err)
	}
	serverSess := <-serverReady

	defer clientSess.Close()
	defer serverSess.Close()

	var wg sync.WaitGroup

	for i := 0; i < channelCount; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := serverSess.AcceptStream()
			if err != nil {
				return
			}
			defer stream.Close()
			buf := make([]byte, msgSize*4)
			for {
				select {
				case <-done:
					return
				default:
				}
				_, err := stream.Read(buf)
				if err != nil {
					return
				}
			}
		}()

		wg.Add(1)
		go func() {
			defer wg.Done()
			stream, err := clientSess.OpenStream()
			if err != nil {
				return
			}
			defer stream.Close()
			for {
				select {
				case <-done:
					return
				default:
				}
				_, err := stream.Write(sendBuf)
				if err != nil {
					return
				}
				atomic.AddInt64(&totalMsgs, 1)
			}
		}()
	}

	time.Sleep(duration)
	close(done)
	wg.Wait()

	return float64(totalMsgs) / duration.Seconds()
}

// ---- Helpers ----

func makeRandBuf(size int) []byte {
	buf := make([]byte, size)
	rand.Read(buf)
	return buf
}

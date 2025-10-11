package types

import (
	"context"
	"net"
	"time"

	"github.com/emiago/diago"
)

type CallSession struct {
	ID               string
	CallerID         string
	Dialog           *diago.DialogServerSession
	State            CallState
	IVRLevel         int
	QueueID          int
	StartTime        time.Time
	Context          context.Context
	Cancel           context.CancelFunc
	RTPSendPort      int
	RTPRecvPort      int
	AgentAvailable   chan struct{}
	AgentAudioChan   chan []byte
	CallerAudioChan  chan []byte
	AgentUDPConn     *net.UDPConn
}

type CallState int

const (
	StateConnecting CallState = iota
	StateIVR
	StateQueue
	StateConnected
	StateHangup
	StateAgent
)

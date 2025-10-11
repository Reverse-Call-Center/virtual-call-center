package server

import (
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/agent"
	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/session"
	"github.com/Reverse-Call-Center/virtual-call-center/types"
	"github.com/gofiber/fiber/v2"
)

var (
	usedPorts    = make(map[int]bool)
	portsMutex   sync.RWMutex
	activeAgents = make(map[string]*net.UDPConn)
	agentsMutex  sync.RWMutex
)

func StartHTTPServer(listenAddr string, cfg *config.Config) {
	app := fiber.New()

	app.Get("/health", func(c *fiber.Ctx) error {
		return c.SendString("OK")
	})

	app.Get("/calls/active", func(c *fiber.Ctx) error {
		calls, err := session.GetRedisActiveCalls()
		if err != nil {
			return c.Status(fiber.StatusInternalServerError).JSON(fiber.Map{"error": "Failed to get active calls"})
		}

		type CallResponse struct {
			ID        string `json:"id"`
			CallerID  string `json:"caller_id"`
			State     string `json:"state"`
			QueueID   int    `json:"queue_id,omitempty"`
			IVRLevel  int    `json:"ivr_level,omitempty"`
			StartTime string `json:"start_time"`
			WaitTime  int    `json:"wait_time"`
		}

		var activeCalls []CallResponse
		for _, call := range calls {
			if call.State == types.StateQueue {
				waitTime := int(time.Since(call.StartTime).Seconds())
				activeCalls = append(activeCalls, CallResponse{
					ID:        call.ID,
					CallerID:  call.CallerID,
					State:     stateToString(call.State),
					QueueID:   call.QueueID,
					IVRLevel:  call.IVRLevel,
					StartTime: call.StartTime.Format(time.RFC3339),
					WaitTime:  waitTime,
				})
			}
		}

		return c.JSON(fiber.Map{"calls": activeCalls})
	})

	app.Get("/metrics", func(c *fiber.Ctx) error {
		// Placeholder for metrics endpoint
		return c.SendString("Metrics not implemented yet")
	})

	app.Post("/calls/answer", func(c *fiber.Ctx) error {
		var body struct {
			CallID string `json:"call_id"`
		}
		if err := c.BodyParser(&body); err != nil {
			return c.Status(fiber.StatusBadRequest).JSON(fiber.Map{"error": "Invalid request body"})
		}

		callSession := session.GetCall(body.CallID)
		if callSession == nil {
			return c.Status(fiber.StatusNotFound).JSON(fiber.Map{"error": "Call not found"})
		}

		if callSession.State != types.StateQueue {
			return c.Status(fiber.StatusBadRequest).JSON(fiber.Map{"error": "Call is not in queue state"})
		}

		udpPort, err := findAvailablePort(cfg.RTPInitialPort)
		if err != nil {
			return c.Status(fiber.StatusInternalServerError).JSON(fiber.Map{"error": "No available ports"})
		}

		reservePort(udpPort)
		defer func() {
			releasePort(udpPort)
		}()

		go func() {
			conn := agent.StartAgentServer(&net.UDPAddr{
				IP:   net.ParseIP("0.0.0.0"),
				Port: udpPort,
			}, callSession, cfg)

			// Store the UDP connection in the call session
			callSession.AgentUDPConn = conn

			agentsMutex.Lock()
			activeAgents[body.CallID] = conn
			agentsMutex.Unlock()

			defer func() {
				agentsMutex.Lock()
				delete(activeAgents, body.CallID)
				agentsMutex.Unlock()
				if conn != nil {
					conn.Close()
				}
				callSession.AgentUDPConn = nil
			}()
		}()

		callSession.State = types.StateAgent
		session.UpdateCallState(callSession)

		// Signal the queue that an agent is available
		select {
		case callSession.AgentAvailable <- struct{}{}:
			log.Printf("Signaled agent availability for call %s", body.CallID)
		default:
			log.Printf("Agent availability signal failed for call %s (queue may have ended)", body.CallID)
		}

		log.Printf("Agent connected to call %s on UDP port %d", body.CallID, udpPort)

		return c.JSON(fiber.Map{
			"status":      "call answered",
			"call_id":     body.CallID,
			"udp_port":    udpPort,
			"caller_id":   callSession.CallerID,
			"start_time":  callSession.StartTime,
			"state":       "agent",
			"rtp_address": fmt.Sprintf("%s:%d", cfg.AgentAddress, udpPort),
		})
	})

	app.Patch("/calls/transfer?id=:id&dest=:dest", func(c *fiber.Ctx) error {
		callID := c.Params("id")
		dest := c.Params("dest")
		var body struct {
			State string `json:"state"`
		}
		if err := c.BodyParser(&body); err != nil {
			return c.Status(fiber.StatusBadRequest).JSON(fiber.Map{"error": "Invalid request body"})
		}
		// Placeholder for call transfer logic
		log.Printf("Transferring call %s to %s with state %s", callID, dest, body.State)
		return c.JSON(fiber.Map{"status": "transfer initiated"})
	})

	log.Printf("Starting HTTP server on %s", listenAddr)
	if err := app.Listen(listenAddr); err != nil {
		log.Fatalf("Error starting HTTP server: %v", err)
	}
}

func findAvailablePort(startPort int) (int, error) {
	portsMutex.RLock()
	defer portsMutex.RUnlock()

	for port := startPort; port < startPort+1000; port++ {
		if !usedPorts[port] {
			if isPortAvailable(port) {
				return port, nil
			}
		}
	}
	return 0, fmt.Errorf("no available ports in range %d-%d", startPort, startPort+1000)
}

func isPortAvailable(port int) bool {
	addr, err := net.ResolveUDPAddr("udp", fmt.Sprintf(":%d", port))
	if err != nil {
		return false
	}

	conn, err := net.ListenUDP("udp", addr)
	if err != nil {
		return false
	}
	conn.Close()
	return true
}

func reservePort(port int) {
	portsMutex.Lock()
	defer portsMutex.Unlock()
	usedPorts[port] = true
}

func releasePort(port int) {
	portsMutex.Lock()
	defer portsMutex.Unlock()
	delete(usedPorts, port)
}

func stateToString(state types.CallState) string {
	switch state {
	case types.StateConnecting:
		return "Connecting"
	case types.StateIVR:
		return "IVR"
	case types.StateQueue:
		return "Queue"
	case types.StateConnected:
		return "Connected"
	case types.StateAgent:
		return "Agent"
	case types.StateHangup:
		return "Hangup"
	default:
		return "Unknown"
	}
}

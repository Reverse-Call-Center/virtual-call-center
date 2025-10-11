package agent

import (
	"log"
	"net"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/types"
	"github.com/pion/rtp"
)

func StartAgentServer(listenAddr *net.UDPAddr, callSession *types.CallSession, cfg *config.Config) *net.UDPConn {
	udpSocket, err := net.ListenUDP("udp", listenAddr)
	if err != nil {
		panic(err)
	}

	var rtpPacket rtp.Packet
	buffer := make([]byte, 2048)
	var agentAddr *net.UDPAddr
	sequenceNumber := uint16(0)
	socketClosed := make(chan struct{})

	log.Printf("Agent server listening on %s for call %s", listenAddr, callSession.ID)

	go func() {
		defer func() {
			udpSocket.Close()
			close(socketClosed)
			log.Printf("Call %s ended, closed agent UDP server", callSession.ID)
		}()
		
		for {
			select {
			case <-callSession.Context.Done():
				return
			default:
				udpSocket.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
				bytesRead, clientAddr, err := udpSocket.ReadFrom(buffer)
				
				if err != nil {
					if netErr, ok := err.(net.Error); ok && netErr.Timeout() {
						continue
					}
					// Check if the context was cancelled before logging the error
					select {
					case <-callSession.Context.Done():
						return
					default:
						log.Printf("Error reading from UDP socket: %v", err)
						return
					}
				}

				if agentAddr == nil {
					agentAddr = clientAddr.(*net.UDPAddr)
					log.Printf("Agent connected from %s for call %s", agentAddr, callSession.ID)
				}

				if err := rtpPacket.Unmarshal(buffer[:bytesRead]); err != nil {
					log.Printf("Error unmarshaling RTP packet: %v", err)
					continue
				}

				if int(rtpPacket.PayloadType) != 0 {
					log.Printf("Received non-PCMU packet, payload type: %d", rtpPacket.PayloadType)
					continue
				}

				log.Printf("Received agent audio: %d bytes from %s for call %s", len(rtpPacket.Payload), agentAddr, callSession.ID)

				if callSession.AgentAudioChan != nil {
					select {
					case callSession.AgentAudioChan <- rtpPacket.Payload:
						log.Printf("Forwarded agent audio to SIP call %s", callSession.ID)
					default:
						log.Printf("AgentAudioChan full for call %s, dropping packet", callSession.ID)
					}
				}
			}
		}
	}()

	go func() {
		for {
			select {
			case <-callSession.Context.Done():
				return
			case <-socketClosed:
				return
			case callerAudio := <-callSession.CallerAudioChan:
				log.Printf("Received caller audio for agent: %d bytes for call %s", len(callerAudio), callSession.ID)
				if agentAddr != nil {
					outboundPacket := rtp.Packet{
						Header: rtp.Header{
							Version:        2,
							Padding:        false,
							Extension:      false,
							Marker:         false,
							PayloadType:    0,
							SequenceNumber: sequenceNumber,
							Timestamp:      uint32(time.Now().UnixNano() / 1000000),
							SSRC:           12345,
						},
						Payload: callerAudio,
					}

					packetData, err := outboundPacket.Marshal()
					if err != nil {
						log.Printf("Error marshaling outbound RTP packet: %v", err)
						continue
					}

					select {
					case <-socketClosed:
						return
					default:
						if _, err := udpSocket.WriteToUDP(packetData, agentAddr); err != nil {
							select {
							case <-callSession.Context.Done():
								return
							case <-socketClosed:
								return
							default:
								log.Printf("Error sending RTP packet to agent: %v", err)
								return
							}
						}
						log.Printf("Sent caller audio to agent %s for call %s (%d bytes)", agentAddr, callSession.ID, len(callerAudio))
						sequenceNumber++
					}
				} else {
					log.Printf("No agent connected yet, dropping caller audio for call %s", callSession.ID)
				}
			}
		}
	}()

	return udpSocket
}

func ulawDecodeSample(u byte) int16 {
	u = ^u
	sign := u & 0x80
	seg := (u >> 4) & 0x07
	mant := int16(u & 0x0F)
	val := ((mant << 3) + 0x84) << seg
	if sign != 0 {
		return -(val - 0x84)
	}
	return val - 0x84
}

func ulawDecodeBuffer(src []byte, dst []int16) {
	for i := range dst {
		dst[i] = ulawDecodeSample(src[i])
	}
}

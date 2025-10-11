package handlers

import (
	"fmt"
	"log"
	"net"
	"strconv"
	"sync"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/agent"
	"github.com/Reverse-Call-Center/virtual-call-center/audio"
	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/session"
	"github.com/Reverse-Call-Center/virtual-call-center/types"
)

var (
	ivrConfig    map[int]*config.Ivr
	queueConfig  map[int]*config.Queue
	redisManager *session.RedisManager
	globalConfig *config.Config
)

func InitializeConfigs(rm *session.RedisManager, cfg *config.Config) {
	redisManager = rm
	globalConfig = cfg

	ivrConfig = make(map[int]*config.Ivr)
	queueConfig = make(map[int]*config.Queue)

	ivrConfigData, err := config.LoadIvrConfig()
	if err != nil {
		fmt.Printf("Error loading IVR config: %v\n", err)
		return
	}

	for _, ivr := range ivrConfigData.IVRs {
		if ivr.OptionId == 0 {
			fmt.Printf("Skipping IVR with OptionId 0: %v\n", ivr)
			continue
		}
		if _, exists := ivrConfig[ivr.OptionId]; exists {
			fmt.Printf("Duplicate IVR OptionId %d found, skipping: %v\n", ivr.OptionId, ivr)
			continue
		}
		fmt.Printf("Loading IVR: %v\n", ivr)
		ivrConfig[ivr.OptionId] = ivr
	}

	queueConfigData, err := config.LoadQueueConfig()
	if err != nil {
		fmt.Printf("Error loading Queue config: %v\n", err)
		return
	}
	for _, queue := range queueConfigData.Queues {
		if queue.OptionId == 0 {
			fmt.Printf("Skipping Queue with OptionId 0: %v\n", queue)
			continue
		}
		if _, exists := queueConfig[queue.OptionId]; exists {
			fmt.Printf("Duplicate Queue OptionId %d found, skipping: %v\n", queue.OptionId, queue)
			continue
		}
		fmt.Printf("Loading Queue: %v\n", queue)
		queueConfig[queue.OptionId] = queue
	}
}

func RouteCallToAction(session *types.CallSession, digit string) {
	currentIVR, exists := ivrConfig[session.IVRLevel]
	if !exists {
		fmt.Printf("Current IVR config not found for level %d\n", session.IVRLevel)
		return
	}

	var selectedOption *config.Option
	for _, option := range currentIVR.Options {
		if strconv.Itoa(option.OptionNumber) == digit {
			selectedOption = &option
			break
		}
	}

	if selectedOption == nil {
		fmt.Printf("Invalid option %s selected\n", digit)
		audio.PlayAudioFile(session, currentIVR.InvalidOptionMessage)
		HandleIVRFlow(session, currentIVR)
		return
	}

	action := selectedOption.OptionAction

	if action == 0 {
		fmt.Printf("Hanging up call %s\n", session.ID)
		session.Dialog.Hangup(session.Context)
		session.State = types.StateHangup
		trackCallState(session)
		removeCall(session.ID)
		return
	}

	for _, ivr := range ivrConfig {
		if ivr.OptionId == action {
			session.IVRLevel = action
			session.State = types.StateIVR
			trackCallState(session)
			HandleIVRFlow(session, ivr)
			return
		}
	}

	for _, queue := range queueConfig {
		log.Printf("Checking queue %d for action %d\n", queue.OptionId, action)
		if queue.OptionId == action {
			session.State = types.StateQueue
			trackCallState(session)
			HandleQueueLogic(session, queue)
			return
		}
	}

	fmt.Printf("No action found for action %d\n", action)
	audio.PlayAudioFile(session, currentIVR.InvalidOptionMessage)
	HandleIVRFlow(session, currentIVR)
}

func HandleAgentFlow(session *types.CallSession, cfg *config.Config) {
	startPort := 40000

	session.State = types.StateAgent
	trackCallState(session)

	conn := agent.StartAgentServer(&net.UDPAddr{
		IP:   net.ParseIP("0.0.0.0"),
		Port: startPort,
	}, session, cfg)

	defer func() {
		if conn != nil {
			conn.Close()
		}
		session.State = types.StateQueue
		trackCallState(session)
		RouteCallToAction(session, "2000")
	}()

	session.State = types.StateAgent
	fmt.Printf("Call %s connected to agent\n", session.ID)
}

func HandleIVRFlow(session *types.CallSession, ivrConfig *config.Ivr) {
	dtmfChan := make(chan string, 1)
	dtmfDone := make(chan struct{})
	audioDone := make(chan struct{})
	stopAudio := make(chan struct{})

	go func() {
		defer close(dtmfDone)
		audio.ListenForDTMF(session, dtmfChan)
	}()

	go func() {
		defer close(audioDone)
		if err := audio.PlayAudioFileInterruptible(session, ivrConfig.WelcomeMessage, stopAudio); err != nil {
			fmt.Printf("Error playing IVR welcome message for call %s: %v\n", session.ID, err)
			return
		}
	}()

	timeout := time.Duration(ivrConfig.Timeout) * time.Second
	if timeout <= 0 {
		timeout = 30 * time.Second
	}

	ivrTimer := time.NewTimer(timeout)
	defer ivrTimer.Stop()

	select {
	case digit := <-dtmfChan:
		close(stopAudio)
		if !ivrTimer.Stop() {
			<-ivrTimer.C
		}

		if digit == "" {
			fmt.Printf("Empty DTMF digit received for call %s\n", session.ID)
			return
		}

		fmt.Printf("Received DTMF digit: %s for call %s\n", digit, session.ID)
		RouteCallToAction(session, digit)
		return

	case <-audioDone:
		select {
		case digit := <-dtmfChan:
			if !ivrTimer.Stop() {
				<-ivrTimer.C
			}

			if digit == "" {
				fmt.Printf("Empty DTMF digit received after audio for call %s\n", session.ID)
				return
			}
			fmt.Printf("Received DTMF digit after audio: %s for call %s\n", digit, session.ID)
			RouteCallToAction(session, digit)
			return

		case <-ivrTimer.C:
			fmt.Printf("IVR timeout for call %s\n", session.ID)

			if ivrConfig.TimeoutMessage != "" {
				audio.PlayAudioFile(session, ivrConfig.TimeoutMessage)
			}

			if ivrConfig.TimeoutAction == 0 {
				fmt.Printf("Hanging up call %s due to timeout\n", session.ID)
				session.Dialog.Hangup(session.Context)
				session.State = types.StateHangup
			} else {
				RouteCallToAction(session, strconv.Itoa(ivrConfig.TimeoutAction))
			}
			return

		case <-session.Context.Done():
			fmt.Printf("Call %s context cancelled during IVR\n", session.ID)
			return

		case <-dtmfDone:
			fmt.Printf("DTMF listener ended for call %s\n", session.ID)
			return
		}

	case <-ivrTimer.C:
		close(stopAudio)
		fmt.Printf("IVR timeout for call %s\n", session.ID)

		if ivrConfig.TimeoutMessage != "" {
			audio.PlayAudioFile(session, ivrConfig.TimeoutMessage)
		}

		if ivrConfig.TimeoutAction == 0 {
			fmt.Printf("Hanging up call %s due to timeout\n", session.ID)
			session.Dialog.Hangup(session.Context)
			session.State = types.StateHangup
		} else {
			RouteCallToAction(session, strconv.Itoa(ivrConfig.TimeoutAction))
		}

	case <-session.Context.Done():
		close(stopAudio)
		fmt.Printf("Call %s context cancelled during IVR\n", session.ID)
		return

	case <-dtmfDone:
		close(stopAudio)
		fmt.Printf("DTMF listener ended for call %s\n", session.ID)
		return
	}
}

func HandleQueueLogic(session *types.CallSession, queueConfig *config.Queue) {
	fmt.Printf("Entering queue %d for call %s\n", queueConfig.OptionId, session.ID)
	session.State = types.StateQueue
	session.QueueID = queueConfig.OptionId
	trackCallState(session)

	queueTimer := time.NewTimer(time.Duration(queueConfig.Timeout) * time.Second)
	defer queueTimer.Stop()

	holdMusicDone := make(chan struct{})
	var holdMusicOnce sync.Once

	go func() {
		defer holdMusicOnce.Do(func() { close(holdMusicDone) })
		lastAnnounceTime := time.Now()

		for {
			select {
			case <-session.Context.Done():
				return
			case <-holdMusicDone:
				return
			default:
				if time.Since(lastAnnounceTime) >= time.Duration(queueConfig.AnnounceTime)*time.Second {
					fmt.Printf("Playing announcement for call %s in queue %d\n", session.ID, queueConfig.OptionId)
					if err := audio.PlayAudioFileInterruptible(session, queueConfig.AnnounceMessage, holdMusicDone); err != nil {
						fmt.Printf("Error playing announce message for call %s: %v\n", session.ID, err)
					}
					lastAnnounceTime = time.Now()
					time.Sleep(500 * time.Millisecond)
				}

				select {
				case <-holdMusicDone:
					return
				default:
					if err := audio.PlayAudioFileInterruptible(session, queueConfig.HoldMusic, holdMusicDone); err != nil {
						fmt.Printf("Error playing hold music for call %s: %v\n", session.ID, err)
						return
					}
				}
			}
		}
	}()

	fmt.Printf("Call %s waiting in queue %d\n", session.ID, queueConfig.OptionId)

	select {
	case <-queueTimer.C:
		holdMusicOnce.Do(func() { close(holdMusicDone) })
		fmt.Printf("Queue timeout for call %s\n", session.ID)

		if queueConfig.TimeoutMessage != "" {
			audio.PlayAudioFile(session, queueConfig.TimeoutMessage)
		}

		if queueConfig.TimeoutAction == 0 {
			fmt.Printf("Hanging up call %s due to queue timeout\n", session.ID)
			session.Dialog.Hangup(session.Context)
			session.State = types.StateHangup
			trackCallState(session)
			removeCall(session.ID)
		} else {
			RouteCallToAction(session, strconv.Itoa(queueConfig.TimeoutAction))
		}

	case <-session.AgentAvailable:
		holdMusicOnce.Do(func() { close(holdMusicDone) })
		fmt.Printf("Agent answered call %s, transitioning to agent mode\n", session.ID)
		session.State = types.StateAgent
		trackCallState(session)

		fmt.Printf("Audio routing for call %s is managed by agent UDP server\n", session.ID)

		fmt.Printf("Call %s now in agent mode, waiting for completion\n", session.ID)
		<-session.Context.Done()
		fmt.Printf("Agent call %s completed\n", session.ID)
		return

	case <-holdMusicDone:
		fmt.Printf("Hold music ended for call %s, call likely disconnected\n", session.ID)
		session.State = types.StateHangup
		trackCallState(session)
		removeCall(session.ID)
		return

	case <-session.Context.Done():
		holdMusicOnce.Do(func() { close(holdMusicDone) })
		fmt.Printf("Call %s context cancelled while in queue\n", session.ID)
		session.State = types.StateHangup
		trackCallState(session)
		removeCall(session.ID)
		return
	}
}

func GetIVRConfig(optionId int) (*config.Ivr, bool) {
	ivr, exists := ivrConfig[optionId]
	return ivr, exists
}

func trackCallState(callSession *types.CallSession) {
	if redisManager != nil {
		if err := redisManager.UpdateCallState(callSession); err != nil {
			log.Printf("Error tracking call state: %v", err)
		}
	}
}

func removeCall(callID string) {
	if redisManager != nil {
		if err := redisManager.RemoveCall(callID); err != nil {
			log.Printf("Error removing call from tracking: %v", err)
		}
	}
}

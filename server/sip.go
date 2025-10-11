package server

import (
	"context"
	"fmt"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/audio"
	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/handlers"
	"github.com/Reverse-Call-Center/virtual-call-center/session"
	"github.com/Reverse-Call-Center/virtual-call-center/types"
	"github.com/Reverse-Call-Center/virtual-call-center/utils"
	"github.com/emiago/diago"
	"github.com/emiago/sipgo"
)

func StartSIPServer(ctx context.Context, globalConfig *config.Config, onStarted ...func()) {
	fmt.Println("Starting SIP server on:", globalConfig.SIPProtocol, globalConfig.SIPListenAddress, ":", globalConfig.SIPPort)

	transport := diago.Transport{
		Transport: globalConfig.SIPProtocol,
		BindHost:  globalConfig.SIPListenAddress,
		BindPort:  globalConfig.SIPPort,
	}

	ua, err := sipgo.NewUA()
	if err != nil {
		fmt.Printf("Error creating SIP User Agent: %v\n", err)
		return
	}

	dg := diago.NewDiago(ua, diago.WithTransport(transport))

	if len(onStarted) > 0 && onStarted[0] != nil {
		onStarted[0]()
	}

	dg.Serve(ctx, func(inDialog *diago.DialogServerSession) {
		HandleIncomingCall(ctx, inDialog, globalConfig)
	})
}

func HandleIncomingCall(parentCtx context.Context, inDialog *diago.DialogServerSession, globalConfig *config.Config) {
	callCtx, cancel := context.WithCancel(parentCtx)
	defer cancel()

	callerID := utils.ExtractCallerPhone(inDialog.InviteRequest.Headers())

	callSession := &types.CallSession{
		ID:              utils.GenerateCallID(),
		CallerID:        callerID,
		Dialog:          inDialog,
		State:           types.StateConnecting,
		IVRLevel:        globalConfig.InitialOptionId,
		StartTime:       time.Now(),
		Context:         callCtx,
		Cancel:          cancel,
		RTPSendPort:     globalConfig.RTPInitialPort,
		RTPRecvPort:     globalConfig.RTPInitialPort + 1,
		AgentAvailable:  make(chan struct{}),
		AgentAudioChan:  make(chan []byte, 100),
		CallerAudioChan: make(chan []byte, 100),
	}

	session.RegisterCall(callSession)

	defer func() {
		session.UnregisterCall(callSession.ID)
		if globalConfig.LogPhoneNumbers {
			fmt.Printf("Call %s from %s ended\n", callSession.ID, callSession.CallerID)
		} else {
			fmt.Printf("Call %s ended\n", callSession.ID)
		}
	}()

	if globalConfig.LogPhoneNumbers {
		fmt.Printf("New call %s from %s\n", callSession.ID, callSession.CallerID)
	} else {
		fmt.Printf("New call %s\n", callSession.ID)
	}

	inDialog.Trying()
	inDialog.Answer()

	if globalConfig.RecordDisclaimerMessage != "" {
		if err := audio.PlayAudioFile(callSession, globalConfig.RecordDisclaimerMessage); err != nil {
			fmt.Printf("Error playing disclaimer for call %s: %v\n", callSession.ID, err)
		}
	}

	callSession.State = types.StateIVR
	if initialIVR, exists := handlers.GetIVRConfig(globalConfig.InitialOptionId); exists {
		handlers.HandleIVRFlow(callSession, initialIVR)
	} else {
		fmt.Printf("No IVR config found for OptionId %d\n", globalConfig.InitialOptionId)
		callSession.Dialog.Hangup(callSession.Context)
	}
}

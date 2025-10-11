package main

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"strconv"

	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/handlers"
	"github.com/Reverse-Call-Center/virtual-call-center/server"
	"github.com/Reverse-Call-Center/virtual-call-center/session"
)

var Config *config.Config
var RedisManager *session.RedisManager

func GetConfig() *config.Config {
	return Config
}

func GetRedisManager() *session.RedisManager {
	return RedisManager
}

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()

	fmt.Println("Virtual Call Center Starting...")
	Config, err := config.LoadConfig()
	if err != nil {
		fmt.Printf("Error loading config: %v\n", err)
		os.Exit(1)
	}

	RedisManager = session.NewRedisManager(Config)
	if RedisManager != nil {
		defer RedisManager.Close()
		session.SetRedisManager(RedisManager)
		if err := RedisManager.PurgeCalls(); err != nil {
			fmt.Printf("Error purging Redis calls: %v\n", err)
		}
	}

	handlers.InitializeConfigs(RedisManager, Config)

	server.StartSIPServer(ctx, Config, func() {
		go startHealthCheckServer(Config.SIPPort + 1)
		go server.StartHTTPServer(fmt.Sprintf(":%d", Config.ServicePort), Config)
	})
}

func startHealthCheckServer(port int) {
	http.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"healthy","service":"virtual-call-center"}`))
	})

	addr := ":" + strconv.Itoa(port)
	fmt.Printf("Health check server listening on %s/health\n", addr)

	if err := http.ListenAndServe(addr, nil); err != nil {
		fmt.Printf("Health check server error: %v\n", err)
	}
}

package config

import (
	"encoding/json"
	"fmt"
	"os"
)

type QueueConfig struct {
	Queues map[int]*Queue `json:"queues"`
}

type Queue struct {
	OptionId        int    `json:"option_id"`
	HoldMusic       string `json:"hold_music"`
	Timeout         int    `json:"timeout"`
	TimeoutMessage  string `json:"timeout_message"`
	TimeoutAction   int    `json:"timeout_action"`
	AnnounceTime    int    `json:"announce_time"`
	AnnounceMessage string `json:"announce_message"`
}

func LoadQueueConfig() (*QueueConfig, error) {
	file, err := os.Open("./configs/queue.json")
	if err != nil {
		return nil, fmt.Errorf("error opening config file: %v", err)
	}
	defer file.Close()

	var config QueueConfig
	if err := json.NewDecoder(file).Decode(&config); err != nil {
		return nil, fmt.Errorf("error decoding config JSON: %v", err)
	}

	return &config, nil
}

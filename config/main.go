package config

import (
	"encoding/json"
	"os"
	"path/filepath"
)

type Config struct {
	SIPProtocol             string `json:"sip_protocol"`
	SIPPort                 int    `json:"sip_port"`
	SIPListenAddress        string `json:"pbx_listen_address"`
	ServicePort             int    `json:"service_port"`
	InitialOptionId         int    `json:"initial_option_id"`
	RecordDisclaimerMessage string `json:"record_disclaimer_message"`
	LogPhoneNumbers         bool   `json:"log_phone_numbers"`
	RTPInitialPort          int    `json:"rtp_initial_port"`
	AgentAddress            string `json:"agent_address"`
	AgentPort               int    `json:"agent_port"`
	RedisAddress            string `json:"redis_address"`
}

func LoadConfig() (*Config, error) {
	currentDir, err := os.Getwd()
	if err != nil {
		return nil, err
	}
	configPath := filepath.Join(currentDir, "configs", "config.json")

	configData, err := os.ReadFile(configPath)

	if err != nil {
		return nil, err
	}

	var config Config
	if err := json.Unmarshal(configData, &config); err != nil {
		return nil, err
	}
	return &config, nil
}

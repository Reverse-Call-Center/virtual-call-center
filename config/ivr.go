package config

import (
	"encoding/json"
	"fmt"
	"os"
)

type IvrConfig struct {
	IVRs []*Ivr `json:"ivrs"`
}

type Ivr struct {
	OptionId             int      `json:"option_id"`
	WelcomeMessage       string   `json:"welcome_message"`
	Timeout              int      `json:"timeout"`
	TimeoutMessage       string   `json:"timeout_message"`
	TimeoutAction        int      `json:"timeout_action"`
	InvalidOptionMessage string   `json:"invalid_option_message"`
	Options              []Option `json:"options"`
}

type Option struct {
	OptionNumber int `json:"option_number"`
	OptionAction int `json:"option_action"`
}

func LoadIvrConfig() (*IvrConfig, error) {
	file, err := os.Open("./configs/ivr.json")
	if err != nil {
		return nil, fmt.Errorf("error opening config file: %v", err)
	}
	defer file.Close()

	var config IvrConfig
	if err := json.NewDecoder(file).Decode(&config); err != nil {
		return nil, fmt.Errorf("error decoding config JSON: %v", err)
	}

	return &config, nil
}

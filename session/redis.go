package session

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/config"
	"github.com/Reverse-Call-Center/virtual-call-center/types"
	"github.com/redis/go-redis/v9"
)

type RedisManager struct {
	client *redis.Client
	ctx    context.Context
}

func NewRedisManager(cfg *config.Config) *RedisManager {
	rdb := redis.NewClient(&redis.Options{
		Addr: cfg.RedisAddress,
		DB:   0,
	})

	ctx := context.Background()

	if err := rdb.Ping(ctx).Err(); err != nil {
		log.Printf("Failed to connect to Redis: %v", err)
		return nil
	}

	log.Printf("Connected to Redis at %s", cfg.RedisAddress)
	return &RedisManager{
		client: rdb,
		ctx:    ctx,
	}
}

func (rm *RedisManager) PurgeCalls() error {
	if rm == nil {
		return fmt.Errorf("redis manager not initialized")
	}

	keys, err := rm.client.Keys(rm.ctx, "call:*").Result()
	if err != nil {
		return fmt.Errorf("error getting call keys: %v", err)
	}

	if len(keys) > 0 {
		err = rm.client.Del(rm.ctx, keys...).Err()
		if err != nil {
			return fmt.Errorf("error deleting call keys: %v", err)
		}
		log.Printf("Purged %d calls from Redis", len(keys))
	}

	return nil
}

func (rm *RedisManager) TrackCall(session *types.CallSession) error {
	if rm == nil {
		return nil
	}

	callInfo := CallInfo{
		ID:        session.ID,
		CallerID:  session.CallerID,
		State:     session.State,
		IVRLevel:  session.IVRLevel,
		QueueID:   session.QueueID,
		StartTime: session.StartTime,
		UpdatedAt: time.Now(),
	}

	data, err := json.Marshal(callInfo)
	if err != nil {
		return fmt.Errorf("error marshaling call info: %v", err)
	}

	key := fmt.Sprintf("call:%s", session.ID)
	err = rm.client.Set(rm.ctx, key, data, 24*time.Hour).Err()
	if err != nil {
		return fmt.Errorf("error storing call in Redis: %v", err)
	}

	log.Printf("Tracked call %s in Redis: State=%s, CallerID=%s", session.ID, stateToString(session.State), session.CallerID)
	return nil
}

func (rm *RedisManager) UpdateCallState(session *types.CallSession) error {
	if rm == nil {
		return nil
	}

	return rm.TrackCall(session)
}

func (rm *RedisManager) RemoveCall(callID string) error {
	if rm == nil {
		return nil
	}

	key := fmt.Sprintf("call:%s", callID)
	err := rm.client.Del(rm.ctx, key).Err()
	if err != nil {
		return fmt.Errorf("error removing call from Redis: %v", err)
	}

	log.Printf("Removed call %s from Redis", callID)
	return nil
}

func (rm *RedisManager) GetActiveCalls() ([]CallInfo, error) {
	if rm == nil {
		return nil, fmt.Errorf("redis manager not initialized")
	}

	keys, err := rm.client.Keys(rm.ctx, "call:*").Result()
	if err != nil {
		return nil, fmt.Errorf("error getting call keys: %v", err)
	}

	var calls []CallInfo
	for _, key := range keys {
		data, err := rm.client.Get(rm.ctx, key).Result()
		if err != nil {
			continue
		}

		var callInfo CallInfo
		if err := json.Unmarshal([]byte(data), &callInfo); err != nil {
			continue
		}

		calls = append(calls, callInfo)
	}

	return calls, nil
}

func (rm *RedisManager) Close() error {
	if rm == nil {
		return nil
	}
	return rm.client.Close()
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

package session

import (
	"sync"
	"time"

	"github.com/Reverse-Call-Center/virtual-call-center/types"
)

type CallInfo struct {
	ID        string          `json:"id"`
	CallerID  string          `json:"caller_id"`
	State     types.CallState `json:"state"`
	IVRLevel  int             `json:"ivr_level"`
	QueueID   int             `json:"queue_id"`
	StartTime time.Time       `json:"start_time"`
	UpdatedAt time.Time       `json:"updated_at"`
}

var (
	activeCalls  map[string]*types.CallSession
	callsMutex   sync.RWMutex
	redisManager *RedisManager
)

func init() {
	activeCalls = make(map[string]*types.CallSession)
}

func SetRedisManager(rm *RedisManager) {
	redisManager = rm
}

func RegisterCall(session *types.CallSession) {
	callsMutex.Lock()
	defer callsMutex.Unlock()
	activeCalls[session.ID] = session

	if redisManager != nil {
		redisManager.TrackCall(session)
	}
}

func UnregisterCall(callID string) {
	callsMutex.Lock()
	defer callsMutex.Unlock()
	delete(activeCalls, callID)

	if redisManager != nil {
		redisManager.RemoveCall(callID)
	}
}

func GetCall(callID string) *types.CallSession {
	callsMutex.RLock()
	defer callsMutex.RUnlock()
	return activeCalls[callID]
}

func UpdateCallState(session *types.CallSession) {
	if redisManager != nil {
		redisManager.UpdateCallState(session)
	}
}

func GetRedisActiveCalls() ([]CallInfo, error) {
	if redisManager != nil {
		return redisManager.GetActiveCalls()
	}
	return []CallInfo{}, nil
}

func GetActiveCallCount() int {
	callsMutex.RLock()
	defer callsMutex.RUnlock()
	return len(activeCalls)
}

func GetCallByID(callID string) (*types.CallSession, bool) {
	callsMutex.RLock()
	defer callsMutex.RUnlock()
	call, exists := activeCalls[callID]
	return call, exists
}

func GetCallsInState(state types.CallState) []*types.CallSession {
	callsMutex.RLock()
	defer callsMutex.RUnlock()

	var calls []*types.CallSession
	for _, call := range activeCalls {
		if call.State == state {
			calls = append(calls, call)
		}
	}
	return calls
}

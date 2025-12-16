# Week 4 Day 1-2: NT-Based Agent Identification - Testing Guide

## Overview
This guide provides step-by-step instructions for testing the NT-based agent identification implementation. The system now identifies agents via Windows NT account (sam_account_name) instead of workstation-to-employee binding.

---

## Prerequisites

1. **Database Migration Applied**
   - Migration `037_add_nt_based_identification.sql` must be executed
   - Verify: `SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_name = 'agent_adherence_events' AND column_name = 'nt';`

2. **Test Employee Setup**
   - At least one test employee with NT account populated in `employee_personal_info`
   - Verify: `SELECT employee_id, nt FROM employee_personal_info WHERE nt IS NOT NULL LIMIT 5;`

3. **Backend Running**
   - Adherence backend service running and connected to shared database
   - Port: 4001 (or configured port)

4. **Desktop Agent**
   - Desktop agent installed on test workstation
   - Agent configured with workstation_id and API key

---

## Phase 1: Database Migration Testing

### 1.1 Verify Migration Applied

```sql
-- Check nt column exists
SELECT column_name, data_type, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'agent_adherence_events' 
  AND column_name = 'nt';

-- Expected: column_name='nt', data_type='character varying', is_nullable='YES'

-- Check employee_id is nullable
SELECT column_name, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'agent_adherence_events' 
  AND column_name = 'employee_id';

-- Expected: is_nullable='YES'

-- Check workstation employee_id is nullable
SELECT column_name, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'agent_workstation_configurations' 
  AND column_name = 'employee_id';

-- Expected: is_nullable='YES'

-- Check indexes created
SELECT indexname, indexdef 
FROM pg_indexes 
WHERE tablename = 'agent_adherence_events' 
  AND indexname LIKE '%nt%';

-- Expected: idx_events_nt_timestamp should exist

-- Check unique index on employee_personal_info.nt
SELECT indexname, indexdef 
FROM pg_indexes 
WHERE tablename = 'employee_personal_info' 
  AND indexname LIKE '%nt%';

-- Expected: idx_employee_personal_info_nt should exist
```

### 1.2 Verify Workstation Configs Cleared

```sql
-- Check existing workstations have NULL employee_id
SELECT id, workstation_id, employee_id, is_active 
FROM agent_workstation_configurations;

-- Expected: employee_id should be NULL for all rows
```

---

## Phase 2: Backend API Testing

### 2.1 Test Workstation Registration (Device-Only)

**Request:**
```bash
POST http://localhost:4001/api/adherence/admin/workstations/register
Authorization: Bearer <JWT_TOKEN>
Content-Type: application/json

{
  "workstation_name": "Test-Workstation-01",
  "os_version": "Windows 11",
  "agent_version": "1.0.0"
}
```

**Expected Response (201 Created):**
```json
{
  "workstation_id": "<UUID>",
  "api_key": "<43-character-key>",
  "message": "Workstation registered successfully",
  "warning": "These credentials will not be shown again. Store them securely.",
  "note": "This workstation is device-only. Employee identification happens via NT account at event ingestion."
}
```

**Verify:**
- No `employee_id` in request (should not be required)
- Response includes workstation_id and api_key
- Database record has `employee_id = NULL`

### 2.2 Test Event Ingestion - Valid NT Account

**Prerequisites:**
- Get workstation_id and api_key from registration
- Ensure test employee has NT account: `SELECT employee_id, nt FROM employee_personal_info WHERE nt IS NOT NULL LIMIT 1;`

**Request:**
```bash
POST http://localhost:4001/api/adherence/events
X-API-Key: <API_KEY>
X-Workstation-ID: <WORKSTATION_ID>
Content-Type: application/json

{
  "event_type": "LOGIN",
  "event_timestamp": "2025-12-20T10:00:00Z",
  "nt": "z.salah.3613"
}
```

**Expected Response (200 OK):**
```json
{
  "success": true,
  "message": "Event ingested successfully",
  "events_processed": 1,
  "events_failed": 0
}
```

**Verify in Database:**
```sql
SELECT id, employee_id, nt, event_type, event_timestamp 
FROM agent_adherence_events 
ORDER BY created_at DESC 
LIMIT 1;

-- Expected:
-- - employee_id should match the employee with nt='z.salah.3613'
-- - nt should be 'z.salah.3613'
-- - Both fields populated
```

### 2.3 Test Event Ingestion - Missing NT Field

**Request:**
```bash
POST http://localhost:4001/api/adherence/events
X-API-Key: <API_KEY>
X-Workstation-ID: <WORKSTATION_ID>
Content-Type: application/json

{
  "event_type": "LOGIN",
  "event_timestamp": "2025-12-20T10:00:00Z"
}
```

**Expected Response (400 Bad Request):**
```json
{
  "statusCode": 400,
  "message": ["nt field is required (Windows NT account sam_account_name)"],
  "error": "Bad Request"
}
```

### 2.4 Test Event Ingestion - Invalid NT Format (with domain prefix)

**Request:**
```bash
POST http://localhost:4001/api/adherence/events
X-API-Key: <API_KEY>
X-Workstation-ID: <WORKSTATION_ID>
Content-Type: application/json

{
  "event_type": "LOGIN",
  "event_timestamp": "2025-12-20T10:00:00Z",
  "nt": "OCTOPUS\\z.salah.3613"
}
```

**Expected Response (400 Bad Request):**
```json
{
  "statusCode": 400,
  "message": ["nt must be sam_account_name only (e.g., z.salah.3613), no domain prefix"],
  "error": "Bad Request"
}
```

### 2.5 Test Event Ingestion - Unmapped NT Account

**Request:**
```bash
POST http://localhost:4001/api/adherence/events
X-API-Key: <API_KEY>
X-Workstation-ID: <WORKSTATION_ID>
Content-Type: application/json

{
  "event_type": "LOGIN",
  "event_timestamp": "2025-12-20T10:00:00Z",
  "nt": "unmapped.user.1234"
}
```

**Expected Response (409 Conflict):**
```json
{
  "statusCode": 409,
  "message": "Employee not found for NT account: unmapped.user.1234. Please ensure the NT account is registered in employee_personal_info.",
  "error": "Conflict"
}
```

**Verify:**
- Event is NOT stored in database
- Error message includes the NT account value

### 2.6 Test Batch Event Ingestion

**Request:**
```bash
POST http://localhost:4001/api/adherence/events
X-API-Key: <API_KEY>
X-Workstation-ID: <WORKSTATION_ID>
Content-Type: application/json

{
  "events": [
    {
      "event_type": "LOGIN",
      "event_timestamp": "2025-12-20T10:00:00Z",
      "nt": "z.salah.3613"
    },
    {
      "event_type": "WINDOW_CHANGE",
      "event_timestamp": "2025-12-20T10:01:00Z",
      "nt": "z.salah.3613",
      "application_name": "Chrome",
      "window_title": "Test Page"
    },
    {
      "event_type": "IDLE_START",
      "event_timestamp": "2025-12-20T10:02:00Z",
      "nt": "unmapped.user.1234"
    }
  ]
}
```

**Expected Response (200 OK):**
```json
{
  "success": true,
  "message": "Events ingested successfully",
  "events_processed": 2,
  "events_failed": 1
}
```

**Verify:**
- First 2 events stored successfully
- Third event rejected (unmapped NT)
- Check backend logs for warning about unmapped NT

---

## Phase 3: Desktop Agent Testing

### 3.1 Verify NT Account Capture

**Test Steps:**
1. Start desktop agent service
2. Check agent logs for NT account capture
3. Verify events are buffered with NT account

**Check SQLite Buffer:**
```sql
-- Connect to agent's SQLite database (usually in AppData)
SELECT id, event_type, nt_account, event_timestamp, status 
FROM event_buffer 
ORDER BY created_at DESC 
LIMIT 10;

-- Expected:
-- - All events should have nt_account populated
-- - Format should be sam_account_name only (e.g., z.salah.3613)
-- - No domain prefix
```

### 3.2 Test Login/Logoff Events

**Test Steps:**
1. Lock workstation (Windows + L)
2. Unlock workstation
3. Check agent logs for LOGIN/LOGOFF events with NT account

**Expected:**
- LOGOFF event captured on lock
- LOGIN event captured on unlock
- Both events include NT account from Security Event Log or current session

### 3.3 Test Active Window Monitoring

**Test Steps:**
1. Switch between different applications (Chrome, Notepad, etc.)
2. Wait for window change events (polling interval ~10 seconds)
3. Check buffered events

**Expected:**
- WINDOW_CHANGE events captured
- Each event includes NT account from current session
- Application name, path, and window title populated

### 3.4 Test Idle Monitoring

**Test Steps:**
1. Stop all mouse/keyboard activity for idle threshold duration (default 5 minutes)
2. Resume activity
3. Check buffered events

**Expected:**
- IDLE_START event captured when idle threshold reached
- IDLE_END event captured when activity resumes
- Both events include NT account

### 3.5 Test Event Upload with Valid NT

**Prerequisites:**
- Ensure test employee NT account is registered
- Agent has valid workstation_id and API key

**Test Steps:**
1. Generate events with valid NT account
2. Wait for upload interval (default 60 seconds)
3. Check backend logs for successful upload
4. Verify events in database

**Expected:**
- Events uploaded successfully
- Backend logs show: "Uploaded X events"
- Events stored in database with both `nt` and `employee_id`

### 3.6 Test Event Upload with Unmapped NT

**Test Steps:**
1. Temporarily change NT account in employee_personal_info to unmapped value
2. Generate events
3. Wait for upload
4. Check agent logs

**Expected:**
- Upload attempted
- Backend returns 409 Conflict
- Agent logs warning: "Upload rejected - unmapped NT account (409 Conflict)"
- Events marked as FAILED in buffer (not retried)

**Verify Buffer:**
```sql
SELECT id, event_type, nt_account, status, error_message 
FROM event_buffer 
WHERE status = 'FAILED' 
  AND error_message LIKE '%unmapped%';

-- Expected: Events with unmapped NT marked as FAILED
```

---

## Phase 4: Integration Testing

### 4.1 Multiple Agents Same Workstation

**Test Scenario:**
- Agent A logs in with NT account "user.a.1234"
- Agent B logs in with NT account "user.b.5678"
- Both use same workstation

**Test Steps:**
1. Register workstation (device-only)
2. Agent A logs in → generates LOGIN event with NT "user.a.1234"
3. Agent A logs out
4. Agent B logs in → generates LOGIN event with NT "user.b.5678"
5. Verify events attributed to correct employees

**Expected:**
- Both events stored with same `workstation_id`
- Different `employee_id` resolved from respective NT accounts
- Events correctly attributed to Agent A and Agent B

### 4.2 NT Account Format Validation

**Test Cases:**

| NT Account Input | Expected Result |
|-----------------|----------------|
| `z.salah.3613` | ✅ Accepted |
| `OCTOPUS\z.salah.3613` | ❌ Rejected (400 Bad Request) |
| `z.salah.3613@domain.com` | ❌ Rejected (400 Bad Request) |
| Empty string | ❌ Rejected (400 Bad Request) |
| Missing field | ❌ Rejected (400 Bad Request) |

---

## Phase 5: Error Handling Testing

### 5.1 Backend Error Responses

| Scenario | Expected HTTP Status | Expected Message |
|----------|---------------------|------------------|
| Missing `nt` field | 400 Bad Request | "nt field is required..." |
| Invalid `nt` format (domain prefix) | 400 Bad Request | "nt must be sam_account_name only..." |
| Unmapped NT account | 409 Conflict | "Employee not found for NT account: ..." |
| Missing `event_timestamp` | 400 Bad Request | "event_timestamp or timestamp is required" |
| Invalid timestamp format | 400 Bad Request | "Invalid timestamp format..." |

### 5.2 Desktop Agent Error Handling

**Test Scenarios:**

1. **Network Error During Upload**
   - Expected: Retry with exponential backoff
   - Events remain PENDING in buffer

2. **409 Conflict (Unmapped NT)**
   - Expected: Events marked as FAILED
   - No retry attempts
   - Warning logged

3. **429 Too Many Requests**
   - Expected: Retry with backoff
   - Batch size reduced

4. **503 Service Unavailable**
   - Expected: Retry with backoff
   - Upload interval increased

---

## Verification Checklist

### Database Verification
- [ ] `nt` column exists in `agent_adherence_events`
- [ ] `employee_id` is nullable in `agent_adherence_events`
- [ ] `employee_id` is nullable in `agent_workstation_configurations`
- [ ] Index `idx_events_nt_timestamp` exists
- [ ] Unique index `idx_employee_personal_info_nt` exists
- [ ] Existing workstation configs have `employee_id = NULL`

### Backend Verification
- [ ] Workstation registration works without `employee_id`
- [ ] Events with valid NT are ingested successfully
- [ ] Events with missing NT return 400 Bad Request
- [ ] Events with invalid NT format return 400 Bad Request
- [ ] Events with unmapped NT return 409 Conflict
- [ ] Batch events process correctly (partial success allowed)
- [ ] Employee ID resolved correctly from NT account

### Desktop Agent Verification
- [ ] NT account captured from Security Event Log (login/logoff)
- [ ] NT account captured from current session (idle/window events)
- [ ] NT format is correct (sam_account_name only, no domain)
- [ ] All events include `nt_account` in SQLite buffer
- [ ] Upload service includes `nt` in API payload
- [ ] 409 Conflict responses handled correctly (no retry)
- [ ] Events without NT are skipped before upload

### Integration Verification
- [ ] Multiple agents can use same workstation
- [ ] Events correctly attributed to correct employees via NT
- [ ] Unmapped NT events rejected appropriately
- [ ] System handles edge cases gracefully

---

## Troubleshooting

### Issue: Events not being ingested

**Check:**
1. Verify NT account exists in `employee_personal_info`
2. Check backend logs for error messages
3. Verify workstation_id and API key are correct
4. Check database connection

### Issue: NT account not captured

**Check:**
1. Verify agent has permissions to read Security Event Log
2. Check agent logs for NT capture warnings
3. Verify `WindowsIdentityHelper.GetCurrentNtAccount()` returns value
4. Check SQLite buffer for `nt_account` column

### Issue: 409 Conflict for valid NT

**Check:**
1. Verify NT account format (no domain prefix)
2. Check `employee_personal_info.nt` column for exact match
3. Verify case sensitivity (should match exactly)
4. Check for whitespace in NT account

### Issue: Migration fails

**Check:**
1. Verify database user has ALTER TABLE permissions
2. Check for existing constraints that might block changes
3. Verify no active transactions holding locks
4. Check PostgreSQL version compatibility

---

## Test Data Setup

### Create Test Employee with NT Account

```sql
-- Find an existing employee or create test employee
INSERT INTO employees (full_name_en, full_name_ar, status, created_at, updated_at)
VALUES ('Test User', 'مستخدم تجريبي', 'Active', NOW(), NOW())
RETURNING id;

-- Update employee_personal_info with NT account
INSERT INTO employee_personal_info (employee_id, nt, created_at, updated_at)
VALUES ('<EMPLOYEE_ID>', 'test.user.1234', NOW(), NOW())
ON CONFLICT (employee_id) 
DO UPDATE SET nt = 'test.user.1234', updated_at = NOW();
```

### Verify Test Data

```sql
-- Check test employee setup
SELECT e.id, e.full_name_en, epi.nt
FROM employees e
LEFT JOIN employee_personal_info epi ON e.id = epi.employee_id
WHERE epi.nt = 'test.user.1234';
```

---

## Success Criteria

✅ **All tests pass**  
✅ **No errors in backend logs**  
✅ **No errors in desktop agent logs**  
✅ **Events correctly stored with NT and employee_id**  
✅ **Unmapped NT events properly rejected**  
✅ **Multiple agents can use same workstation**  
✅ **System handles edge cases gracefully**

---

## Next Steps After Testing

1. Document any issues found
2. Update test employee NT accounts if needed
3. Prepare for production deployment
4. Monitor system logs for first 24 hours
5. Verify adherence calculations work with NT-based events

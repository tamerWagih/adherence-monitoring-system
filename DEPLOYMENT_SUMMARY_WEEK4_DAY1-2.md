# Week 4 Day 1-2: NT-Based Agent Identification - Deployment Summary

## Changes Overview

This implementation transitions from **workstation-to-employee binding** to **dynamic NT account (sam_account_name) identification**. Multiple agents can now use the same workstation on different shifts, with employee resolution happening at event ingestion time via the `nt` field.

---

## Files Changed

### Database Migration
- **New File:** `people-ops-system/database/migrations/037_add_nt_based_identification.sql`
  - Adds `nt` column to `agent_adherence_events`
  - Makes `employee_id` nullable in events and workstation configs
  - Adds indexes for NT-based queries
  - Clears `employee_id` from existing workstations

### Backend Changes

#### Entities
- `adherence-monitoring-system/backend/src/entities/agent-adherence-event.entity.ts`
  - Added `nt` field (varchar(100), nullable)
  - Made `employeeId` nullable
  - Fixed `workstationId` type to varchar (was incorrectly uuid)

- `adherence-monitoring-system/backend/src/entities/agent-workstation-configuration.entity.ts`
  - Made `employeeId` nullable
  - Fixed `workstationId` type to varchar (was incorrectly uuid)

- `adherence-monitoring-system/backend/src/entities/employee-personal-info.entity.ts` (NEW)
  - Minimal entity for NT account resolution
  - Only includes `id`, `employeeId`, and `nt` fields

#### DTOs
- `adherence-monitoring-system/backend/src/dto/create-adherence-event.dto.ts`
  - Added required `nt` field with validation
  - Regex validation to prevent domain prefix

- `adherence-monitoring-system/backend/src/dto/register-workstation.dto.ts`
  - Removed `employee_id` requirement

#### Services
- `adherence-monitoring-system/backend/src/adherence/services/event-ingestion.service.ts`
  - Refactored to resolve `employee_id` from `nt` via `employee_personal_info`
  - Returns 409 Conflict for unmapped NT accounts
  - Added proper logging

#### Controllers
- `adherence-monitoring-system/backend/src/adherence/controllers/events.controller.ts`
  - Removed dependency on `req.employeeId`
  - Extracts `nt` from request body

- `adherence-monitoring-system/backend/src/admin/services/workstations.service.ts`
  - Removed employee validation from registration
  - Device-only registration

#### Guards
- `adherence-monitoring-system/backend/src/guards/workstation-auth.guard.ts`
  - Removed `employeeId` assignment (device-only auth)

#### Modules
- `adherence-monitoring-system/backend/src/adherence/adherence.module.ts`
  - Added `EmployeePersonalInfo` entity to TypeORM imports

### Desktop Agent Changes

#### Models
- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Shared/Models/AdherenceEvent.cs`
  - Added required `NtAccount` property

#### Storage
- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Shared/Storage/SQLiteEventBuffer.cs`
  - Added `nt_account` column to buffer schema
  - Migration logic for existing databases
  - Updated INSERT and SELECT queries

#### Helpers
- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Shared/Helpers/WindowsIdentityHelper.cs` (NEW)
  - `GetCurrentNtAccount()` method
  - `ExtractSamAccountName()` method
  - `IsValidNtAccount()` validation method

#### Monitors
- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Service/Capture/LoginLogoffMonitor.cs`
  - Extracts NT from Security Event Log
  - Falls back to current session

- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Service/Capture/IdleMonitor.cs`
  - Captures NT from current session

- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Service/Capture/ActiveWindowMonitor.cs`
  - Captures NT from current session

- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Service/Capture/SessionSwitchMonitor.cs`
  - Captures NT from current session

#### Upload Service
- `adherence-monitoring-system/desktop-agent/AdherenceAgent.Service/Upload/UploadService.cs`
  - Includes `nt` in API payload
  - Validates NT before upload
  - Handles 409 Conflict (unmapped NT) - no retry

---

## Deployment Steps

### Step 1: Database Migration

**Location:** `people-ops-system/database/migrations/037_add_nt_based_identification.sql`

**Execute:**
```bash
# Connect to PostgreSQL database
psql -U people_ops -d people_ops_dev -f people-ops-system/database/migrations/037_add_nt_based_identification.sql

# Or using pgAdmin / DBeaver
# Open and execute the migration file
```

**Verify:**
```sql
-- Check migration applied
SELECT column_name, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'agent_adherence_events' 
  AND column_name IN ('nt', 'employee_id');

-- Expected: nt exists, employee_id is_nullable = 'YES'
```

### Step 2: Backend Deployment

**Prerequisites:**
- Database migration completed
- Backend dependencies installed

**Steps:**
1. **Build backend:**
   ```bash
   cd adherence-monitoring-system/backend
   npm install
   npm run build
   ```

2. **Restart backend service:**
   ```bash
   # If using PM2
   pm2 restart adherence-backend
   
   # If using Docker
   docker-compose restart backend
   
   # If running directly
   npm run start:prod
   ```

3. **Verify backend started:**
   ```bash
   curl http://localhost:4001/health
   # Should return 200 OK
   ```

### Step 3: Desktop Agent Deployment

**Prerequisites:**
- Backend deployed and running
- Test workstation available

**Steps:**
1. **Build desktop agent:**
   ```bash
   cd adherence-monitoring-system/desktop-agent
   dotnet build
   ```

2. **Create MSI installer** (if not already created):
   ```bash
   # Follow MSI creation process
   # Include both service and tray application
   ```

3. **Deploy to test workstation:**
   - Install MSI on test workstation
   - Register workstation (device-only, no employee_id)
   - Configure API endpoint
   - Start service

4. **Verify agent running:**
   - Check Windows Services for "Adherence Agent Service"
   - Check system tray for agent icon
   - Review agent logs

### Step 4: Verify Deployment

**Quick Verification:**
1. Register a test workstation (should not require employee_id)
2. Generate a test event with valid NT account
3. Verify event ingested successfully
4. Check database for event with both `nt` and `employee_id`

**Detailed Testing:**
- Follow `TESTING_GUIDE_WEEK4_DAY1-2.md` for comprehensive testing

---

## Rollback Plan

If issues occur, rollback steps:

### 1. Database Rollback

```sql
BEGIN;

-- Remove nt column (if needed)
ALTER TABLE agent_adherence_events DROP COLUMN IF EXISTS nt;

-- Restore employee_id NOT NULL (if needed)
-- Note: This will fail if NULL values exist
ALTER TABLE agent_adherence_events ALTER COLUMN employee_id SET NOT NULL;

-- Restore workstation employee_id NOT NULL (if needed)
ALTER TABLE agent_workstation_configurations ALTER COLUMN employee_id SET NOT NULL;

-- Drop indexes
DROP INDEX IF EXISTS idx_events_nt_timestamp;
DROP INDEX IF EXISTS idx_employee_personal_info_nt;

COMMIT;
```

### 2. Backend Rollback

- Revert to previous backend version
- Restart backend service

### 3. Desktop Agent Rollback

- Uninstall current agent version
- Install previous agent version
- Restart service

---

## Important Notes

1. **Breaking Changes:**
   - Desktop agents MUST send `nt` field or events will be rejected (400)
   - Events with unmapped NT will be rejected (409)
   - Workstation registration no longer requires `employee_id`

2. **NT Account Format:**
   - Must be sam_account_name only (e.g., `z.salah.3613`)
   - No domain prefix (e.g., `OCTOPUS\z.salah.3613` is invalid)
   - Case-sensitive matching

3. **Employee Mapping:**
   - All active employees using the system MUST have NT account in `employee_personal_info`
   - Unmapped NT accounts will cause events to be rejected
   - Admin should monitor 409 Conflict responses for unmapped NT accounts

4. **Workstation Registration:**
   - Workstations are now device-only
   - Multiple employees can use same workstation
   - Employee identification happens at event ingestion via NT

5. **Migration Safety:**
   - Migration is backward compatible (makes columns nullable)
   - Existing events remain valid
   - New events require NT account

---

## Monitoring After Deployment

### Backend Logs

Monitor for:
- 409 Conflict responses (unmapped NT accounts)
- Event ingestion errors
- NT resolution failures

### Desktop Agent Logs

Monitor for:
- NT account capture warnings
- Upload failures (especially 409 responses)
- Events without NT account

### Database Queries

```sql
-- Check for events with unmapped NT (should be none after mapping)
SELECT COUNT(*) 
FROM agent_adherence_events 
WHERE nt IS NOT NULL 
  AND employee_id IS NULL;

-- Check NT account distribution
SELECT nt, COUNT(*) as event_count
FROM agent_adherence_events
WHERE nt IS NOT NULL
GROUP BY nt
ORDER BY event_count DESC;

-- Check for employees without NT accounts (may need mapping)
SELECT e.id, e.full_name_en, epi.nt
FROM employees e
LEFT JOIN employee_personal_info epi ON e.id = epi.employee_id
WHERE e.status = 'Active'
  AND epi.nt IS NULL;
```

---

## Support Contacts

- **Database Issues:** Database Administrator
- **Backend Issues:** Backend Development Team
- **Desktop Agent Issues:** Desktop Agent Development Team
- **NT Account Mapping:** HR/People Ops Team

---

## Success Metrics

After deployment, verify:
- ✅ All test events ingested successfully
- ✅ No 409 Conflict errors for mapped NT accounts
- ✅ Events correctly attributed to employees
- ✅ Multiple agents can use same workstation
- ✅ System performance acceptable
- ✅ No critical errors in logs

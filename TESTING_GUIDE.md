# Adherence Monitoring System - Testing Guide

## 📋 Overview

This guide covers testing the Week 2 implementation of the Adherence Monitoring System. The system consists of:
- **Adherence Backend Service** (Port 4001)
- **Adherence Admin Frontend** (Port 3001)
- **Nginx Reverse Proxy** (Port 80)
- **PostgreSQL Database** (shared with People Ops System)
- **Redis** (for rate limiting)

---

## 🚀 Pre-Testing Setup

### 1. Verify Services Are Running

On the VM, check all services are up:

```bash
cd adherence-monitoring-system
docker compose ps
```

Expected output should show all containers as "Up":
- `adherence-backend`
- `adherence-frontend`
- `adherence-nginx`
- `adherence-redis`

### 2. Check Service Logs

```bash
# Backend logs
docker compose logs -f adherence-backend

# Frontend logs
docker compose logs -f adherence-frontend

# Nginx logs
docker compose logs -f adherence-nginx
```

**Expected:**
- Backend: `🚀 Adherence Backend server running on port 4001`
- Frontend: `✓ Ready in XXms`
- Nginx: `Configuration complete; ready for start up`

---

## 🧪 Testing Scenarios

### **Test 1: Health Check Endpoints**

#### 1.1 Backend Health (Direct)
```bash
curl http://localhost:4001/health
```

**Expected Response:**
```json
{
  "status": "ok",
  "timestamp": "2025-12-05T...",
  "service": "Adherence Backend Service"
}
```

#### 1.2 Backend Health (via Nginx)
```bash
curl http://adherence-server/health
# or
curl http://10.20.13.82/health
```

**Expected:** Same as above

#### 1.3 Admin Health Endpoint
```bash
# Requires Authorization header (any Bearer token works in Week 2)
curl http://adherence-server/api/adherence/admin/health \
  -H "Authorization: Bearer placeholder-token"
```

**Expected Response:**
```json
{
  "status": "healthy",
  "timestamp": "2025-12-05T...",
  "services": {
    "database": {
      "status": "connected",
      "response_time_ms": 15
    },
    "redis": {
      "status": "unknown",
      "response_time_ms": 0
    }
  },
  "metrics": {
    "events_ingested_last_hour": 0,
    "events_ingested_last_24h": 0,
    "active_workstations": 0
  }
}
```

**Note:** In Week 2, any Bearer token is accepted (placeholder auth). In Week 5, this will require a valid JWT token with WFM_Admin role.

---

### **Test 2: Workstation Registration (Admin API)**

**Note:** This requires JWT authentication. For now, we'll test the endpoint structure. Full JWT implementation is in Week 5.

#### 2.1 Get a Valid Active Employee ID

**Important:** The `employee_id` must exist in the `employees` table AND have `status = 'Active'`. Get a valid active employee ID first:

```bash
# Option 1: Query database directly (only active employees)
psql -h <DATABASE_HOST> -U <DATABASE_USERNAME> -d <DATABASE_NAME> -c "SELECT id, hr_id, first_name, last_name, status FROM employees WHERE status = 'Active' LIMIT 5;"

# Option 2: Using Docker (if database is in container)
docker compose exec postgres psql -U <DATABASE_USERNAME> -d <DATABASE_NAME> -c "SELECT id, hr_id, first_name, last_name, status FROM employees WHERE status = 'Active' LIMIT 5;"
```

**Note:** Only employees with `status = 'Active'` can have workstations registered. Inactive or terminated employees will be rejected with a clear error message.

**Example output:**
```
                  id                  | hr_id | first_name | last_name
--------------------------------------+-------+------------+-----------
 123e4567-e89b-12d3-a456-426614174000 |  1001 | John       | Doe
 223e4567-e89b-12d3-a456-426614174001 |  1002 | Jane       | Smith
```

#### 2.2 Register Workstation

```bash
# Replace <VALID_EMPLOYEE_ID> with an actual employee ID from step 2.1
curl -X POST http://adherence-server/api/adherence/admin/workstations/register \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer placeholder-token" \
  -d '{
    "employee_id": "<VALID_EMPLOYEE_ID>",
    "workstation_name": "DESKTOP-TEST-001",
    "os_version": "Windows 11 Pro",
    "agent_version": "1.0.0",
    "notes": "Test workstation"
  }'
```

**Expected Response (Week 2 with placeholder auth):**
```json
{
  "workstation_id": "550e8400-e29b-41d4-a716-446655440000",
  "api_key": "xK9mP2qR7vT3wY8zA1bC4dE6fG9hJ0kL2mN5pQ8sT1vW4xY7z",
  "message": "Workstation registered successfully",
  "warning": "These credentials will not be shown again. Store them securely."
}
```

**Note:** 
- In Week 2, any Bearer token works (placeholder auth)
- In Week 5, will require valid JWT with WFM_Admin role
- **Valid fields:** `employee_id` (required), `workstation_name`, `os_version`, `agent_version`, `notes` (all optional)
- **Invalid fields:** `workstation_mac_address` is not supported

---

### **Test 3: Event Ingestion (Desktop Agent API)**

**Note:** Requires workstation registration first (Test 2) to get API key.

#### 3.1 Get Workstation Configuration

```bash
curl -X GET http://adherence-server/api/adherence/workstation/config \
  -H "X-API-Key: <API_KEY>" \
  -H "X-Workstation-ID: <WORKSTATION_ID>"
```

**Expected Response:**
```json
{
  "workstation_id": "uuid",
  "application_classifications": [
    {
      "application_name": "chrome.exe",
      "classification": "WORK",
      "category": "BROWSER"
    }
  ],
  "break_schedules": [],
  "settings": {
    "event_batch_size": 50,
    "sync_interval_seconds": 60
  }
}
```

**Current (Week 2):** Will return 401 if API key is invalid (WorkstationAuthGuard is implemented)

#### 3.2 Ingest Single Event

```bash
curl -X POST http://adherence-server/api/adherence/events \
  -H "Content-Type: application/json" \
  -H "X-API-Key: <API_KEY>" \
  -H "X-Workstation-ID: <WORKSTATION_ID>" \
  -d '{
    "event_type": "APPLICATION_FOCUS",
    "application_name": "chrome.exe",
    "window_title": "Google - Chrome",
    "timestamp": "2025-12-05T10:30:00Z"
  }'
```

**Expected Response:**
```json
{
  "success": true,
  "message": "Event ingested successfully",
  "events_processed": 1,
  "events_failed": 0
}
```

**Current (Week 2):** Will return 401 if API key is invalid

#### 3.3 Ingest Batch Events

```bash
curl -X POST http://adherence-server/api/adherence/events \
  -H "Content-Type: application/json" \
  -H "X-API-Key: <API_KEY>" \
  -H "X-Workstation-ID: <WORKSTATION_ID>" \
  -d '{
    "events": [
      {
        "event_type": "APPLICATION_FOCUS",
        "application_name": "chrome.exe",
        "window_title": "Google - Chrome",
        "timestamp": "2025-12-05T10:30:00Z"
      },
      {
        "event_type": "IDLE_START",
        "timestamp": "2025-12-05T10:35:00Z"
      }
    ]
  }'
```

**Expected Response:**
```json
{
  "success": true,
  "message": "Events ingested successfully",
  "events_processed": 2,
  "events_failed": 0
}
```

---

### **Test 4: Frontend Access**

#### 4.1 Access Admin Dashboard

Open browser and navigate to:
```
http://adherence-server/admin/dashboard
# or
http://10.20.13.82/admin/dashboard
```

**Expected:**
- Admin layout with sidebar navigation
- Dashboard page loads (may show "No data" - expected for Week 2)

#### 4.2 Access Other Admin Pages

Test all admin routes:
- `/admin/dashboard` - Main dashboard
- `/admin/devices` - Device management
- `/admin/agents` - Agent sync status
- `/admin/config` - Configuration management
- `/admin/health` - System health

**Expected:** All pages should load without errors (may show placeholder content)

---

### **Test 5: Database Verification**

#### 5.1 Check Tables Exist

Connect to PostgreSQL and verify tables:

```bash
# On VM, connect to database
docker compose exec -it adherence-db psql -U postgres -d people_ops_db

# List adherence-related tables
\dt agent_adherence*

# Expected tables:
# - agent_adherence_events
# - agent_adherence_summaries
# - agent_adherence_exceptions
# - agent_workstation_configurations
# - application_classifications
```

#### 5.2 Verify Event Storage (After Test 3)

```sql
-- Check if events are being stored
SELECT COUNT(*) FROM agent_adherence_events;

-- View recent events
SELECT 
  event_type,
  application_name,
  timestamp,
  employee_id,
  workstation_id
FROM agent_adherence_events
ORDER BY timestamp DESC
LIMIT 10;
```

---

### **Test 6: Error Handling**

#### 6.1 Missing API Key

```bash
curl -X POST http://adherence-server/api/adherence/events \
  -H "Content-Type: application/json" \
  -d '{"event_type": "APPLICATION_FOCUS", "application_name": "chrome.exe"}'
```

**Expected:** `401 Unauthorized` with error message

#### 6.2 Invalid API Key

```bash
curl -X POST http://adherence-server/api/adherence/events \
  -H "Content-Type: application/json" \
  -H "X-API-Key: invalid-key" \
  -H "X-Workstation-ID: invalid-id" \
  -d '{"event_type": "APPLICATION_FOCUS", "application_name": "chrome.exe"}'
```

**Expected:** `401 Unauthorized` with error message

#### 6.3 Invalid Request Body

```bash
curl -X POST http://adherence-server/api/adherence/events \
  -H "Content-Type: application/json" \
  -H "X-API-Key: <VALID_KEY>" \
  -H "X-Workstation-ID: <VALID_ID>" \
  -d '{"invalid_field": "value"}'
```

**Expected:** `400 Bad Request` with validation errors

---

## ✅ Week 2 Testing Checklist

### Backend Services
- [ ] Backend service starts without errors
- [ ] Health endpoint returns 200 OK
- [ ] Admin health endpoint returns 200 OK
- [ ] All modules load correctly (AdherenceModule, AdminModule)

### API Endpoints (Structure)
- [ ] `POST /api/adherence/events` - Endpoint exists (auth required)
- [ ] `GET /api/adherence/workstation/config` - Endpoint exists (auth required)
- [ ] `GET /api/adherence/admin/workstations` - Endpoint exists (JWT required)
- [ ] `POST /api/adherence/admin/workstations/register` - Endpoint exists (JWT required)
- [ ] `GET /api/adherence/admin/health` - Endpoint exists

### Authentication Guards
- [ ] `WorkstationAuthGuard` - Rejects requests without API key
- [ ] `JwtAuthGuard` - Placeholder exists (full implementation Week 5)
- [ ] `RolesGuard` - Placeholder exists (full implementation Week 5)

### Frontend
- [ ] Frontend service starts without errors
- [ ] All admin routes load (`/admin/*`)
- [ ] Admin layout with sidebar navigation
- [ ] No console errors in browser

### Database
- [ ] All entity tables exist in database
- [ ] TypeORM can connect to shared database
- [ ] Entities are properly mapped

### Docker & Infrastructure
- [ ] All containers start successfully
- [ ] Nginx routes traffic correctly
- [ ] Services can communicate via Docker network
- [ ] Redis is accessible for rate limiting

---

## 🔍 Troubleshooting

### Backend Won't Start
```bash
# Check logs
docker compose logs adherence-backend

# Common issues:
# - Database connection failed → Check DB credentials in .env
# - Port already in use → Check if another service is using port 4001
# - Missing dependencies → Run `npm install` in backend directory
```

### Frontend Won't Start
```bash
# Check logs
docker compose logs adherence-frontend

# Common issues:
# - Build failed → Check Next.js build errors
# - Port conflict → Check port 3001
# - Missing dependencies → Run `npm install` in frontend directory
```

### API Returns 401 Unauthorized
- **Expected for Week 2:** JWT endpoints will return 401 (not fully implemented)
- **WorkstationAuthGuard:** Requires valid API key from registered workstation
- **Solution:** Register workstation first (Test 2) or wait for Week 5 JWT implementation

### Database Connection Issues
```bash
# Verify database is running
docker compose ps adherence-db

# Check connection string in .env
# Should point to shared People Ops database
```

---

## 📝 Notes for Week 5

The following will be fully implemented in Week 5:
- ✅ Complete JWT authentication for Admin API
- ✅ Complete workstation registration flow
- ✅ Adherence calculation engine
- ✅ Exception management (in People Ops Backend)
- ✅ Full frontend-backend integration
- ✅ Per-workstation rate limiting (custom throttler)

For now, focus on verifying:
- ✅ Service startup and health
- ✅ Endpoint structure and routing
- ✅ Database schema and entities
- ✅ Frontend routing and layout
- ✅ Error handling and validation

---

## 🎯 Quick Test Commands

```bash
# 1. Health check
curl http://adherence-server/health

# 2. Admin health
curl http://adherence-server/api/adherence/admin/health

# 3. Check frontend
curl -I http://adherence-server/admin/dashboard

# 4. View logs
docker compose logs -f adherence-backend

# 5. Restart services
docker compose restart
```

---

**Last Updated:** December 2025  
**Week:** 2 (Foundation & Structure)  
**Next:** Week 5 (Full Implementation)


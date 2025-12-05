# Database Migrations Guide

## Overview

The Adherence Monitoring System uses PostgreSQL with TypeORM. Since `synchronize: false` is set (for production safety), database schema changes must be applied manually via SQL migrations.

## Migration: Add registration_source Column

### Issue
The `agent_workstation_configurations` table is missing the `registration_source` column, causing errors when registering workstations.

### Solution

Run the following SQL migration on your PostgreSQL database:

```sql
-- Add the registration_source column
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

-- Add comment to column
COMMENT ON COLUMN agent_workstation_configurations.registration_source IS 'Source of registration: ADMIN, SELF_SERVICE (future)';
```

### How to Apply

#### Option 1: Using Docker Exec (Recommended)

```bash
# Connect to the database container
docker compose exec adherence-db psql -U postgres -d people_ops_db

# Then run the SQL:
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

# Exit psql
\q
```

#### Option 2: Using psql Directly

```bash
# If you have psql installed on the VM
psql -h localhost -U postgres -d people_ops_db

# Then run the SQL:
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

# Exit psql
\q
```

#### Option 3: Using Docker Run (One-liner)

```bash
docker compose exec -T adherence-db psql -U postgres -d people_ops_db <<EOF
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';
EOF
```

### Verify Migration

After running the migration, verify the column exists:

```sql
-- Check if column exists
SELECT column_name, data_type, column_default
FROM information_schema.columns
WHERE table_name = 'agent_workstation_configurations'
  AND column_name = 'registration_source';
```

Expected output:
```
     column_name      | data_type | column_default
----------------------+-----------+----------------
 registration_source  | character varying | 'ADMIN'
```

### After Migration

1. Restart the backend service:
   ```bash
   docker compose restart adherence-backend
   ```

2. Test the workstation registration endpoint again:
   ```bash
   curl -X POST http://adherence-server/api/adherence/admin/workstations/register \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer placeholder-token" \
     -d '{
       "employee_id": "550e8400-e29b-41d4-a716-446655440000",
       "workstation_name": "DESKTOP-TEST-001",
       "os_version": "Windows 11 Pro",
       "agent_version": "1.0.0",
       "notes": "Test workstation"
     }'
   ```

---

## Future Migrations

When adding new entities or columns:

1. Create a migration SQL file in `backend/migrations/`
2. Document the migration in this file
3. Test on development database first
4. Apply to production during maintenance window

---

**Last Updated:** December 2025


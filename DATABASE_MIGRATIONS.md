# Database Migrations Guide

## Overview

**Important:** The Adherence Monitoring System shares the PostgreSQL database with the People Ops System. All database migrations must be applied to the **shared database** (`people_ops_db` or `people_ops_staging`/`people_ops_prod`).

Since `synchronize: false` is set in TypeORM config (for production safety), database schema changes must be applied manually via SQL migrations.

**Migration Location:** Migrations should be added to `people-ops-system/database/migrations/` and numbered sequentially.

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

**Note:** The database is shared with people-ops-system. Connect to the shared database using the credentials from your `.env` file (DATABASE_HOST, DATABASE_NAME, DATABASE_USERNAME, DATABASE_PASSWORD).

#### Option 1: Add Migration to People Ops System (Recommended)

The migration should be added as `027_add_registration_source_to_workstations.sql` in `people-ops-system/database/migrations/`:

```sql
-- Migration 027: Add registration_source column to agent_workstation_configurations
-- Date: 2025-12-05
-- Description: Adds registration_source column to track how workstations were registered

BEGIN;

ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

COMMENT ON COLUMN agent_workstation_configurations.registration_source IS 'Source of registration: ADMIN, SELF_SERVICE (future)';

COMMIT;
```

Then apply it:
```bash
# From people-ops-system directory
psql -h <DATABASE_HOST> -U <DATABASE_USERNAME> -d <DATABASE_NAME> -f database/migrations/027_add_registration_source_to_workstations.sql
```

#### Option 2: Direct SQL (Quick Fix)

```bash
# Connect to shared database (use your actual database credentials)
psql -h <DATABASE_HOST> -U <DATABASE_USERNAME> -d <DATABASE_NAME>

# Run the migration
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

COMMENT ON COLUMN agent_workstation_configurations.registration_source IS 'Source of registration: ADMIN, SELF_SERVICE (future)';

# Exit
\q
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


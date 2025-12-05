-- Migration: Add registration_source column to agent_workstation_configurations table
-- Date: 2025-12-05
-- Description: Adds registration_source column to track how workstations were registered

-- Add the registration_source column
ALTER TABLE agent_workstation_configurations
ADD COLUMN IF NOT EXISTS registration_source VARCHAR(50) DEFAULT 'ADMIN';

-- Add comment to column
COMMENT ON COLUMN agent_workstation_configurations.registration_source IS 'Source of registration: ADMIN, SELF_SERVICE (future)';


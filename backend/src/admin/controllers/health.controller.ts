import { Controller, Get, UseGuards } from '@nestjs/common';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { HealthService } from '../services/health.service';

/**
 * HealthController
 * 
 * Admin endpoints for system health monitoring.
 * 
 * Authentication: JWT + WFM_Admin role required (per API specification).
 * The health endpoint returns sensitive metrics (event counts, database status, etc.)
 * and should only be accessible to administrators.
 * 
 * For Week 2: Works with placeholder JWT auth (any Bearer token accepted).
 * For Week 5: Will require full JWT validation.
 */
@Controller('admin/health')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('System_Admin')
export class HealthController {
  constructor(private healthService: HealthService) {}

  /**
   * GET /api/adherence/admin/health
   * 
   * Get system health status with detailed metrics.
   * Requires: Authorization header with Bearer token (any token works in Week 2).
   */
  @Get()
  async getSystemHealth() {
    return this.healthService.getSystemHealth();
  }
}


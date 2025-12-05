import { Controller, Get, UseGuards } from '@nestjs/common';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { HealthService } from '../services/health.service';

/**
 * HealthController
 * 
 * Admin endpoints for system health monitoring.
 * Protected by JWT authentication and WFM_Admin role.
 */
@Controller('admin/health')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('WFM_Admin')
export class HealthController {
  constructor(private healthService: HealthService) {}

  /**
   * GET /api/adherence/admin/health
   * 
   * Get system health status.
   */
  @Get()
  async getSystemHealth() {
    return this.healthService.getSystemHealth();
  }
}


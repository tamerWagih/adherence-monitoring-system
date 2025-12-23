import { Controller, Get, UseGuards } from '@nestjs/common';
import { ApiBearerAuth, ApiOperation, ApiResponse, ApiTags } from '@nestjs/swagger';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { HealthService } from '../services/health.service';

/**
 * HealthController
 * 
 * Admin endpoints for system health monitoring.
 * 
 * Authentication: JWT + System_Admin role required.
 * The health endpoint returns sensitive metrics (event counts, database status, etc.)
 * and should only be accessible to system administrators.
 * System_Admin has access to all endpoints by default.
 */
@ApiTags('Admin')
@Controller('admin/health')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('System_Admin')
@ApiBearerAuth('JWT-auth')
export class HealthController {
  constructor(private healthService: HealthService) {}

  /**
   * GET /api/adherence/admin/health
   * 
   * Get system health status with detailed metrics.
   * Requires: Authorization header with Bearer token (any token works in Week 2).
   */
  @Get()
  @ApiOperation({ summary: 'System health (admin)' })
  @ApiResponse({ status: 200, description: 'Health status returned' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
  async getSystemHealth() {
    return this.healthService.getSystemHealth();
  }
}


import { Controller, Get } from '@nestjs/common';
import { HealthService } from '../services/health.service';

/**
 * HealthController
 * 
 * Admin endpoints for system health monitoring.
 * 
 * Note: Health endpoint is public for monitoring purposes.
 * In production, consider adding IP whitelist or basic auth.
 */
@Controller('admin/health')
export class HealthController {
  constructor(private healthService: HealthService) {}

  /**
   * GET /api/adherence/admin/health
   * 
   * Get system health status.
   * Public endpoint for monitoring (no authentication required).
   */
  @Get()
  async getSystemHealth() {
    return this.healthService.getSystemHealth();
  }
}


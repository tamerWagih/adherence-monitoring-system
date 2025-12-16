import {
  Controller,
  Get,
  UseGuards,
  Request,
  Query,
} from '@nestjs/common';
import { ThrottlerGuard } from '@nestjs/throttler';
import { WorkstationAuthGuard } from '../../guards/workstation-auth.guard';
import { WorkstationConfigService } from '../services/workstation-config.service';

/**
 * WorkstationConfigController
 * 
 * Provides workstation configuration to Desktop Agents.
 * Protected by WorkstationAuthGuard (API key authentication).
 */
@Controller('workstation/config')
@UseGuards(WorkstationAuthGuard, ThrottlerGuard)
export class WorkstationConfigController {
  constructor(private configService: WorkstationConfigService) {}

  /**
   * GET /api/adherence/workstation/config
   * 
   * Get workstation configuration (classification rules, break schedules, settings).
   * 
   * Headers Required:
   * - X-API-Key: API key
   * - X-Workstation-ID: Workstation ID
   * 
   * Query Parameters (Optional):
   * - nt: Windows NT account (sam_account_name) for break schedule resolution
   */
  @Get()
  async getConfig(@Request() req: any, @Query('nt') nt?: string) {
    const workstationId = req.workstation.workstationId;
    return this.configService.getWorkstationConfig(workstationId, nt);
  }
}


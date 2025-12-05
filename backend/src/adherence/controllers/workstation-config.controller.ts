import {
  Controller,
  Get,
  UseGuards,
  Request,
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
   */
  @Get()
  async getConfig(@Request() req: any) {
    const workstationId = req.workstation.workstationId;
    return this.configService.getWorkstationConfig(workstationId);
  }
}


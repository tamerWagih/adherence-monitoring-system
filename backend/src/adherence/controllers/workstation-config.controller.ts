import {
  Controller,
  Get,
  UseGuards,
  Request,
  Query,
} from '@nestjs/common';
import { ThrottlerGuard } from '@nestjs/throttler';
import { ApiOperation, ApiQuery, ApiResponse, ApiSecurity, ApiTags } from '@nestjs/swagger';
import { WorkstationAuthGuard } from '../../guards/workstation-auth.guard';
import { WorkstationConfigService } from '../services/workstation-config.service';

/**
 * WorkstationConfigController
 * 
 * Provides workstation configuration to Desktop Agents.
 * Protected by WorkstationAuthGuard (API key authentication).
 */
@ApiTags('Events')
@Controller('workstation/config')
@UseGuards(WorkstationAuthGuard, ThrottlerGuard)
@ApiSecurity('API-Key')
@ApiSecurity('Workstation-ID')
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
  @ApiOperation({ summary: 'Get workstation configuration (Desktop Agent)' })
  @ApiQuery({ name: 'nt', required: false, type: String, description: 'Optional NT account for break schedule resolution' })
  @ApiResponse({ status: 200, description: 'Configuration returned' })
  @ApiResponse({ status: 401, description: 'Invalid workstation credentials' })
  async getConfig(@Request() req: any, @Query('nt') nt?: string) {
    const workstationId = req.workstation.workstationId;
    return this.configService.getWorkstationConfig(workstationId, nt);
  }
}


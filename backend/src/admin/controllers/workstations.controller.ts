import {
  Controller,
  Get,
  Post,
  Body,
  Param,
  Query,
  UseGuards,
  HttpCode,
  HttpStatus,
} from '@nestjs/common';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { WorkstationsService } from '../services/workstations.service';
import { RegisterWorkstationDto } from '../../dto/register-workstation.dto';

/**
 * WorkstationsController
 * 
 * Admin endpoints for workstation management.
 * Protected by JWT authentication and WFM_Admin role.
 */
@Controller('admin/workstations')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('WFM_Admin')
export class WorkstationsController {
  constructor(private workstationsService: WorkstationsService) {}

  /**
   * GET /api/adherence/admin/workstations
   * 
   * List all workstations with status.
   */
  @Get()
  async listWorkstations(@Query() query: any) {
    return this.workstationsService.listWorkstations(query);
  }

  /**
   * GET /api/adherence/admin/workstations/status
   * 
   * Get registration status dashboard data.
   */
  @Get('status')
  async getRegistrationStatus(@Query() query: any) {
    return this.workstationsService.getRegistrationStatus(query);
  }

  /**
   * POST /api/adherence/admin/workstations/register
   * 
   * Register a new workstation for an agent.
   * Returns workstation_id and api_key (shown once only).
   */
  @Post('register')
  @HttpCode(HttpStatus.CREATED)
  async registerWorkstation(@Body() dto: RegisterWorkstationDto) {
    return this.workstationsService.registerWorkstation(dto);
  }

  /**
   * POST /api/adherence/admin/workstations/:id/revoke
   * 
   * Revoke/deactivate a workstation.
   */
  @Post(':id/revoke')
  async revokeWorkstation(
    @Param('id') workstationId: string,
    @Body() body: { reason?: string },
  ) {
    return this.workstationsService.revokeWorkstation(workstationId, body.reason);
  }
}


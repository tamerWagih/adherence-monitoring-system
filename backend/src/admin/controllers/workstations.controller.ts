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
import {
  ApiBearerAuth,
  ApiBody,
  ApiOperation,
  ApiParam,
  ApiQuery,
  ApiResponse,
  ApiTags,
} from '@nestjs/swagger';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { WorkstationsService } from '../services/workstations.service';
import { RegisterWorkstationDto } from '../../dto/register-workstation.dto';

/**
 * WorkstationsController
 * 
 * Admin endpoints for workstation management.
 * Protected by JWT authentication and System_Admin role.
 * System_Admin has access to all endpoints by default.
 */
@ApiTags('Admin')
@Controller('admin/workstations')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('System_Admin')
@ApiBearerAuth('JWT-auth')
export class WorkstationsController {
  constructor(private workstationsService: WorkstationsService) {}

  /**
   * GET /api/adherence/admin/workstations
   * 
   * List all workstations with status.
   */
  @Get()
  @ApiOperation({ summary: 'List workstations (admin)' })
  @ApiQuery({ name: 'status', required: false, type: String })
  @ApiQuery({ name: 'employeeId', required: false, type: String })
  @ApiResponse({ status: 200, description: 'Workstations list returned' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
  async listWorkstations(@Query() query: any) {
    return this.workstationsService.listWorkstations(query);
  }

  /**
   * GET /api/adherence/admin/workstations/status
   * 
   * Get registration status dashboard data.
   */
  @Get('status')
  @ApiOperation({ summary: 'Workstation registration status (admin dashboard)' })
  @ApiResponse({ status: 200, description: 'Registration status returned' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
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
  @ApiOperation({ summary: 'Register workstation (admin)' })
  @ApiBody({ type: RegisterWorkstationDto })
  @ApiResponse({ status: 201, description: 'Workstation registered (API key returned once)' })
  @ApiResponse({ status: 400, description: 'Invalid request body' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
  async registerWorkstation(@Body() dto: RegisterWorkstationDto) {
    return this.workstationsService.registerWorkstation(dto);
  }

  /**
   * POST /api/adherence/admin/workstations/:id/revoke
   * 
   * Revoke/deactivate a workstation.
   */
  @Post(':id/revoke')
  @ApiOperation({ summary: 'Revoke workstation (admin)' })
  @ApiParam({ name: 'id', required: true, type: String, description: 'Workstation UUID' })
  @ApiBody({
    required: false,
    schema: {
      type: 'object',
      properties: {
        reason: { type: 'string', example: 'Workstation replaced' },
      },
    },
  })
  @ApiResponse({ status: 200, description: 'Workstation revoked' })
  @ApiResponse({ status: 400, description: 'Invalid input' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
  async revokeWorkstation(
    @Param('id') workstationId: string,
    @Body() body?: { reason?: string },
  ) {
    return this.workstationsService.revokeWorkstation(
      workstationId,
      body?.reason,
    );
  }
}


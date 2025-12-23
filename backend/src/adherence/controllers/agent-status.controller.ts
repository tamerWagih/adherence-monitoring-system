import { Controller, Get, UseGuards, Request, Query, Logger } from '@nestjs/common';
import { ApiBearerAuth, ApiOperation, ApiQuery, ApiResponse, ApiTags } from '@nestjs/swagger';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { AgentStatusService } from '../services/agent-status.service';

/**
 * AgentStatusController
 * 
 * Agent-facing endpoint for status information.
 * Checks NT account mapping and returns adherence data if mapped.
 * 
 * Note: Currently uses placeholder JWT auth. In Week 5, NT account will be
 * extracted from JWT token. For now, accepts NT as query parameter for testing.
 */
@ApiTags('Agent')
@Controller('agent')
@UseGuards(JwtAuthGuard)
@ApiBearerAuth('JWT-auth')
export class AgentStatusController {
  private readonly logger = new Logger(AgentStatusController.name);

  constructor(private agentStatusService: AgentStatusService) {}

  /**
   * GET /api/adherence/agent/status
   * 
   * Get agent status by NT account.
   * 
   * Authentication: JWT (agent-facing, not admin)
   * 
   * Query Parameters (for testing until Week 5):
   * - nt: Windows NT account (sam_account_name, e.g., "z.salah.3613")
   * 
   * TODO Week 5: Extract NT account from JWT token instead of query parameter
   * 
   * Response:
   * - If NT not mapped: Returns warning message
   * - If NT mapped: Returns workstation status and adherence data
   */
  @Get('status')
  @ApiOperation({ summary: 'Get agent status (agent-facing)' })
  @ApiQuery({ name: 'nt', required: false, type: String, description: 'Windows NT account (temporary for testing)' })
  @ApiResponse({ status: 200, description: 'Agent status returned' })
  @ApiResponse({ status: 401, description: 'Unauthorized' })
  async getAgentStatus(@Request() req: any, @Query('nt') ntAccount?: string) {
    try {
      // TODO Week 5: Extract NT account from JWT token
      // For now, accept as query parameter for testing
      // const ntAccount = req.user.nt; // Will be available after JWT implementation

      if (!ntAccount) {
        // In Week 5, this will come from JWT token
        // For now, return error if not provided
        return {
          ntMapped: false,
          warning: 'NT account not provided. In Week 5, this will be extracted from JWT token.',
          workstation: null,
          adherence: null,
        };
      }

      return await this.agentStatusService.getAgentStatusByNt(ntAccount);
    } catch (error) {
      this.logger.error(`Error getting agent status for NT: ${ntAccount}`, error);
      throw error;
    }
  }
}

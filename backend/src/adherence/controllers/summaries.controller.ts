import {
  Controller,
  Post,
  Get,
  Body,
  Query,
  Param,
  UseGuards,
  HttpCode,
  HttpStatus,
  Logger,
  BadRequestException,
  NotFoundException,
  ParseUUIDPipe,
} from '@nestjs/common';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { AdherenceCalculationService } from '../services/adherence-calculation.service';
import { SummariesService, SummariesQueryDto } from '../services/summaries.service';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { AgentSchedule } from '../../entities/agent-schedule.entity';
import { CacheService } from '../../common/cache.service';

/**
 * SummariesController
 * 
 * Handles adherence summary calculation and retrieval.
 * Protected by JWT authentication and WFM_Admin role.
 */
@Controller('summaries')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('WFM_Admin')
export class SummariesController {
  private readonly logger = new Logger(SummariesController.name);

  constructor(
    private adherenceCalculationService: AdherenceCalculationService,
    private summariesService: SummariesService,
    @InjectRepository(AgentSchedule)
    private scheduleRepo: Repository<AgentSchedule>,
    private cacheService: CacheService,
  ) {}

  /**
   * POST /api/adherence/summaries/calculate
   * 
   * Manually trigger adherence calculation for a specific employee and date.
   * 
   * Query Parameters:
   * - employeeId: Employee UUID (required)
   * - date: Date in YYYY-MM-DD format (required, defaults to yesterday)
   */
  @Post('calculate')
  @HttpCode(HttpStatus.OK)
  async calculateAdherence(
    @Query('employeeId') employeeId?: string,
    @Query('date') dateStr?: string,
  ) {
    if (!employeeId) {
      throw new BadRequestException('employeeId query parameter is required');
    }

    // Parse date (default to yesterday in Egypt timezone)
    let date: Date;
    if (dateStr) {
      date = new Date(dateStr);
      if (isNaN(date.getTime())) {
        throw new BadRequestException(
          `Invalid date format: ${dateStr}. Expected YYYY-MM-DD format.`,
        );
      }
    } else {
      // Default to yesterday in Egypt timezone
      const now = new Date();
      const egyptOffset = 2 * 60; // +02:00 in minutes
      const utcTime = now.getTime() + now.getTimezoneOffset() * 60000;
      const egyptTime = new Date(utcTime + egyptOffset * 60000);
      egyptTime.setDate(egyptTime.getDate() - 1);
      date = egyptTime;
    }

    // Get schedule for the employee and date
    const dateStrFormatted = date.toISOString().split('T')[0];
    const schedules = await this.scheduleRepo.query(
      `
      SELECT * FROM agent_schedules
      WHERE employee_id = $1
        AND schedule_date = $2::date
        AND is_break = $3
        AND is_confirmed = $4
      ORDER BY shift_start ASC
      LIMIT 1
      `,
      [employeeId, dateStrFormatted, false, true],
    );

    if (schedules.length === 0) {
      throw new BadRequestException(
        `No schedule found for employee ${employeeId} on date ${dateStrFormatted}`,
      );
    }

    const scheduleRow = schedules[0];
    const schedule: AgentSchedule = {
      id: scheduleRow.id,
      employeeId: scheduleRow.employee_id,
      scheduleDate: scheduleRow.schedule_date,
      shiftStart: scheduleRow.shift_start,
      shiftEnd: scheduleRow.shift_end,
      isBreak: scheduleRow.is_break,
      breakDuration: scheduleRow.break_duration,
      isConfirmed: scheduleRow.is_confirmed,
    };

    try {
      const summary = await this.adherenceCalculationService.calculateDailyAdherence(
        employeeId,
        date,
        schedule,
      );

      this.logger.log(
        `Successfully calculated adherence for employee ${employeeId} on ${dateStrFormatted}`,
      );

      // Invalidate cache for this employee and date
      await Promise.all([
        this.cacheService.invalidatePattern(`summaries:*`),
        this.cacheService.invalidatePattern(`reports:*`),
        this.cacheService.invalidatePattern(`adherence-read:*`),
        this.cacheService.invalidatePattern(`agent-status:byEmployeeId:*${employeeId}*`),
      ]);

      return {
        success: true,
        message: 'Adherence calculated successfully',
        summary: {
          id: summary.id,
          employeeId: summary.employeeId,
          scheduleDate: summary.scheduleDate,
          adherencePercentage: summary.adherencePercentage,
          calculatedAt: summary.calculatedAt,
        },
      };
    } catch (error) {
      this.logger.error(
        `Failed to calculate adherence for employee ${employeeId}: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * POST /api/adherence/summaries/batch-calculate
   * 
   * Batch calculate adherence for all employees with schedules on a specific date.
   * 
   * Query Parameters:
   * - date: Date in YYYY-MM-DD format (required, defaults to yesterday)
   * - batchSize: Number of employees to process at a time (optional, default: 50)
   */
  @Post('batch-calculate')
  @HttpCode(HttpStatus.OK)
  async batchCalculateAdherence(
    @Query('date') dateStr?: string,
    @Query('batchSize') batchSizeStr?: string,
  ) {
    // Parse date (default to yesterday in Egypt timezone)
    let date: Date;
    if (dateStr) {
      date = new Date(dateStr);
      if (isNaN(date.getTime())) {
        throw new BadRequestException(
          `Invalid date format: ${dateStr}. Expected YYYY-MM-DD format.`,
        );
      }
    } else {
      // Default to yesterday in Egypt timezone
      const now = new Date();
      const egyptOffset = 2 * 60; // +02:00 in minutes
      const utcTime = now.getTime() + now.getTimezoneOffset() * 60000;
      const egyptTime = new Date(utcTime + egyptOffset * 60000);
      egyptTime.setDate(egyptTime.getDate() - 1);
      date = egyptTime;
    }

    const batchSize = batchSizeStr ? parseInt(batchSizeStr, 10) : 50;
    if (isNaN(batchSize) || batchSize < 1) {
      throw new BadRequestException('batchSize must be a positive integer');
    }

    try {
      const result = await this.adherenceCalculationService.batchCalculateAdherence(
        date,
        batchSize,
      );

      this.logger.log(
        `Batch calculation completed for ${date.toISOString().split('T')[0]}: ${result.processed} processed, ${result.failed} failed`,
      );

      // Invalidate cache for the calculated date
      const dateStr = date.toISOString().split('T')[0];
      this.logger.log(`Invalidating cache for date: ${dateStr}`);
      
      await Promise.all([
        this.cacheService.invalidatePattern(`summaries:*`),
        this.cacheService.invalidatePattern(`reports:*`),
        this.cacheService.invalidatePattern(`adherence-read:*`),
        this.cacheService.invalidatePattern(`agent-status:*`),
      ]);

      this.logger.log('Cache invalidation completed after batch calculation');

      return {
        success: true,
        message: 'Batch calculation completed',
        date: dateStr,
        processed: result.processed,
        failed: result.failed,
        errors: result.errors,
      };
    } catch (error) {
      this.logger.error(
        `Failed to batch calculate adherence: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * GET /api/adherence/summaries
   * 
   * List adherence summaries with filters and pagination.
   * Admin-facing endpoint (no RBAC filtering - shows all data).
   * 
   * Query Parameters:
   * - employeeId: Filter by employee UUID (optional)
   * - startDate: Start date (YYYY-MM-DD) (optional)
   * - endDate: End date (YYYY-MM-DD) (optional)
   * - department: Filter by department (optional)
   * - minAdherence: Minimum adherence percentage (optional)
   * - maxAdherence: Maximum adherence percentage (optional)
   * - page: Page number (default: 1)
   * - limit: Items per page (default: 50, max: 500)
   */
  @Get()
  async getSummaries(@Query() query: SummariesQueryDto) {
    try {
      return await this.summariesService.getSummaries(query);
    } catch (error) {
      this.logger.error(
        `Failed to get summaries: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * GET /api/adherence/summaries/realtime
   * 
   * Get real-time adherence for all active agents.
   * Returns current shift adherence based on today's summaries and latest events.
   * Admin-facing endpoint (no RBAC filtering - shows all agents).
   * 
   * Query Parameters:
   * - department: Filter by department (optional)
   * 
   * Note: This route must come before @Get(':id') to avoid route conflicts.
   */
  @Get('realtime')
  async getRealtimeAdherence(@Query('department') department?: string) {
    try {
      // Get today's summaries for active agents
      const today = new Date();
      const todayStr = today.toISOString().split('T')[0];

      const query: SummariesQueryDto = {
        startDate: todayStr,
        endDate: todayStr,
        department,
        limit: 1000, // Get all active agents
      };

      const result = await this.summariesService.getSummaries(query);

      // Transform to real-time format
      const realtimeData = result.data.map((summary: any) => ({
        employeeId: summary.employeeId,
        employeeName: summary.employeeName,
        hrId: summary.hrId,
        scheduleDate: summary.scheduleDate,
        scheduledStartTime: summary.scheduledStartTime,
        scheduledEndTime: summary.scheduledEndTime,
        currentAdherence: summary.adherencePercentage,
        startVarianceMinutes: summary.startVarianceMinutes,
        endVarianceMinutes: summary.endVarianceMinutes,
        breakCompliancePercentage: summary.breakCompliancePercentage,
        productiveTimeMinutes: summary.productiveTimeMinutes,
        idleTimeMinutes: summary.idleTimeMinutes,
        awayTimeMinutes: summary.awayTimeMinutes,
        // Real-time status indicators
        isCurrentlyActive: summary.actualEndTime === null, // No end time = still active
        minutesIntoShift: summary.actualStartTime
          ? Math.floor(
              (new Date().getTime() - new Date(summary.actualStartTime).getTime()) /
                (1000 * 60),
            )
          : 0,
      }));

      return {
        timestamp: new Date().toISOString(),
        totalAgents: realtimeData.length,
        activeAgents: realtimeData.filter((a: any) => a.isCurrentlyActive).length,
        data: realtimeData,
      };
    } catch (error) {
      this.logger.error(
        `Failed to get real-time adherence: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * GET /api/adherence/summaries/:id
   * 
   * Get a single adherence summary by ID.
   * 
   * @param id - Summary UUID
   * 
   * Note: This route must come after @Get('realtime') to avoid route conflicts.
   */
  @Get(':id')
  async getSummaryById(@Param('id', ParseUUIDPipe) id: string) {
    try {
      const summary = await this.summariesService.getSummaryById(id);
      
      if (!summary) {
        throw new NotFoundException(`Summary with ID ${id} not found`);
      }

      return summary;
    } catch (error) {
      if (error instanceof NotFoundException) {
        throw error;
      }
      this.logger.error(
        `Failed to get summary ${id}: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }
}


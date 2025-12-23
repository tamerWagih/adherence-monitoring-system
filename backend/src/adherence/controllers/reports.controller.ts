import {
  Controller,
  Get,
  Query,
  UseGuards,
  Logger,
  BadRequestException,
  Res,
} from '@nestjs/common';
import { Response } from 'express';
import { JwtAuthGuard } from '../../guards/jwt-auth.guard';
import { RolesGuard } from '../../guards/roles.guard';
import { Roles } from '../../decorators/roles.decorator';
import { ReportingService } from '../services/reporting.service';

/**
 * ReportsController
 * 
 * Handles adherence report generation (daily, weekly, monthly).
 * Supports JSON and CSV export formats.
 * Protected by JWT authentication and System_Admin role.
 * System_Admin has access to all endpoints by default.
 */
@Controller('reports')
@UseGuards(JwtAuthGuard, RolesGuard)
@Roles('System_Admin')
export class ReportsController {
  private readonly logger = new Logger(ReportsController.name);

  constructor(private reportingService: ReportingService) {}

  /**
   * GET /api/adherence/reports/daily
   * 
   * Generate daily adherence report.
   * 
   * Query Parameters:
   * - date: Date in YYYY-MM-DD format (required)
   * - department: Filter by department (optional)
   * - format: Response format - 'json' or 'csv' (default: 'json')
   */
  @Get('daily')
  async getDailyReport(
    @Query('date') date?: string,
    @Query('department') department?: string,
    @Query('format') format: string = 'json',
    @Res({ passthrough: true }) res?: Response,
  ) {
    if (!date) {
      throw new BadRequestException('date query parameter is required (YYYY-MM-DD)');
    }

    // Validate date format
    const dateRegex = /^\d{4}-\d{2}-\d{2}$/;
    if (!dateRegex.test(date)) {
      throw new BadRequestException(
        'Invalid date format. Expected YYYY-MM-DD format.',
      );
    }

    try {
      const report = await this.reportingService.generateDailyReport(
        date,
        department,
      );

      if (format === 'csv') {
        const csv = this.reportingService.generateDailyReportCSV(report);
        res?.setHeader('Content-Type', 'text/csv');
        res?.setHeader(
          'Content-Disposition',
          `attachment; filename="adherence_daily_${date}.csv"`,
        );
        return csv;
      }

      return report;
    } catch (error) {
      this.logger.error(
        `Failed to generate daily report: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * GET /api/adherence/reports/weekly
   * 
   * Generate weekly adherence report.
   * 
   * Query Parameters:
   * - weekStart: Start date in YYYY-MM-DD format (required)
   * - weekEnd: End date in YYYY-MM-DD format (required)
   * - department: Filter by department (optional)
   * - format: Response format - 'json' or 'csv' (default: 'json')
   */
  @Get('weekly')
  async getWeeklyReport(
    @Query('weekStart') weekStart?: string,
    @Query('weekEnd') weekEnd?: string,
    @Query('department') department?: string,
    @Query('format') format: string = 'json',
    @Res({ passthrough: true }) res?: Response,
  ) {
    if (!weekStart || !weekEnd) {
      throw new BadRequestException(
        'weekStart and weekEnd query parameters are required (YYYY-MM-DD)',
      );
    }

    // Validate date formats
    const dateRegex = /^\d{4}-\d{2}-\d{2}$/;
    if (!dateRegex.test(weekStart) || !dateRegex.test(weekEnd)) {
      throw new BadRequestException(
        'Invalid date format. Expected YYYY-MM-DD format.',
      );
    }

    // Validate date range
    const start = new Date(weekStart);
    const end = new Date(weekEnd);
    if (start > end) {
      throw new BadRequestException('weekStart must be before or equal to weekEnd');
    }

    try {
      const report = await this.reportingService.generateWeeklyReport(
        weekStart,
        weekEnd,
        department,
      );

      if (format === 'csv') {
        const csv = this.reportingService.generateWeeklyReportCSV(report);
        res?.setHeader('Content-Type', 'text/csv');
        res?.setHeader(
          'Content-Disposition',
          `attachment; filename="adherence_weekly_${weekStart}_to_${weekEnd}.csv"`,
        );
        return csv;
      }

      return report;
    } catch (error) {
      this.logger.error(
        `Failed to generate weekly report: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }

  /**
   * GET /api/adherence/reports/monthly
   * 
   * Generate monthly adherence report.
   * 
   * Query Parameters:
   * - month: Month in YYYY-MM format (required)
   * - department: Filter by department (optional)
   * - format: Response format - 'json' or 'csv' (default: 'json')
   */
  @Get('monthly')
  async getMonthlyReport(
    @Query('month') month?: string,
    @Query('department') department?: string,
    @Query('format') format: string = 'json',
    @Res({ passthrough: true }) res?: Response,
  ) {
    if (!month) {
      throw new BadRequestException('month query parameter is required (YYYY-MM)');
    }

    // Validate month format
    const monthRegex = /^\d{4}-\d{2}$/;
    if (!monthRegex.test(month)) {
      throw new BadRequestException(
        'Invalid month format. Expected YYYY-MM format.',
      );
    }

    try {
      const report = await this.reportingService.generateMonthlyReport(
        month,
        department,
      );

      if (format === 'csv') {
        const csv = this.reportingService.generateMonthlyReportCSV(report);
        res?.setHeader('Content-Type', 'text/csv');
        res?.setHeader(
          'Content-Disposition',
          `attachment; filename="adherence_monthly_${month}.csv"`,
        );
        return csv;
      }

      return report;
    } catch (error) {
      this.logger.error(
        `Failed to generate monthly report: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
      throw error;
    }
  }
}


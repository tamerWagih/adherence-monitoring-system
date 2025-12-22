import { Injectable, Logger, NotFoundException } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, Between, LessThanOrEqual, MoreThanOrEqual } from 'typeorm';
import { AgentAdherenceEvent } from '../../entities/agent-adherence-event.entity';
import { AgentAdherenceSummary } from '../../entities/agent-adherence-summary.entity';
import { AgentAdherenceException } from '../../entities/agent-adherence-exception.entity';
import { AgentSchedule } from '../../entities/agent-schedule.entity';
import { EventType } from '../../dto/create-adherence-event.dto';

/**
 * Activity Metrics Interface
 */
interface ActivityMetrics {
  productiveTimeMinutes: number;
  idleTimeMinutes: number;
  awayTimeMinutes: number;
  nonWorkAppTimeMinutes: number;
  workAppTimeMinutes: number;
}

/**
 * Break Compliance Interface
 */
interface BreakCompliance {
  percentage: number;
  missedBreaksCount: number;
  extendedBreaksCount: number;
  scheduledBreaks: Array<{
    id: string;
    start: string;
    end: string;
    duration_minutes: number;
  }>;
  actualBreaks: Array<{
    start: Date;
    end: Date;
    duration_minutes: number;
  }>;
}

/**
 * AdherenceCalculationService
 * 
 * Calculates daily adherence from events and generates summaries.
 * Uses Egypt timezone (+02:00) for all date calculations.
 */
@Injectable()
export class AdherenceCalculationService {
  private readonly logger = new Logger(AdherenceCalculationService.name);
  private readonly EGYPT_TIMEZONE = '+02:00';
  private readonly DEFAULT_BATCH_SIZE = 50;

  constructor(
    @InjectRepository(AgentAdherenceEvent)
    private eventRepo: Repository<AgentAdherenceEvent>,
    @InjectRepository(AgentAdherenceSummary)
    private summaryRepo: Repository<AgentAdherenceSummary>,
    @InjectRepository(AgentAdherenceException)
    private exceptionRepo: Repository<AgentAdherenceException>,
    @InjectRepository(AgentSchedule)
    private scheduleRepo: Repository<AgentSchedule>,
  ) {}

  /**
   * Calculate daily adherence for an employee
   * 
   * @param employeeId - Employee UUID
   * @param scheduleDate - Date to calculate adherence for (in Egypt timezone)
   * @param schedule - Agent schedule for the day
   * @returns Calculated adherence summary
   */
  async calculateDailyAdherence(
    employeeId: string,
    scheduleDate: Date,
    schedule: AgentSchedule,
  ): Promise<AgentAdherenceSummary> {
    this.logger.debug(
      `Calculating adherence for employee ${employeeId} on ${scheduleDate.toISOString()}`,
    );

    // 1. Get all events for the day
    const events = await this.getEventsForDay(employeeId, scheduleDate);

    // 2. Calculate actual start/end times
    const actualStart = this.findActualStartTime(events, schedule);
    const actualEnd = this.findActualEndTime(events, schedule);

    // 3. Calculate variances
    const startVariance = this.calculateStartVariance(actualStart, schedule);
    const endVariance = this.calculateEndVariance(actualEnd, schedule);

    // 4. Calculate break compliance
    const breakCompliance = await this.calculateBreakCompliance(
      employeeId,
      scheduleDate,
      events,
    );

    // 5. Calculate activity metrics
    const activityMetrics = this.calculateActivityMetrics(events, schedule);

    // 6. Apply exceptions
    const exceptions = await this.getExceptions(employeeId, scheduleDate);
    const adjustedMetrics = this.applyExceptions(activityMetrics, exceptions);

    // 7. Calculate weighted adherence percentage
    const adherencePercentage = this.calculateWeightedAdherence(
      startVariance,
      endVariance,
      breakCompliance,
      adjustedMetrics,
      schedule,
    );

    // 8. Calculate scheduled duration
    const scheduledDurationMinutes = this.calculateScheduledDuration(schedule);
    const actualDurationMinutes = actualStart && actualEnd
      ? Math.round((actualEnd.getTime() - actualStart.getTime()) / (1000 * 60))
      : 0;

    // 9. Save or update summary
    return this.saveSummary({
      employeeId,
      scheduleDate,
      schedule,
      actualStart,
      actualEnd,
      startVariance,
      endVariance,
      breakCompliance,
      activityMetrics: adjustedMetrics,
      adherencePercentage,
      scheduledDurationMinutes,
      actualDurationMinutes,
      exceptions,
    });
  }

  /**
   * Get all events for a specific day
   * Uses Egypt timezone for date range calculation
   */
  private async getEventsForDay(
    employeeId: string,
    scheduleDate: Date,
  ): Promise<AgentAdherenceEvent[]> {
    // Convert scheduleDate to Egypt timezone start and end of day
    const dateStr = scheduleDate.toISOString().split('T')[0]; // YYYY-MM-DD
    const startOfDay = new Date(`${dateStr}T00:00:00${this.EGYPT_TIMEZONE}`);
    const endOfDay = new Date(`${dateStr}T23:59:59${this.EGYPT_TIMEZONE}`);

    // Convert to UTC for database query (database stores in UTC)
    const startUTC = new Date(startOfDay.toISOString());
    const endUTC = new Date(endOfDay.toISOString());

    return this.eventRepo.find({
      where: {
        employeeId,
        eventTimestamp: Between(startUTC, endUTC),
      },
      order: {
        eventTimestamp: 'ASC',
      },
    });
  }

  /**
   * Find actual start time from events
   * Uses first LOGIN event, or first event if no LOGIN
   */
  private findActualStartTime(
    events: AgentAdherenceEvent[],
    schedule: AgentSchedule,
  ): Date | null {
    // Look for LOGIN event first
    const loginEvent = events.find((e) => e.eventType === EventType.LOGIN);
    if (loginEvent) {
      return loginEvent.eventTimestamp;
    }

    // If no LOGIN, use first event
    if (events.length > 0) {
      return events[0].eventTimestamp;
    }

    return null;
  }

  /**
   * Find actual end time from events
   * Uses last LOGOFF event, or last event if no LOGOFF
   */
  private findActualEndTime(
    events: AgentAdherenceEvent[],
    schedule: AgentSchedule,
  ): Date | null {
    // Look for LOGOFF event (from end of array)
    const logoffEvents = events.filter((e) => e.eventType === EventType.LOGOFF);
    if (logoffEvents.length > 0) {
      return logoffEvents[logoffEvents.length - 1].eventTimestamp;
    }

    // If no LOGOFF, use last event
    if (events.length > 0) {
      return events[events.length - 1].eventTimestamp;
    }

    return null;
  }

  /**
   * Calculate start variance in minutes
   */
  private calculateStartVariance(
    actualStart: Date | null,
    schedule: AgentSchedule,
  ): number {
    if (!actualStart || !schedule.shiftStart) {
      return 0;
    }

    // Parse scheduled start time (format: "HH:mm:ss" or "HH:mm")
    const [hours, minutes] = schedule.shiftStart.split(':').map(Number);
    const scheduleDate = new Date(actualStart);
    scheduleDate.setHours(hours, minutes || 0, 0, 0);

    // Convert schedule to Egypt timezone
    const scheduleDateStr = scheduleDate.toISOString().split('T')[0];
    const scheduleDateTime = new Date(`${scheduleDateStr}T${schedule.shiftStart}${this.EGYPT_TIMEZONE}`);
    const scheduleUTC = new Date(scheduleDateTime.toISOString());

    // Calculate difference in minutes
    const diffMs = actualStart.getTime() - scheduleUTC.getTime();
    return Math.round(diffMs / (1000 * 60));
  }

  /**
   * Calculate end variance in minutes
   */
  private calculateEndVariance(
    actualEnd: Date | null,
    schedule: AgentSchedule,
  ): number {
    if (!actualEnd || !schedule.shiftEnd) {
      return 0;
    }

    // Parse scheduled end time (format: "HH:mm:ss" or "HH:mm")
    const [hours, minutes] = schedule.shiftEnd.split(':').map(Number);
    const scheduleDate = new Date(actualEnd);
    scheduleDate.setHours(hours, minutes || 0, 0, 0);

    // Convert schedule to Egypt timezone
    const scheduleDateStr = scheduleDate.toISOString().split('T')[0];
    const scheduleDateTime = new Date(`${scheduleDateStr}T${schedule.shiftEnd}${this.EGYPT_TIMEZONE}`);
    const scheduleUTC = new Date(scheduleDateTime.toISOString());

    // Calculate difference in minutes
    const diffMs = actualEnd.getTime() - scheduleUTC.getTime();
    return Math.round(diffMs / (1000 * 60));
  }

  /**
   * Calculate break compliance
   */
  private async calculateBreakCompliance(
    employeeId: string,
    scheduleDate: Date,
    events: AgentAdherenceEvent[],
  ): Promise<BreakCompliance> {
    // Get scheduled breaks from agent_schedules
    const dateStr = scheduleDate.toISOString().split('T')[0];
    const schedules = await this.scheduleRepo.query(
      `
      SELECT * FROM agent_schedules
      WHERE employee_id = $1
        AND schedule_date = $2::date
        AND is_break = $3
        AND is_confirmed = $4
      ORDER BY shift_start ASC
      `,
      [employeeId, dateStr, true, true],
    );

    const scheduledBreaks = schedules.map((s: any) => ({
      id: s.id,
      start: s.shift_start,
      end: s.shift_end,
      duration_minutes: s.break_duration || 0,
    }));

    // Get actual breaks from events
    const actualBreaks: Array<{
      start: Date;
      end: Date;
      duration_minutes: number;
    }> = [];

    let breakStart: Date | null = null;
    for (const event of events) {
      if (event.eventType === EventType.BREAK_START) {
        breakStart = event.eventTimestamp;
      } else if (event.eventType === EventType.BREAK_END && breakStart) {
        const durationMs = event.eventTimestamp.getTime() - breakStart.getTime();
        const durationMinutes = Math.round(durationMs / (1000 * 60));
        actualBreaks.push({
          start: breakStart,
          end: event.eventTimestamp,
          duration_minutes: durationMinutes,
        });
        breakStart = null;
      }
    }

    // Match actual breaks to scheduled breaks
    let matchedBreaks = 0;
    let missedBreaks = 0;
    let extendedBreaks = 0;
    const EXTENDED_BREAK_THRESHOLD = 5; // minutes

    for (const scheduled of scheduledBreaks) {
      // Find matching actual break (within scheduled window)
      const scheduledStart = this.parseTimeToDate(scheduleDate, scheduled.start);
      const scheduledEnd = this.parseTimeToDate(scheduleDate, scheduled.end);

      const matchingBreak = actualBreaks.find((actual) => {
        return (
          actual.start >= scheduledStart &&
          actual.end <= scheduledEnd &&
          actual.duration_minutes >= scheduled.duration_minutes - EXTENDED_BREAK_THRESHOLD
        );
      });

      if (matchingBreak) {
        matchedBreaks++;
        // Check if extended
        if (matchingBreak.duration_minutes > scheduled.duration_minutes + EXTENDED_BREAK_THRESHOLD) {
          extendedBreaks++;
        }
      } else {
        missedBreaks++;
      }
    }

    // Calculate compliance percentage
    const compliancePercentage =
      scheduledBreaks.length > 0
        ? (matchedBreaks / scheduledBreaks.length) * 100
        : 100; // No scheduled breaks = 100% compliance

    return {
      percentage: Math.round(compliancePercentage * 100) / 100, // Round to 2 decimals
      missedBreaksCount: missedBreaks,
      extendedBreaksCount: extendedBreaks,
      scheduledBreaks,
      actualBreaks,
    };
  }

  /**
   * Parse time string to Date object in Egypt timezone
   */
  private parseTimeToDate(date: Date, timeStr: string): Date {
    const dateStr = date.toISOString().split('T')[0];
    return new Date(`${dateStr}T${timeStr}${this.EGYPT_TIMEZONE}`);
  }

  /**
   * Calculate activity metrics from events
   */
  private calculateActivityMetrics(
    events: AgentAdherenceEvent[],
    schedule: AgentSchedule,
  ): ActivityMetrics {
    let productiveTimeMinutes = 0;
    let idleTimeMinutes = 0;
    let awayTimeMinutes = 0;
    let nonWorkAppTimeMinutes = 0;
    let workAppTimeMinutes = 0;

    // Track idle periods
    let idleStart: Date | null = null;
    for (const event of events) {
      if (event.eventType === EventType.IDLE_START) {
        idleStart = event.eventTimestamp;
      } else if (event.eventType === EventType.IDLE_END && idleStart) {
        const durationMs = event.eventTimestamp.getTime() - idleStart.getTime();
        idleTimeMinutes += Math.round(durationMs / (1000 * 60));
        idleStart = null;
      }
    }

    // Track away periods (LOGOFF to LOGIN gaps)
    let logoffTime: Date | null = null;
    for (const event of events) {
      if (event.eventType === EventType.LOGOFF) {
        logoffTime = event.eventTimestamp;
      } else if (event.eventType === EventType.LOGIN && logoffTime) {
        const durationMs = event.eventTimestamp.getTime() - logoffTime.getTime();
        awayTimeMinutes += Math.round(durationMs / (1000 * 60));
        logoffTime = null;
      }
    }

    // Calculate work/non-work app time from WINDOW_CHANGE and APPLICATION_FOCUS events
    // We need to track time spent in each application
    const appTimeMap = new Map<string, { start: Date; isWork: boolean }>();
    let lastEventTime: Date | null = null;

    for (const event of events) {
      const eventTime = event.eventTimestamp;

      // Calculate time since last event for previous app
      if (lastEventTime && appTimeMap.size > 0) {
        const durationMs = eventTime.getTime() - lastEventTime.getTime();
        const durationMinutes = Math.round(durationMs / (1000 * 60));

        // Add time to all active apps
        for (const [appKey, appData] of appTimeMap.entries()) {
          if (appData.isWork) {
            workAppTimeMinutes += durationMinutes;
            productiveTimeMinutes += durationMinutes;
          } else {
            nonWorkAppTimeMinutes += durationMinutes;
          }
        }
      }

      // Handle window/application events
      if (
        event.eventType === EventType.WINDOW_CHANGE ||
        event.eventType === EventType.APPLICATION_FOCUS
      ) {
        const appKey = event.applicationName || 'unknown';
        const isWork = event.isWorkApplication === true;

        // Clear previous apps and set new one
        appTimeMap.clear();
        appTimeMap.set(appKey, { start: eventTime, isWork });
      }

      lastEventTime = eventTime;
    }

    return {
      productiveTimeMinutes,
      idleTimeMinutes,
      awayTimeMinutes,
      nonWorkAppTimeMinutes,
      workAppTimeMinutes,
    };
  }

  /**
   * Get approved exceptions for the day
   */
  private async getExceptions(
    employeeId: string,
    scheduleDate: Date,
  ): Promise<AgentAdherenceException[]> {
    const dateStr = scheduleDate.toISOString().split('T')[0];
    const exceptionDateObj = new Date(dateStr + 'T00:00:00Z');
    
    return this.exceptionRepo.find({
      where: {
        employeeId,
        exceptionDate: exceptionDateObj as any, // TypeORM date comparison
        status: 'APPROVED',
      },
    });
  }

  /**
   * Apply exceptions to activity metrics
   * Excludes exception time from idle/away calculations
   */
  private applyExceptions(
    metrics: ActivityMetrics,
    exceptions: AgentAdherenceException[],
  ): ActivityMetrics {
    let totalExceptionMinutes = 0;

    for (const exception of exceptions) {
      // Use approved adjustment if available, otherwise requested adjustment
      const adjustmentMinutes =
        exception.approvedAdjustmentMinutes || exception.requestedAdjustmentMinutes || 0;
      totalExceptionMinutes += adjustmentMinutes;
    }

    // Exclude exception time from idle and away time
    // Exception time is considered productive/excused time
    const adjustedIdleTime = Math.max(0, metrics.idleTimeMinutes - totalExceptionMinutes);
    const adjustedAwayTime = Math.max(0, metrics.awayTimeMinutes - totalExceptionMinutes);

    return {
      ...metrics,
      idleTimeMinutes: adjustedIdleTime,
      awayTimeMinutes: adjustedAwayTime,
      // Exception time adds to productive time
      productiveTimeMinutes: metrics.productiveTimeMinutes + totalExceptionMinutes,
    };
  }

  /**
   * Calculate weighted adherence percentage
   */
  private calculateWeightedAdherence(
    startVariance: number,
    endVariance: number,
    breakCompliance: BreakCompliance,
    activityMetrics: ActivityMetrics,
    schedule: AgentSchedule,
  ): number {
    // Start time score (20% weight)
    // Penalty: -2 points per minute variance
    const startScore = Math.max(0, 100 - Math.abs(startVariance) * 2);

    // End time score (20% weight)
    // Penalty: -2 points per minute variance
    const endScore = Math.max(0, 100 - Math.abs(endVariance) * 2);

    // Break compliance score (20% weight)
    const breakScore = breakCompliance.percentage;

    // Productive time score (40% weight)
    const scheduledDurationMinutes = this.calculateScheduledDuration(schedule);
    const productiveScore =
      scheduledDurationMinutes > 0
        ? (activityMetrics.productiveTimeMinutes / scheduledDurationMinutes) * 100
        : 0;

    // Weighted calculation
    const adherencePercentage =
      startScore * 0.2 +
      endScore * 0.2 +
      breakScore * 0.2 +
      productiveScore * 0.4;

    return Math.round(adherencePercentage * 100) / 100; // Round to 2 decimals
  }

  /**
   * Calculate scheduled duration in minutes
   */
  private calculateScheduledDuration(schedule: AgentSchedule): number {
    if (!schedule.shiftStart || !schedule.shiftEnd) {
      return 0;
    }

    const [startHours, startMinutes] = schedule.shiftStart.split(':').map(Number);
    const [endHours, endMinutes] = schedule.shiftEnd.split(':').map(Number);

    const startTotalMinutes = startHours * 60 + (startMinutes || 0);
    const endTotalMinutes = endHours * 60 + (endMinutes || 0);

    return endTotalMinutes - startTotalMinutes;
  }

  /**
   * Save or update adherence summary
   */
  private async saveSummary(data: {
    employeeId: string;
    scheduleDate: Date;
    schedule: AgentSchedule;
    actualStart: Date | null;
    actualEnd: Date | null;
    startVariance: number;
    endVariance: number;
    breakCompliance: BreakCompliance;
    activityMetrics: ActivityMetrics;
    adherencePercentage: number;
    scheduledDurationMinutes: number;
    actualDurationMinutes: number;
    exceptions: AgentAdherenceException[];
  }): Promise<AgentAdherenceSummary> {
    const dateStr = data.scheduleDate.toISOString().split('T')[0];
    const scheduleDateObj = new Date(dateStr + 'T00:00:00Z');

    // Check if summary already exists
    const existing = await this.summaryRepo.findOne({
      where: {
        employeeId: data.employeeId,
        scheduleDate: scheduleDateObj as any,
      },
    });

    const exceptionAdjustments = data.exceptions.map((e) => ({
      id: e.id,
      type: e.exceptionType,
      adjustment_minutes: e.approvedAdjustmentMinutes || e.requestedAdjustmentMinutes || 0,
    }));

    // Prepare JSONB data
    const scheduledBreaksJson = data.breakCompliance.scheduledBreaks;
    const actualBreaksJson = data.breakCompliance.actualBreaks.map((b) => ({
      start: b.start.toISOString(),
      end: b.end.toISOString(),
      duration_minutes: b.duration_minutes,
    }));

    const summaryData: Partial<AgentAdherenceSummary> = {
      employeeId: data.employeeId,
      scheduleDate: data.scheduleDate,
      scheduledStartTime: data.schedule.shiftStart,
      scheduledEndTime: data.schedule.shiftEnd,
      scheduledDurationMinutes: data.scheduledDurationMinutes,
      actualStartTime: data.actualStart,
      actualEndTime: data.actualEnd,
      actualDurationMinutes: data.actualDurationMinutes,
      startVarianceMinutes: data.startVariance,
      endVarianceMinutes: data.endVariance,
      breakCompliancePercentage: data.breakCompliance.percentage,
      missedBreaksCount: data.breakCompliance.missedBreaksCount,
      extendedBreaksCount: data.breakCompliance.extendedBreaksCount,
      productiveTimeMinutes: data.activityMetrics.productiveTimeMinutes,
      idleTimeMinutes: data.activityMetrics.idleTimeMinutes,
      awayTimeMinutes: data.activityMetrics.awayTimeMinutes,
      nonWorkAppTimeMinutes: data.activityMetrics.nonWorkAppTimeMinutes,
      adherencePercentage: data.adherencePercentage,
      calculatedAt: new Date(),
    };

    if (existing) {
      // Update existing summary
      Object.assign(existing, summaryData);
      const saved = await this.summaryRepo.save(existing);
      
      // Update JSONB fields using raw query
      await this.summaryRepo.query(
        `
        UPDATE agent_adherence_summaries
        SET scheduled_breaks = $1::jsonb,
            actual_breaks = $2::jsonb,
            exception_adjustments = $3::jsonb
        WHERE id = $4
        `,
        [
          JSON.stringify(scheduledBreaksJson),
          JSON.stringify(actualBreaksJson),
          JSON.stringify(exceptionAdjustments),
          saved.id,
        ],
      );
      
      return saved;
    } else {
      // Create new summary
      const summary = this.summaryRepo.create(summaryData);
      const saved = await this.summaryRepo.save(summary);
      
      // Update JSONB fields using raw query
      await this.summaryRepo.query(
        `
        UPDATE agent_adherence_summaries
        SET scheduled_breaks = $1::jsonb,
            actual_breaks = $2::jsonb,
            exception_adjustments = $3::jsonb
        WHERE id = $4
        `,
        [
          JSON.stringify(scheduledBreaksJson),
          JSON.stringify(actualBreaksJson),
          JSON.stringify(exceptionAdjustments),
          saved.id,
        ],
      );
      
      return saved;
    }
  }

  /**
   * Get employees with schedules for a specific date
   */
  async getEmployeesWithSchedules(date: Date): Promise<Array<{ employeeId: string; schedule: AgentSchedule }>> {
    const dateStr = date.toISOString().split('T')[0];
    const schedules = await this.scheduleRepo.query(
      `
      SELECT * FROM agent_schedules
      WHERE schedule_date = $1::date
        AND is_break = $2
        AND is_confirmed = $3
      ORDER BY employee_id ASC
      `,
      [dateStr, false, true], // is_break = false means shift, not break
    );

    // Group by employee and get first shift schedule
    const employeeMap = new Map<string, AgentSchedule>();
    for (const row of schedules) {
      if (!employeeMap.has(row.employee_id)) {
        employeeMap.set(row.employee_id, {
          id: row.id,
          employeeId: row.employee_id,
          scheduleDate: row.schedule_date,
          shiftStart: row.shift_start,
          shiftEnd: row.shift_end,
          isBreak: row.is_break,
          breakDuration: row.break_duration,
          isConfirmed: row.is_confirmed,
        });
      }
    }

    return Array.from(employeeMap.entries()).map(([employeeId, schedule]) => ({
      employeeId,
      schedule,
    }));
  }

  /**
   * Batch calculate adherence for multiple employees
   */
  async batchCalculateAdherence(
    date: Date,
    batchSize: number = this.DEFAULT_BATCH_SIZE,
  ): Promise<{ processed: number; failed: number; errors: Array<{ employeeId: string; error: string }> }> {
    const employees = await this.getEmployeesWithSchedules(date);
    let processed = 0;
    let failed = 0;
    const errors: Array<{ employeeId: string; error: string }> = [];

    // Process in batches
    for (let i = 0; i < employees.length; i += batchSize) {
      const batch = employees.slice(i, i + batchSize);

      await Promise.all(
        batch.map(async ({ employeeId, schedule }) => {
          try {
            await this.calculateDailyAdherence(employeeId, date, schedule);
            processed++;
          } catch (error) {
            failed++;
            errors.push({
              employeeId,
              error: error instanceof Error ? error.message : String(error),
            });
            this.logger.error(
              `Failed to calculate adherence for employee ${employeeId}: ${error instanceof Error ? error.message : String(error)}`,
              error instanceof Error ? error.stack : undefined,
            );
          }
        }),
      );
    }

    return { processed, failed, errors };
  }
}


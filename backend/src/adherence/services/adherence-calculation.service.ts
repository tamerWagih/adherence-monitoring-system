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
    const startVariance = this.calculateStartVariance(actualStart, schedule, scheduleDate);
    const endVariance = this.calculateEndVariance(actualEnd, schedule, scheduleDate);

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
   * 
   * Note: Schedule times (shiftStart/shiftEnd) are stored as TIME without timezone.
   * We interpret them as UTC to match how events are stored (UTC timestamps).
   */
  private calculateStartVariance(
    actualStart: Date | null,
    schedule: AgentSchedule,
    scheduleDate: Date,
  ): number {
    if (!actualStart || !schedule.shiftStart) {
      return 0;
    }

    // Parse scheduled start time (format: "HH:mm:ss" or "HH:mm")
    const [hours, minutes] = schedule.shiftStart.split(':').map(Number);
    
    // Build scheduled time as UTC (schedule times are stored as UTC)
    const scheduleDateStr = scheduleDate.toISOString().split('T')[0];
    const scheduleUTC = new Date(`${scheduleDateStr}T${schedule.shiftStart}Z`);

    // Calculate difference in minutes
    const diffMs = actualStart.getTime() - scheduleUTC.getTime();
    return Math.round(diffMs / (1000 * 60));
  }

  /**
   * Calculate end variance in minutes
   * 
   * Note: Schedule times (shiftStart/shiftEnd) are stored as TIME without timezone.
   * We interpret them as UTC to match how events are stored (UTC timestamps).
   */
  private calculateEndVariance(
    actualEnd: Date | null,
    schedule: AgentSchedule,
    scheduleDate: Date,
  ): number {
    if (!actualEnd || !schedule.shiftEnd) {
      return 0;
    }

    // Parse scheduled end time (format: "HH:mm:ss" or "HH:mm")
    const [hours, minutes] = schedule.shiftEnd.split(':').map(Number);
    
    // Build scheduled time as UTC (schedule times are stored as UTC)
    const scheduleDateStr = scheduleDate.toISOString().split('T')[0];
    const scheduleUTC = new Date(`${scheduleDateStr}T${schedule.shiftEnd}Z`);

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

    this.logger.debug(
      `Break compliance: Found ${scheduledBreaks.length} scheduled break(s) for ${employeeId} on ${dateStr}`,
    );

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
        this.logger.debug(
          `Break compliance: Found actual break ${breakStart.toISOString()} to ${event.eventTimestamp.toISOString()} (${durationMinutes} minutes)`,
        );
        breakStart = null;
      }
    }

    this.logger.debug(
      `Break compliance: Found ${actualBreaks.length} actual break(s)`,
    );

    // Match actual breaks to scheduled breaks
    // Use a set to track which actual breaks have been matched (prevent double-matching)
    const matchedActualBreakIndices = new Set<number>();
    let matchedBreaks = 0;
    let missedBreaks = 0;
    let extendedBreaks = 0;
    const EXTENDED_BREAK_THRESHOLD = 5; // minutes

    for (const scheduled of scheduledBreaks) {
      // Find matching actual break (starts within scheduled window)
      // Extended breaks (ending after scheduled end) still count as matched
      const scheduledStart = this.parseTimeToDate(scheduleDate, scheduled.start);
      const scheduledEnd = this.parseTimeToDate(scheduleDate, scheduled.end);

      // Find first unmatched actual break that matches this scheduled break
      const matchingBreakIndex = actualBreaks.findIndex((actual, index) => {
        if (matchedActualBreakIndices.has(index)) {
          return false; // Already matched to another scheduled break
        }
        // Break matches if it starts within the scheduled window
        // and has at least the minimum duration (allowing for short breaks)
        return (
          actual.start >= scheduledStart &&
          actual.start <= scheduledEnd &&
          actual.duration_minutes >= scheduled.duration_minutes - EXTENDED_BREAK_THRESHOLD
        );
      });

      if (matchingBreakIndex !== -1) {
        const matchingBreak = actualBreaks[matchingBreakIndex];
        matchedActualBreakIndices.add(matchingBreakIndex);
        matchedBreaks++;
        // Check if extended (duration exceeds scheduled + threshold)
        if (matchingBreak.duration_minutes > scheduled.duration_minutes + EXTENDED_BREAK_THRESHOLD) {
          extendedBreaks++;
        }
        this.logger.debug(
          `Break compliance: Scheduled break ${scheduled.start}-${scheduled.end} matched actual break ${matchingBreak.start.toISOString()}-${matchingBreak.end.toISOString()} (extended: ${matchingBreak.duration_minutes > scheduled.duration_minutes + EXTENDED_BREAK_THRESHOLD})`,
        );
      } else {
        missedBreaks++;
        this.logger.debug(
          `Break compliance: Scheduled break ${scheduled.start}-${scheduled.end} NOT matched`,
        );
      }
    }

    // Calculate compliance percentage
    const compliancePercentage =
      scheduledBreaks.length > 0
        ? (matchedBreaks / scheduledBreaks.length) * 100
        : 100; // No scheduled breaks = 100% compliance

    this.logger.debug(
      `Break compliance: ${matchedBreaks} matched, ${missedBreaks} missed, ${extendedBreaks} extended out of ${scheduledBreaks.length} scheduled breaks = ${compliancePercentage.toFixed(2)}%`,
    );

    return {
      percentage: Math.round(compliancePercentage * 100) / 100, // Round to 2 decimals
      missedBreaksCount: missedBreaks,
      extendedBreaksCount: extendedBreaks,
      scheduledBreaks,
      actualBreaks,
    };
  }

  /**
   * Parse time string to Date object
   * 
   * Note: Schedule times (shift_start/shift_end) are stored as TIME without timezone.
   * We interpret them as UTC to match how events are stored (UTC timestamps).
   */
  private parseTimeToDate(date: Date, timeStr: string): Date {
    const dateStr = date.toISOString().split('T')[0];
    return new Date(`${dateStr}T${timeStr}Z`); // Interpret as UTC
  }

  /**
   * Calculate activity metrics from events
   * 
   * Simplified approach:
   * 1. Start with total time from LOGIN to LOGOFF (or schedule end)
   * 2. Subtract idle periods
   * 3. Subtract break periods
   * 4. Track activity events to determine work vs non-work app time
   * 
   * Handles ALL event types:
   * - LOGIN/LOGOFF: Define work boundaries
   * - IDLE_START/IDLE_END: Reduce productive time
   * - BREAK_START/BREAK_END: Exclude from productive time
   * - Activity events (WINDOW_CHANGE, APPLICATION_START, etc.): Track productive/non-productive time
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

    // Find actual start/end times (LOGIN/LOGOFF events)
    const loginEvent = events.find(e => e.eventType === EventType.LOGIN);
    const logoffEvents = events.filter(e => e.eventType === EventType.LOGOFF);
    const actualStartTime = loginEvent?.eventTimestamp || null;
    const actualEndTime = logoffEvents.length > 0 
      ? logoffEvents[logoffEvents.length - 1].eventTimestamp 
      : null;

    // Use schedule end if no LOGOFF event
    let workEndTime = actualEndTime;
    if (!workEndTime && schedule.shiftEnd && actualStartTime) {
      const [endHours, endMinutes] = schedule.shiftEnd.split(':').map(Number);
      const scheduleDateStr = actualStartTime.toISOString().split('T')[0];
      const scheduledEndDateTime = new Date(`${scheduleDateStr}T${schedule.shiftEnd}${this.EGYPT_TIMEZONE}`);
      workEndTime = new Date(scheduledEndDateTime.toISOString());
    }

    if (!actualStartTime || !workEndTime) {
      // No valid work period
      return {
        productiveTimeMinutes: 0,
        idleTimeMinutes: 0,
        awayTimeMinutes: 0,
        nonWorkAppTimeMinutes: 0,
        workAppTimeMinutes: 0,
      };
    }

    // Track idle periods (these reduce productive time)
    let idleStart: Date | null = null;
    const idlePeriods: Array<{ start: Date; end: Date }> = [];
    for (const event of events) {
      if (event.eventType === EventType.IDLE_START) {
        idleStart = event.eventTimestamp;
      } else if (event.eventType === EventType.IDLE_END && idleStart) {
        const durationMs = event.eventTimestamp.getTime() - idleStart.getTime();
        idleTimeMinutes += Math.round(durationMs / (1000 * 60));
        idlePeriods.push({ start: idleStart, end: event.eventTimestamp });
        idleStart = null;
      }
    }
    // Handle idle that extends to end of shift
    if (idleStart && workEndTime) {
      const durationMs = workEndTime.getTime() - idleStart.getTime();
      idleTimeMinutes += Math.round(durationMs / (1000 * 60));
      idlePeriods.push({ start: idleStart, end: workEndTime });
    }

    // Track break periods (excluded from productive time)
    let breakStart: Date | null = null;
    const breakPeriods: Array<{ start: Date; end: Date }> = [];
    for (const event of events) {
      if (event.eventType === EventType.BREAK_START) {
        breakStart = event.eventTimestamp;
      } else if (event.eventType === EventType.BREAK_END && breakStart) {
        breakPeriods.push({ start: breakStart, end: event.eventTimestamp });
        breakStart = null;
      }
    }
    // Handle break that extends to end of shift
    if (breakStart && workEndTime) {
      breakPeriods.push({ start: breakStart, end: workEndTime });
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

    // Helper function to check if event indicates active work
    const isActivityEvent = (eventType: string): boolean => {
      return (
        eventType === EventType.WINDOW_CHANGE ||
        eventType === EventType.APPLICATION_FOCUS ||
        eventType === EventType.APPLICATION_START ||
        eventType === EventType.BROWSER_TAB_CHANGE ||
        eventType === EventType.CLIENT_WEBSITE_ACCESS ||
        eventType === EventType.CALLING_APP_IN_CALL ||
        eventType === EventType.CALLING_APP_START ||
        eventType === EventType.TEAMS_MEETING_START ||
        eventType === EventType.TEAMS_CHAT_ACTIVE
      );
    };

    // Calculate total work time (from LOGIN to LOGOFF/schedule end)
    const totalWorkTimeMs = workEndTime.getTime() - actualStartTime.getTime();
    const totalWorkTimeMinutes = Math.round(totalWorkTimeMs / (1000 * 60));

    // Calculate excluded time (idle + breaks)
    let excludedTimeMinutes = 0;
    for (const idle of idlePeriods) {
      const durationMs = idle.end.getTime() - idle.start.getTime();
      excludedTimeMinutes += Math.round(durationMs / (1000 * 60));
    }
    for (const breakPeriod of breakPeriods) {
      const durationMs = breakPeriod.end.getTime() - breakPeriod.start.getTime();
      excludedTimeMinutes += Math.round(durationMs / (1000 * 60));
    }

    // Start with total work time minus excluded time
    let availableTimeMinutes = totalWorkTimeMinutes - excludedTimeMinutes;

    // Now track activity events to determine work vs non-work app time
    // Sort events by timestamp
    const sortedEvents = [...events].sort((a, b) => 
      a.eventTimestamp.getTime() - b.eventTimestamp.getTime()
    );

    // Track periods of work vs non-work app usage
    let currentAppStart: Date | null = null;
    let currentAppIsWork: boolean | null = null;
    let lastEventTime: Date | null = actualStartTime;

    for (const event of sortedEvents) {
      const eventTime = event.eventTimestamp;

      // Skip events outside work period
      if (eventTime < actualStartTime || eventTime > workEndTime) {
        continue;
      }

      // Skip LOGIN/LOGOFF (already handled)
      if (event.eventType === EventType.LOGIN || event.eventType === EventType.LOGOFF) {
        lastEventTime = eventTime;
        continue;
      }

      // Skip IDLE_START/IDLE_END and BREAK_START/BREAK_END (already handled)
      if (
        event.eventType === EventType.IDLE_START ||
        event.eventType === EventType.IDLE_END ||
        event.eventType === EventType.BREAK_START ||
        event.eventType === EventType.BREAK_END
      ) {
        lastEventTime = eventTime;
        continue;
      }

      // Calculate time since last event for current app
      if (lastEventTime && currentAppStart !== null && currentAppIsWork !== null) {
        // Calculate actual work time in this period (excluding idle/break overlaps)
        let workTimeMs = eventTime.getTime() - lastEventTime.getTime();
        
        // Subtract idle periods that overlap with this time period
        for (const idle of idlePeriods) {
          const overlapStart = Math.max(lastEventTime.getTime(), idle.start.getTime());
          const overlapEnd = Math.min(eventTime.getTime(), idle.end.getTime());
          if (overlapStart < overlapEnd) {
            workTimeMs -= (overlapEnd - overlapStart);
          }
        }
        
        // Subtract break periods that overlap with this time period
        for (const breakPeriod of breakPeriods) {
          const overlapStart = Math.max(lastEventTime.getTime(), breakPeriod.start.getTime());
          const overlapEnd = Math.min(eventTime.getTime(), breakPeriod.end.getTime());
          if (overlapStart < overlapEnd) {
            workTimeMs -= (overlapEnd - overlapStart);
          }
        }
        
        const durationMinutes = Math.round(workTimeMs / (1000 * 60));
        
        if (durationMinutes > 0) {
          if (currentAppIsWork) {
            workAppTimeMinutes += durationMinutes;
          } else {
            nonWorkAppTimeMinutes += durationMinutes;
          }
        }
      }

      // Handle activity events
      if (isActivityEvent(event.eventType)) {
        // APPLICATION_END means app closed, so stop tracking that app
        if (event.eventType === EventType.APPLICATION_END) {
          currentAppStart = null;
          currentAppIsWork = null;
        } else {
          // Start tracking new app/activity
          currentAppStart = eventTime;
          // Default to work application if not specified (most activity is work-related)
          currentAppIsWork = event.isWorkApplication !== undefined ? event.isWorkApplication : true;
        }
      }

      lastEventTime = eventTime;
    }

    // Calculate time from last event to end of work period
    if (lastEventTime && lastEventTime < workEndTime) {
      // Calculate actual work time in this period (excluding idle/break overlaps)
      let workTimeMs = workEndTime.getTime() - lastEventTime.getTime();
      
      // Subtract idle periods that overlap with this time period
      for (const idle of idlePeriods) {
        const overlapStart = Math.max(lastEventTime.getTime(), idle.start.getTime());
        const overlapEnd = Math.min(workEndTime.getTime(), idle.end.getTime());
        if (overlapStart < overlapEnd) {
          workTimeMs -= (overlapEnd - overlapStart);
        }
      }
      
      // Subtract break periods that overlap with this time period
      for (const breakPeriod of breakPeriods) {
        const overlapStart = Math.max(lastEventTime.getTime(), breakPeriod.start.getTime());
        const overlapEnd = Math.min(workEndTime.getTime(), breakPeriod.end.getTime());
        if (overlapStart < overlapEnd) {
          workTimeMs -= (overlapEnd - overlapStart);
        }
      }
      
      const durationMinutes = Math.round(workTimeMs / (1000 * 60));
      
      if (durationMinutes > 0) {
        if (currentAppIsWork !== null) {
          if (currentAppIsWork) {
            workAppTimeMinutes += durationMinutes;
          } else {
            nonWorkAppTimeMinutes += durationMinutes;
          }
        } else {
          // No app tracked, assume work time (agent is logged in)
          workAppTimeMinutes += durationMinutes;
        }
      }
    }

    // Calculate total tracked time from activity events
    const totalTrackedTime = workAppTimeMinutes + nonWorkAppTimeMinutes;
    
    // If we have gaps (availableTimeMinutes > totalTrackedTime), fill them
    if (availableTimeMinutes > totalTrackedTime) {
      const gapMinutes = availableTimeMinutes - totalTrackedTime;
      // Assume gaps are work time (agent is logged in and working)
      workAppTimeMinutes += gapMinutes;
    }

    // If no activity events tracked at all, assume all available time is work app time
    if (workAppTimeMinutes === 0 && nonWorkAppTimeMinutes === 0 && availableTimeMinutes > 0) {
      workAppTimeMinutes = availableTimeMinutes;
    }

    // Productive time = work app time (non-work app time is already excluded)
    productiveTimeMinutes = workAppTimeMinutes;

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
   * 
   * Note: Database uses start_time/end_time (TIMESTAMPTZ), not exception_date (DATE).
   * We need to check if the exception overlaps with the schedule date.
   */
  private async getExceptions(
    employeeId: string,
    scheduleDate: Date,
  ): Promise<AgentAdherenceException[]> {
    const dateStr = scheduleDate.toISOString().split('T')[0];
    
    // Calculate start and end of day in Egypt timezone, then convert to UTC for query
    const startOfDay = new Date(`${dateStr}T00:00:00+02:00`);
    const endOfDay = new Date(`${dateStr}T23:59:59+02:00`);
    const startUTC = startOfDay.toISOString();
    const endUTC = endOfDay.toISOString();
    
    // Use raw query to check if exception overlaps with the schedule date
    // Exception overlaps if: start_time <= end_of_day AND end_time >= start_of_day
    const results = await this.exceptionRepo.query(
      `
      SELECT * FROM agent_adherence_exceptions
      WHERE employee_id = $1
        AND status = $2
        AND start_time <= $3::timestamptz
        AND end_time >= $4::timestamptz
      `,
      [employeeId, 'APPROVED', endUTC, startUTC],
    );

    // Convert raw results to entity objects
    // Note: Database has start_time/end_time, but entity expects exceptionDate
    // We'll use start_time date as exceptionDate for compatibility
    return results.map((row: any) => {
      const startTime = new Date(row.start_time);
      const exceptionDate = new Date(startTime.toISOString().split('T')[0] + 'T00:00:00Z');
      
      return {
        id: row.id,
        employeeId: row.employee_id,
        exceptionType: row.exception_type,
        exceptionDate: exceptionDate,
        status: row.status,
        justification: row.justification || row.description,
        requestedAdjustmentMinutes: row.duration_minutes || null,
        approvedAdjustmentMinutes: row.duration_minutes || null,
        createdBy: row.requested_by,
        reviewedBy: row.approved_by || row.rejected_by,
        reviewNotes: row.rejection_reason,
        reviewedAt: row.approved_at || row.rejected_at,
        createdAt: row.created_at,
        updatedAt: row.updated_at,
      };
    }) as AgentAdherenceException[];
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

    // Check if summary already exists (use raw query for date comparison)
    const existingResults = await this.summaryRepo.query(
      `
      SELECT * FROM agent_adherence_summaries
      WHERE employee_id = $1
        AND schedule_date = $2::date
      LIMIT 1
      `,
      [data.employeeId, dateStr],
    );

    const existing = existingResults.length > 0
      ? await this.summaryRepo.findOne({ where: { id: existingResults[0].id } })
      : null;

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


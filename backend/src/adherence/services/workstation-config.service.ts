import { Injectable, NotFoundException } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, Raw } from 'typeorm';
import { ApplicationClassification } from '../../entities/application-classification.entity';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';
import { EmployeePersonalInfo } from '../../entities/employee-personal-info.entity';
import { AgentSchedule } from '../../entities/agent-schedule.entity';

/**
 * WorkstationConfigService
 * 
 * Provides configuration data to Desktop Agents.
 */
@Injectable()
export class WorkstationConfigService {
  constructor(
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
    @InjectRepository(ApplicationClassification)
    private classificationRepo: Repository<ApplicationClassification>,
    @InjectRepository(EmployeePersonalInfo)
    private employeePersonalInfoRepo: Repository<EmployeePersonalInfo>,
    @InjectRepository(AgentSchedule)
    private agentScheduleRepo: Repository<AgentSchedule>,
  ) {}

  /**
   * Get workstation configuration
   * 
   * @param workstationId - Workstation ID
   * @param nt - Optional Windows NT account (sam_account_name) for break schedule resolution
   */
  async getWorkstationConfig(workstationId: string, nt?: string) {
    const workstation = await this.workstationRepo.findOne({
      where: { workstationId },
    });

    if (!workstation) {
      throw new NotFoundException('Workstation not found');
    }

    // Get active application classifications
    const classifications = await this.classificationRepo.find({
      where: { isActive: true },
      order: { priority: 'DESC' },
    });

    // Load break schedules if NT account is provided
    let breakSchedules: Array<{
      id: string;
      start_time: string;
      end_time: string;
      break_duration_minutes: number;
    }> = [];

    if (nt) {
      try {
        // Resolve employee_id from NT account
        const personalInfo = await this.employeePersonalInfoRepo.findOne({
          where: { nt },
        });

        if (personalInfo?.employeeId) {
          // Query break schedules for today
          // Use raw SQL to set timezone and get today's date explicitly
          // This ensures we always use Egypt timezone (+02:00) regardless of connection settings
          const result = await this.agentScheduleRepo.query(
            `
            SET LOCAL timezone = '+02:00';
            SELECT * FROM agent_schedules
            WHERE employee_id = $1
              AND schedule_date = CURRENT_DATE
              AND is_break = $2
              AND is_confirmed = $3
            ORDER BY shift_start ASC
            `,
            [personalInfo.employeeId, true, true],
          );

          // Convert raw results to entity objects
          const schedules = result.map((row: any) => ({
            id: row.id,
            employeeId: row.employee_id,
            scheduleDate: row.schedule_date,
            shiftStart: row.shift_start,
            shiftEnd: row.shift_end,
            isBreak: row.is_break,
            breakDuration: row.break_duration,
            isConfirmed: row.is_confirmed,
          }));

          // Debug logging
          console.log(
            `[BreakSchedule] Query for employee ${personalInfo.employeeId}: Found ${schedules.length} schedules`,
          );
          if (schedules.length > 0) {
            schedules.forEach((s) => {
              console.log(
                `  - Schedule ${s.id}: date=${s.scheduleDate}, start=${s.shiftStart}, end=${s.shiftEnd}`,
              );
            });
          } else {
            console.log(
              `[BreakSchedule] No schedules found. Current date check: SELECT CURRENT_DATE;`,
            );
          }

          // Convert to break schedule format
          breakSchedules = schedules.map((schedule) => {
            // Calculate end time from start time + break duration
            const startTime = this.parseTime(schedule.shiftStart);
            const endTime = new Date(startTime);
            endTime.setMinutes(endTime.getMinutes() + schedule.breakDuration);

            return {
              id: schedule.id,
              start_time: this.formatTime(startTime),
              end_time: this.formatTime(endTime),
              break_duration_minutes: schedule.breakDuration,
            };
          });
        }
      } catch (error) {
        // Log error but don't fail the request - break schedules are optional
        console.warn(`Failed to load break schedules for NT account ${nt}:`, error);
      }
    }

    return {
      workstation_id: workstationId,
      sync_interval_seconds: 60, // Default, can be configured per workstation
      batch_size: 100, // Default batch size
      idle_threshold_minutes: 5, // Default idle threshold
      application_classifications: classifications.map((c) => ({
        name_pattern: c.namePattern,
        path_pattern: c.pathPattern,
        window_title_pattern: c.windowTitlePattern,
        classification: c.classification,
        priority: c.priority,
      })),
      break_schedules: breakSchedules,
    };
  }

  /**
   * Parse time string (HH:mm:ss or HH:mm) to Date object (today)
   */
  private parseTime(timeStr: string): Date {
    const parts = timeStr.split(':');
    const hours = parseInt(parts[0], 10);
    const minutes = parseInt(parts[1], 10);
    const date = new Date();
    date.setHours(hours, minutes, 0, 0);
    return date;
  }

  /**
   * Format Date object to HH:mm:ss string
   */
  private formatTime(date: Date): string {
    const hours = date.getHours().toString().padStart(2, '0');
    const minutes = date.getMinutes().toString().padStart(2, '0');
    const seconds = date.getSeconds().toString().padStart(2, '0');
    return `${hours}:${minutes}:${seconds}`;
  }
}


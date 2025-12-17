import { Injectable, NotFoundException } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { EmployeePersonalInfo } from '../../entities/employee-personal-info.entity';
import { AgentAdherenceSummary } from '../../entities/agent-adherence-summary.entity';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';

/**
 * AgentStatusService
 * 
 * Provides agent-facing status information.
 * Checks NT account mapping and returns adherence data if mapped.
 */
@Injectable()
export class AgentStatusService {
  constructor(
    @InjectRepository(EmployeePersonalInfo)
    private employeePersonalInfoRepo: Repository<EmployeePersonalInfo>,
    @InjectRepository(AgentAdherenceSummary)
    private adherenceSummaryRepo: Repository<AgentAdherenceSummary>,
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
  ) {}

  /**
   * Get agent status by NT account
   * 
   * @param ntAccount - Windows NT account (sam_account_name, e.g., "z.salah.3613")
   * @returns Agent status with workstation info and adherence data
   */
  async getAgentStatusByNt(ntAccount: string) {
    // 1. Check if NT account is mapped in employee_personal_info
    const personalInfo = await this.employeePersonalInfoRepo.findOne({
      where: { nt: ntAccount },
    });

    if (!personalInfo) {
      return {
        ntMapped: false,
        warning: 'Workstation user not mapped—contact admin',
        workstation: null,
        adherence: null,
      };
    }

    const employeeId = personalInfo.employeeId;

    // 2. Get workstation status (device-scoped, not employee-bound)
    // Find most recently seen active workstation
    // Note: Workstations are device-only, so we can't directly link to employee
    // For now, we'll return general workstation status
    const activeWorkstations = await this.workstationRepo.find({
      where: { isActive: true },
      order: { lastSeenAt: 'DESC' },
      take: 1,
    });

    const workstation = activeWorkstations.length > 0 ? activeWorkstations[0] : null;

    // 3. Get today's adherence summary
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    
    const adherence = await this.adherenceSummaryRepo.findOne({
      where: {
        employeeId: employeeId,
        scheduleDate: today,
      },
    });

    return {
      ntMapped: true,
      warning: null,
      workstation: workstation
        ? {
            workstationName: workstation.workstationName,
            lastSeenAt: workstation.lastSeenAt,
            isOnline:
              workstation.lastSeenAt &&
              Date.now() - workstation.lastSeenAt.getTime() < 24 * 60 * 60 * 1000,
          }
        : null,
      adherence: adherence
        ? {
            adherencePercentage: adherence.adherencePercentage
              ? parseFloat(adherence.adherencePercentage.toString())
              : null,
            scheduledMinutes: adherence.scheduledDurationMinutes,
            actualMinutes: adherence.actualDurationMinutes,
            exceptionsCount: adherence.exceptionsCount,
            scheduleDate: adherence.scheduleDate,
          }
        : null,
    };
  }
}

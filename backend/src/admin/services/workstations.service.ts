import { Injectable, ConflictException, NotFoundException, BadRequestException } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, DataSource } from 'typeorm';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';
import { RegisterWorkstationDto } from '../../dto/register-workstation.dto';
import { WorkstationAuthService } from '../../adherence/services/workstation-auth.service';
import { randomUUID } from 'crypto';

/**
 * WorkstationsService
 * 
 * Handles workstation registration and management.
 */
@Injectable()
export class WorkstationsService {
  constructor(
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
    private workstationAuthService: WorkstationAuthService,
    private dataSource: DataSource,
  ) {}

  /**
   * List all workstations with filters
   */
  async listWorkstations(query: any) {
    const { status, employee_id, department, page = 1, limit = 50 } = query;

    const queryBuilder = this.workstationRepo.createQueryBuilder('w');

    if (status === 'ACTIVE') {
      queryBuilder.where('w.isActive = :isActive', { isActive: true });
    } else if (status === 'INACTIVE') {
      queryBuilder.where('w.isActive = :isActive', { isActive: false });
    }

    if (employee_id) {
      queryBuilder.andWhere('w.employeeId = :employeeId', { employeeId: employee_id });
    }

    const skip = (page - 1) * limit;
    const [data, total] = await queryBuilder
      .skip(skip)
      .take(limit)
      .orderBy('w.createdAt', 'DESC')
      .getManyAndCount();

    return {
      data: data.map((w) => ({
        id: w.id,
        workstation_id: w.workstationId,
        employee_id: w.employeeId,
        workstation_name: w.workstationName,
        os_version: w.osVersion,
        agent_version: w.agentVersion,
        is_active: w.isActive,
        last_seen_at: w.lastSeenAt,
        last_sync_at: w.lastSyncAt,
        last_event_at: w.lastEventAt,
        registration_source: w.registrationSource,
        created_at: w.createdAt,
      })),
      pagination: {
        page: Number(page),
        limit: Number(limit),
        total,
        total_pages: Math.ceil(total / limit),
      },
    };
  }

  /**
   * Get registration status dashboard data
   */
  async getRegistrationStatus(query: any) {
    // TODO: Implement with employee joins for full dashboard data
    const total = await this.workstationRepo.count();
    const registered = await this.workstationRepo.count({
      where: { isActive: true },
    });
    const offline = await this.workstationRepo.count({
      where: {
        isActive: true,
        lastSeenAt: null, // Or check if last_seen_at > 24 hours
      },
    });

    return {
      summary: {
        total_agents: total, // TODO: Get from employees table
        registered_agents: registered,
        unregistered_agents: total - registered,
        offline_agents: offline,
        online_agents: registered - offline,
      },
      data: [], // TODO: Implement full list with employee info
      pagination: {
        page: 1,
        limit: 50,
        total: 0,
        total_pages: 0,
      },
    };
  }

  /**
   * Register a new workstation
   * 
   * Workstation registration is now device-only.
   * Employee resolution happens at event ingestion time via NT account (sam_account_name).
   */
  async registerWorkstation(dto: RegisterWorkstationDto) {
    // Generate credentials
    const workstationId = randomUUID();
    const apiKey = this.workstationAuthService.generateApiKey();
    const apiKeyHash = await this.workstationAuthService.hashApiKey(apiKey);

    // Create workstation (device-only, no employee binding)
    const workstation = this.workstationRepo.create({
      workstationId,
      employeeId: undefined, // Device-only registration
      apiKeyHash,
      workstationName: dto.workstation_name,
      osVersion: dto.os_version,
      agentVersion: dto.agent_version,
      isActive: true,
      registrationSource: 'ADMIN',
    });

    await this.workstationRepo.save(workstation);

    // Return credentials (shown once only)
    return {
      workstation_id: workstationId,
      api_key: apiKey, // Plain text - shown once only
      message: 'Workstation registered successfully',
      warning: 'These credentials will not be shown again. Store them securely.',
      note: 'This workstation is device-only. Employee identification happens via NT account at event ingestion.',
    };
  }

  /**
   * Revoke/deactivate workstation
   */
  async revokeWorkstation(workstationId: string, reason?: string) {
    const workstation = await this.workstationRepo.findOne({
      where: { workstationId },
    });

    if (!workstation) {
      throw new NotFoundException('Workstation not found');
    }

    workstation.isActive = false;
    await this.workstationRepo.save(workstation);

    return {
      message: 'Workstation deactivated successfully',
      workstation_id: workstationId,
    };
  }
}

